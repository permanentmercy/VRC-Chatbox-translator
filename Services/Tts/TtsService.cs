#pragma warning disable CA1416

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VrcChatboxDemo.Services.Synthesis;

namespace VrcChatboxDemo.Services.Tts;

public sealed partial class TtsService : IDisposable
{
    private static readonly Lazy<TtsService> _instance = new(() => new TtsService());
    public static TtsService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private Process? _serverProcess;
    private readonly object _serverProcessLock = new();
    private long _serverGeneration;
    private long _synthesisSequence;
    private long _healthCheckSequence;
    // ResponseHeadersRead 之后 HttpClient.Timeout 不再覆盖正文；逐次读取单独限制空闲时间。
    private readonly TimeSpan _streamReadTimeout = TimeSpan.FromMinutes(5);
    private int _disposeStarted;
    private CancellationTokenSource? _activePlaybackCts;
    private readonly object _playbackLock = new();
    private readonly SemaphoreSlim _playbackQueueLock = new(1, 1);
    private readonly SemaphoreSlim _serverStartLock = new(1, 1);

    /// <summary>当前服务端注册过的权重别名；null 表示服务端不支持变体切换</summary>
    private volatile List<string>? _registeredModelAliases;

    public TtsServerState State { get; private set; } = TtsServerState.Stopped;
    public string StatusMessage { get; private set; } = "TTS 引擎已停止";

    // 健康探测与权重别名注册表（IsModelRegistered / CheckHealthAsync /
    // CheckHealthCoreAsync / RefreshHealthAsync）已移到 TtsService.Health.cs。

    public event Action<TtsServerState, string>? StateChanged;
    public event Action<string, double, long>? SynthesisCompleted;
    public event Action<string>? LogOccurred;
    public event Action<bool>? PlaybackActiveStateChanged;
    public event Action<int, int>? StreamProgressChanged;

    private TtsService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    private void SetState(TtsServerState state, string message)
    {
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(state, message);
        LogOccurred?.Invoke($"[{state}] {message}");
    }

    /// <summary>
    /// 获取当前系统可用的音频播放设备（包括虚拟麦克风跳线与物理耳机）
    /// </summary>
    // 设备相关成员（GetPlaybackDevices / IsVirtualAudioDevice / FindFirstVirtualAudioDevice /
    // ResolveDevices / DisposeDevices）已移到 TtsService.Devices.cs。

    // 权重与音色目录扫描已移入 Core：TtsCatalog（无状态、不读单例配置）。
    // 这里保留实例方法做兼容转发，UI 与内部调用无需改动。

    /// <summary>兼容转发：目录定位见 <see cref="TtsCatalog.ResolvePromptsDir"/>。</summary>
    public static string? ResolvePromptsDir(string? serverScriptPath = null) =>
        TtsCatalog.ResolvePromptsDir(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath);

    /// <summary>兼容转发：音色扫描见 <see cref="TtsCatalog.DiscoverPrompts"/>。</summary>
    public List<TtsPromptOption> DiscoverPrompts(string? serverScriptPath = null) =>
        TtsCatalog.DiscoverPrompts(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath, LogOccurred);

    /// <summary>兼容转发：配套关系判断见 <see cref="TtsCatalog.IsPromptPairedWith"/>。</summary>
    public static bool IsPromptPairedWith(string promptFile, string checkpointFile) =>
        TtsCatalog.IsPromptPairedWith(promptFile, checkpointFile);

    /// <summary>兼容转发：目录定位见 <see cref="TtsCatalog.ResolveCheckpointsDir"/>。</summary>
    public static string? ResolveCheckpointsDir(string? serverScriptPath = null) =>
        TtsCatalog.ResolveCheckpointsDir(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath);

    /// <summary>兼容转发：权重扫描见 <see cref="TtsCatalog.DiscoverModels"/>。</summary>
    public List<TtsModelOption> DiscoverModels(string? serverScriptPath = null) =>
        TtsCatalog.DiscoverModels(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath, LogOccurred);

    // 服务端进程生命周期（StartManagedServerAsync / BuildModelRegistryArgs /
    // StopManagedServer / StopOwnedProcess）已移到 TtsService.Server.cs。

    /// <summary>
    /// 本次请求要用的权重别名。
    ///
    /// **只有服务端明确注册过该别名才返回它**，否则返回 null（让服务端用它自己的默认权重）：
    /// 手工启动的旧版服务端拿到未注册的别名会直接报错，宁可少带一个字段也不能让合成失败。
    /// </summary>
    private string? ResolveRequestModelAlias()
    {
        string selected = SettingsService.Instance.Config.TtsModelName;
        return IsModelRegistered(selected) ? selected : null;
    }

    /// <summary>
    /// 本次请求指定的音色特征。
    ///
    /// 只发**文件名**（不含路径），服务端在它自己的 prompts/ 目录内解析，
    /// 这样客户端不需要知道服务端装在哪，也不存在路径穿越问题。
    /// </summary>
    private static string? ResolveRequestPrompt()
    {
        string prompt = SettingsService.Instance.Config.TtsPromptFile;
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        return Path.GetFileName(prompt);
    }

    private (long Generation, long Sequence) BeginSynthesis(string message, CancellationToken ct)
    {
        lock (_serverProcessLock)
        {
            // 停止时已取消、但尚未运行到此处的播放任务，也不能重新开启一次合成状态。
            if (ct.IsCancellationRequested || Volatile.Read(ref _disposeStarted) != 0)
                return (-1, -1);
            var operation = (_serverGeneration, ++_synthesisSequence);
            SetState(TtsServerState.Synthesizing, message);
            return operation;
        }
    }

    private bool IsCurrentSynthesis((long Generation, long Sequence) operation)
    {
        lock (_serverProcessLock)
        {
            return operation.Generation == _serverGeneration && operation.Sequence == _synthesisSequence &&
                   Volatile.Read(ref _disposeStarted) == 0;
        }
    }

    private bool TrySetSynthesisState((long Generation, long Sequence) operation,
        TtsServerState state, string message, TtsServerState? expectedState = null)
    {
        lock (_serverProcessLock)
        {
            // 检查与写入必须和 Stop 的 generation 更新共用锁，避免检查通过后才被停止。
            if (!IsCurrentSynthesis(operation) || (expectedState.HasValue && State != expectedState.Value))
                return false;
            SetState(state, message);
            return true;
        }
    }

    private async Task<T> ReadStreamBodyAsync<T>(Func<CancellationToken, Task<T>> read,
        (long Generation, long Sequence) operation, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(_streamReadTimeout);
        try
        {
            // WaitAsync 同时约束不响应取消的内容流，超时后由枚举器释放 HTTP 响应和正文。
            return await read(readCts.Token).WaitAsync(readCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            const string message = "流式音频读取超时";
            TrySetSynthesisState(operation, TtsServerState.Error, message);
            // 不能把超时当作 EOF：已播放过部分音频时 PlayStreamAsync 会误报成功。
            throw new TimeoutException(message);
        }
    }

    /// <summary>
    /// 请求 IndexTTS 服务合成语音（免参考音频，纯模型推理）
    /// </summary>
    public async Task<TtsSynthesisResult> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        string rawText = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new TtsSynthesisResult(false, null, 0, 0, 24000, "输入文本为空");
        }

        var cfg = SettingsService.Instance.Config;
        string ttsUrl = cfg.TtsServerEndpoint.TrimEnd('/') + "/tts";

        var operation = BeginSynthesis($"正在合成语音: \"{rawText}\"...", ct);
        if (!IsCurrentSynthesis(operation))
            return new TtsSynthesisResult(false, null, 0, 0, 24000, "合成请求已失效");

        var sw = Stopwatch.StartNew();
        try
        {
            var reqObj = new
            {
                text = rawText,
                audio_prompt = ResolveRequestPrompt(), // 指定音色特征（prompts/ 下的文件名）
                model = ResolveRequestModelAlias()
            };
            string reqJson = JsonSerializer.Serialize(reqObj);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(ttsUrl, content, ct);
            using (resp)
            {
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                {
                    string errDetail = await resp.Content.ReadAsStringAsync(ct);
                    TrySetSynthesisState(operation, TtsServerState.Error, $"TTS 合成失败: HTTP {(int)resp.StatusCode}");
                    return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, errDetail);
                }

                byte[] wavBytes = await resp.Content.ReadAsByteArrayAsync(ct);

                double duration = 0;
                if (resp.Headers.TryGetValues("X-Audio-Duration", out var durValues))
                {
                    double.TryParse(durValues.FirstOrDefault(), out duration);
                }

                int sr = 24000;
                if (resp.Headers.TryGetValues("X-Sample-Rate", out var srValues) &&
                    int.TryParse(srValues.FirstOrDefault(), out int parsedSampleRate) &&
                    parsedSampleRate > 0)
                {
                    // TryParse 失败会把 out 参数写成 0；必须保留默认 24000，不能直接用失败结果覆盖。
                    sr = parsedSampleRate;
                }

                if (!TrySetSynthesisState(operation, TtsServerState.Ready, $"合成成功 (耗时: {sw.ElapsedMilliseconds}ms, 时长: {duration:F2}s)"))
                    return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, sr, "合成请求已失效");
                SynthesisCompleted?.Invoke(rawText, duration, sw.ElapsedMilliseconds);

                return new TtsSynthesisResult(true, wavBytes, duration, sw.ElapsedMilliseconds, sr, null);
            }
        }
        catch (OperationCanceledException)
        {
            TrySetSynthesisState(operation, TtsServerState.Ready, "合成已取消");
            return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, "用户已取消");
        }
        catch (Exception ex)
        {
            TrySetSynthesisState(operation, TtsServerState.Error, $"合成异常: {ex.Message}");
            return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, ex.Message);
        }
    }

    /// <summary>
    /// 请求 IndexTTS 服务进行真正低延迟流式语音合成（POST /tts/stream?format=pcm）
    /// 逐块产出 24kHz 16-bit 单声道 PCM 音频流，首包延迟 ~550ms，全程内存无磁盘 IO
    /// </summary>
    public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string rawText = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            yield break;
        }

        var cfg = SettingsService.Instance.Config;
        string streamUrl = $"{cfg.TtsServerEndpoint.TrimEnd('/')}/tts/stream?format=pcm&first_chunk_frames=16&chunk_frames=16";

        var operation = BeginSynthesis($"正在流式合成语音: \"{rawText}\"...", ct);
        if (!IsCurrentSynthesis(operation)) yield break;

        var reqObj = new
        {
            text = rawText,
            audio_prompt = ResolveRequestPrompt(), // 指定音色特征（prompts/ 下的文件名）
            do_sample = false,           // 贪心解码：2.5倍提速，消除首字出声漫长等待
            model = ResolveRequestModelAlias()
        };
        string reqJson = JsonSerializer.Serialize(reqObj);
        using var request = new HttpRequestMessage(HttpMethod.Post, streamUrl)
        {
            Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp;
        var sw = Stopwatch.StartNew();
        try
        {
            resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException)
        {
            TrySetSynthesisState(operation, TtsServerState.Ready, "流式合成已取消");
            yield break;
        }
        catch (Exception ex)
        {
            TrySetSynthesisState(operation, TtsServerState.Error, $"流式连接异常: {ex.Message}");
            LogOccurred?.Invoke($"[TTS Stream] 请求连接异常: {ex.Message}");
            yield break;
        }

        bool finished = false;
        using (resp)
        {
          try
          {
            if (!resp.IsSuccessStatusCode)
            {
                string errDetail = await ReadStreamBodyAsync(token => resp.Content.ReadAsStringAsync(token), operation, ct);
                TrySetSynthesisState(operation, TtsServerState.Error, $"流式合成失败: HTTP {(int)resp.StatusCode}");
                LogOccurred?.Invoke($"[TTS Stream] 合成失败 (HTTP {(int)resp.StatusCode}): {errDetail}");
                yield break;
            }

            using var stream = await ReadStreamBodyAsync(token => resp.Content.ReadAsStreamAsync(token), operation, ct);
            byte[] readBuffer = new byte[32768]; // 32KB 读取缓冲，减少网络分包与读取碎片
            byte[]? leftover = null; // 保证按 2 字节（int16 sample）对齐
            long totalBytes = 0;
            int chunkCount = 0;
            bool firstChunk = true;
            bool canceled = false;

            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await ReadStreamBodyAsync(
                        token => stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), token).AsTask(), operation, ct);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }

                if (!IsCurrentSynthesis(operation)) yield break;
                if (bytesRead <= 0) break;

                chunkCount++;
                if (firstChunk)
                {
                    firstChunk = false;
                    LogOccurred?.Invoke($"[TTS Stream] 收到首块音频流 (首包耗时: {sw.ElapsedMilliseconds}ms, 大小: {bytesRead}B)");
                }

                // 处理 16-bit PCM 字节对齐（2 字节/样本）
                byte[] toProcess;
                if (leftover != null && leftover.Length > 0)
                {
                    toProcess = new byte[leftover.Length + bytesRead];
                    Buffer.BlockCopy(leftover, 0, toProcess, 0, leftover.Length);
                    Buffer.BlockCopy(readBuffer, 0, toProcess, leftover.Length, bytesRead);
                    leftover = null;
                }
                else
                {
                    toProcess = new byte[bytesRead];
                    Buffer.BlockCopy(readBuffer, 0, toProcess, 0, bytesRead);
                }

                int usableBytes = toProcess.Length - (toProcess.Length % 2);
                if (usableBytes < toProcess.Length)
                {
                    leftover = new byte[toProcess.Length - usableBytes];
                    Buffer.BlockCopy(toProcess, usableBytes, leftover, 0, leftover.Length);
                }

                if (usableBytes > 0)
                {
                    totalBytes += usableBytes;
                    byte[] chunk = (usableBytes == toProcess.Length) ? toProcess : toProcess[..usableBytes];
                    yield return chunk;
                }
            }

            sw.Stop();

            if (canceled || ct.IsCancellationRequested)
            {
                TrySetSynthesisState(operation, TtsServerState.Ready, "流式合成已取消");
                yield break;
            }

            double duration = (double)totalBytes / (24000 * 2);
            if (!TrySetSynthesisState(operation, TtsServerState.Ready, $"流式合成完成 (耗时: {sw.ElapsedMilliseconds}ms, 音频时长: {duration:F2}s, 块数: {chunkCount})"))
                yield break;
            LogOccurred?.Invoke($"[TTS Stream] 流式传输结束: 共 {chunkCount} 块, 总字节: {totalBytes} ({duration:F2}s), 耗时: {sw.ElapsedMilliseconds}ms");
            SynthesisCompleted?.Invoke(rawText, duration, sw.ElapsedMilliseconds);
            finished = true;
          }
          finally
          {
            // 调用方提前放弃枚举（例如 PlayStreamAsync 被取消后不再 MoveNext）时，
            // 枚举器的 finally 仍会执行；这里把状态从 Synthesizing 复位，
            // 避免界面永久停在“正在合成语音”。
            if (!finished)
            {
                TrySetSynthesisState(operation, TtsServerState.Ready, "流式合成已结束", TtsServerState.Synthesizing);
            }
          }
        }
    }

    /// <summary>
    /// 流式推流管线：直接发起 IndexTTS /tts/stream 流式请求，
    /// 收到首个 PCM 音频块立刻唤醒声卡开始推流，后续音频流在后台边生成边播放，
    /// 实现毫秒级（约 500ms）首字出声，全程在内存中完成，不落磁盘。
    /// </summary>
    public async Task<bool> PlayStreamAsync(
        string text,
        string? virtualMicDeviceId = null,
        string? monitorDeviceId = null,
        bool enableMonitor = true,
        float micVolume = 1.0f,
        float monitorVolume = 0.8f,
        Action<int>? onChunkReceived = null,
        CancellationToken ct = default)
    {
        string rawText = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawText)) return false;

        // 等待队列前就登记本请求，停止和后来的请求也能取消尚未开播的任务。
        using var playbackCts = BeginPlayback(ct);
        var token = playbackCts.Token;
        bool acquired = false;
        try
        {
            await _playbackQueueLock.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            PlaybackActiveStateChanged?.Invoke(true);
            return await Task.Run(async () =>
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? micDevice = null;
                MMDevice? monDevice = null;
                var resolved = ResolveDevices(enumerator, virtualMicDeviceId, monitorDeviceId, enableMonitor);
                micDevice = resolved.micDevice;
                monDevice = resolved.monDevice;
                bool isSameDevice = resolved.isSameDevice;

                var inputFormat = new WaveFormat(24000, 16, 1);
                SpeechQueueWaveProvider? bufferSame = null;
                SpeechQueueWaveProvider? bufferMic = null;
                SpeechQueueWaveProvider? bufferMon = null;

                WasapiOut? playerSame = null;
                WasapiOut? playerMic = null;
                WasapiOut? playerMon = null;

                try
                {
                    if (isSameDevice)
                    {
                        float maxVol = Math.Max(micVolume, monitorVolume);
                        if (maxVol > 0.001f && micDevice != null)
                        {
                            bufferSame = new SpeechQueueWaveProvider(inputFormat);
                            using var audioClient = micDevice.CreateAudioClient();
                            var mixFormat = audioClient.MixFormat;
                            ISampleProvider sampleProvider = bufferSame.ToSampleProvider();
                            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);
                            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                            {
                                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
                            }
                            var volProvider = new SoftLimiterSampleProvider(sampleProvider, maxVol);
                            playerSame = new WasapiOut(micDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
                            playerSame.Init(volProvider);
                        }
                    }
                    else
                    {
                        if (micDevice != null && micVolume > 0.001f)
                        {
                            bufferMic = new SpeechQueueWaveProvider(inputFormat);
                            using var audioClient = micDevice.CreateAudioClient();
                            var mixFormat = audioClient.MixFormat;
                            ISampleProvider sampleProvider = bufferMic.ToSampleProvider();
                            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);
                            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                            {
                                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
                            }
                            var volProvider = new SoftLimiterSampleProvider(sampleProvider, micVolume);
                            playerMic = new WasapiOut(micDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
                            playerMic.Init(volProvider);
                        }

                        if (enableMonitor && monDevice != null && monitorVolume > 0.001f)
                        {
                            bufferMon = new SpeechQueueWaveProvider(inputFormat);
                            using var audioClient = monDevice.CreateAudioClient();
                            var mixFormat = audioClient.MixFormat;
                            ISampleProvider sampleProvider = bufferMon.ToSampleProvider();
                            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);
                            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                            {
                                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
                            }
                            var volProvider = new SoftLimiterSampleProvider(sampleProvider, monitorVolume);
                            playerMon = new WasapiOut(monDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
                            playerMon.Init(volProvider);
                        }
                    }

                    // 没有任何可用输出端点时，音频块会被静默丢弃，但旧逻辑仍会
                    // 根据 totalBufferedBytes 把 startedPlayback 置 true，向 UI 谎报“推流完成”。
                    bool hasOutputEndpoint = playerSame != null || playerMic != null || playerMon != null;
                    if (!hasOutputEndpoint)
                    {
                        LogOccurred?.Invoke("未找到可用输出设备，或推流/耳返音量均为 0，已取消本次推流。");
                        return false;
                    }

                    bool startedPlayback = false;
                    bool playbackInterrupted = false;
                    int chunkCount = 0;
                    long totalBufferedBytes = 0;
                    // 高响应防欠载预缓冲阈值：
                    // 服务端已配置首块 16 帧 (32,768 字节，约 683ms 音频) + 贪心解码。
                    // 后续块为 16 帧 (约 683ms 音频)，在 GPU 上约需 ~520ms 生成完毕。
                    // 首块的 683ms 音频能够充裕覆盖次块的 520ms 计算耗时（保留 +160ms 安全余量）。
                    // 将预防水位设为 20,000 字节（约 0.42 秒音频）：首块一到达立即唤醒声卡推流，
                    // 实现极速首字出声的同时彻底杜绝开头播完首包后的卡顿/断音。
                    const int PRE_BUFFER_BYTES = 20000;

                    await foreach (var chunk in SynthesizeStreamAsync(rawText, token))
                    {
                        if (token.IsCancellationRequested) break;
                        chunkCount++;
                        totalBufferedBytes += chunk.Length;

                        // 送入音频缓冲区
                        if (isSameDevice && bufferSame != null)
                        {
                            bufferSame.AddSamples(chunk, 0, chunk.Length);
                        }
                        else
                        {
                            if (bufferMic != null) bufferMic.AddSamples(chunk, 0, chunk.Length);
                            if (bufferMon != null) bufferMon.AddSamples(chunk, 0, chunk.Length);
                        }

                        // 收到首块（达到预防水位）立即唤醒声卡推流，实现 ~450ms 极速首字出声
                        if (!startedPlayback && totalBufferedBytes >= PRE_BUFFER_BYTES)
                        {
                            startedPlayback = true;
                            if (isSameDevice)
                            {
                                playerSame?.Play();
                            }
                            else
                            {
                                playerMic?.Play();
                                playerMon?.Play();
                            }
                        }

                        onChunkReceived?.Invoke(chunkCount);
                        StreamProgressChanged?.Invoke(chunkCount, 0);
                    }

                    // 短文本兜底：若整段文本总音频较短（如短语/单字，全句总长不足 0.4 秒），在流传输结束 (EOF) 时立即唤醒声卡播完
                    if (!startedPlayback && totalBufferedBytes > 0 && !token.IsCancellationRequested)
                    {
                        startedPlayback = true;
                        if (isSameDevice)
                        {
                            playerSame?.Play();
                        }
                        else
                        {
                            playerMic?.Play();
                            playerMon?.Play();
                        }
                    }

                    // 全部流式音频接收完毕，等待缓冲区全部音频彻底播放完毕
                    if (startedPlayback)
                    {
                        // 声卡被独占/设备被拔出时 WasapiOut 会自行停止，缓冲区里的 PCM 再也不会被排空。
                        // 旧实现只等 BufferedBytes 归零：单设备还好，双设备（推流+耳返）里只要有一个
                        // 播放器还活着就永远不会判定失败，于是死循环并一直占着播放队列。
                        while (!token.IsCancellationRequested)
                        {
                            bool micPending = bufferMic != null && bufferMic.BufferedBytes > 0;
                            bool monPending = bufferMon != null && bufferMon.BufferedBytes > 0;
                            bool samePending = bufferSame != null && bufferSame.BufferedBytes > 0;

                            if (isSameDevice)
                            {
                                if (!samePending) break;
                                if (playerSame?.PlaybackState != PlaybackState.Playing)
                                {
                                    playbackInterrupted = true;
                                    break;
                                }
                            }
                            else
                            {
                                if (!micPending && !monPending) break;

                                bool micStalled = micPending && playerMic?.PlaybackState != PlaybackState.Playing;
                                bool monStalled = monPending && playerMon?.PlaybackState != PlaybackState.Playing;
                                if (micStalled || monStalled)
                                {
                                    playbackInterrupted = true;
                                    break;
                                }
                            }

                            await Task.Delay(30, token);
                        }

                        if (playbackInterrupted)
                        {
                            LogOccurred?.Invoke("输出设备已停止播放（被独占或已拔出），已中止本次推流。");
                        }

                        await Task.Delay(150, token); // 硬件延迟余音淡出
                    }

                    // 用户点了“停止播放”后，合成流会被取消，但已播放的块数可能 >0。
                    // 不能把这种情况当成成功返回，否则 UI 会误报“推流完成”。
                    if (token.IsCancellationRequested) return false;
                    if (playbackInterrupted) return false;

                    return startedPlayback;
                }
                finally
                {
                    try { playerSame?.Stop(); playerSame?.Dispose(); bufferSame?.ClearBuffer(); } catch { }
                    try { playerMic?.Stop(); playerMic?.Dispose(); bufferMic?.ClearBuffer(); } catch { }
                    try { playerMon?.Stop(); playerMon?.Dispose(); bufferMon?.ClearBuffer(); } catch { }
                    DisposeDevices(micDevice, monDevice);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"流式推流异常: {ex.Message}");
            return false;
        }
        finally
        {
            EndPlayback(playbackCts);
            if (acquired)
            {
                try { PlaybackActiveStateChanged?.Invoke(false); }
                finally { _playbackQueueLock.Release(); }
            }
        }
    }

    /// <summary>
    /// 兼容重载：支持接收分句列表，拼接后以单一流式请求完成高效推理
    /// </summary>
    public Task<bool> PlayStreamAsync(
        IReadOnlyList<string> clauses,
        string? virtualMicDeviceId = null,
        string? monitorDeviceId = null,
        bool enableMonitor = true,
        float micVolume = 1.0f,
        float monitorVolume = 0.8f,
        Action<int, int>? onClauseSynthesized = null,
        Action<int, int>? onClausePlaying = null,
        CancellationToken ct = default)
    {
        if (clauses == null || clauses.Count == 0) return Task.FromResult(false);
        string combined = string.Join(" ", clauses);
        return PlayStreamAsync(
            combined,
            virtualMicDeviceId,
            monitorDeviceId,
            enableMonitor,
            micVolume,
            monitorVolume,
            onChunkReceived: onClauseSynthesized != null ? (c) => onClauseSynthesized(c, 0) : null,
            ct);
    }

    /// <summary>
    /// 播放音频字节流到指定的虚拟麦克风设备与耳返监听设备
    /// </summary>
    public async Task PlayAudioAsync(
        byte[] wavBytes,
        string? virtualMicDeviceId = null,
        string? monitorDeviceId = null,
        bool enableMonitor = true,
        float micVolume = 1.0f,
        float monitorVolume = 0.8f,
        CancellationToken ct = default)
    {
        if (wavBytes == null || wavBytes.Length == 0) return;

        using var playbackCts = BeginPlayback(ct);
        var token = playbackCts.Token;

        try
        {
        await Task.Run(async () =>
        {
            MMDevice? micDevice = null;
            MMDevice? monDevice = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var resolved = ResolveDevices(enumerator, virtualMicDeviceId, monitorDeviceId, enableMonitor);
                micDevice = resolved.micDevice;
                monDevice = resolved.monDevice;
                bool isSameDevice = resolved.isSameDevice;

                var tasks = new List<Task>();

                // 【核心防电音防干涉保护】：已由 ResolveDevices 统一计算 isSameDevice
                if (isSameDevice)
                {
                    float maxVol = Math.Max(micVolume, monitorVolume);
                    if (maxVol > 0.001f && micDevice != null)
                    {
                        tasks.Add(PlayToSingleEndpointAsync(wavBytes, micDevice, maxVol, token));
                    }
                }
                else
                {
                    if (micDevice != null && micVolume > 0.001f)
                    {
                        tasks.Add(PlayToSingleEndpointAsync(wavBytes, micDevice, micVolume, token));
                    }

                    if (enableMonitor && monDevice != null && monitorVolume > 0.001f)
                    {
                        tasks.Add(PlayToSingleEndpointAsync(wavBytes, monDevice, monitorVolume, token));
                    }
                }

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogOccurred?.Invoke($"音频推流播放异常: {ex.Message}");
            }
            finally
            {
                DisposeDevices(micDevice, monDevice);
            }
        }, token);
        }
        catch (OperationCanceledException) { }
        finally { EndPlayback(playbackCts); }
    }

    /// <summary>
    /// 把单声道 TTS 音频对齐到目标设备的原生声道数。
    /// 只处理 2 声道时，5.1/7.1 设备（HDMI、虚拟 7.1 耳机）会保留单声道格式，
    /// 共享模式 WasapiOut 初始化失败，整段 TTS 静音；这里用 MultiplexingSampleProvider
    /// 把单声道复制到前 L/R，其余声道静音。
    /// </summary>
    private static ISampleProvider MatchDeviceChannelCount(ISampleProvider source, WaveFormat mixFormat)
    {
        if (mixFormat.Channels <= 0 || source.WaveFormat.Channels == mixFormat.Channels)
        {
            return source;
        }

        if (source.WaveFormat.Channels == 1 && mixFormat.Channels == 2)
        {
            return new MonoToStereoSampleProvider(source);
        }

        if (source.WaveFormat.Channels == 1 && mixFormat.Channels > 2)
        {
            var multiplex = new MultiplexingSampleProvider(new[] { source }, mixFormat.Channels);
            multiplex.ConnectInputToOutput(0, 0);
            multiplex.ConnectInputToOutput(0, 1);
            return multiplex;
        }

        return source;
    }

    private async Task PlayToSingleEndpointAsync(
        byte[] wavBytes,
        MMDevice targetDevice,
        float volume,
        CancellationToken token)
    {
        try
        {
            if (targetDevice == null) return;

            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);

            // 获取目标声卡的原生混音格式（通常为 48000Hz 2声道 IEEE Float）
            using var audioClient = targetDevice.CreateAudioClient();
            var mixFormat = audioClient.MixFormat;
            ISampleProvider sampleProvider = reader.ToSampleProvider();

            // 1. 声道对齐：若目标为双声道（立体声）而源音频为单声道，平滑复制到左右声道，防止驱动单声道升采样抖动
            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);

            // 2. 采样率平滑重采样：若目标采样率与 24000Hz 不一致（如 48000Hz 或 44100Hz）
            // 使用高质量多相带通重采样器（WdlResamplingSampleProvider），杜绝驱动粗暴线性插值产生的高频数码电音
            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
            {
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
            }

            // 3. 防爆音软削顶音量调节（Soft Limiter），防止音量过大产生硬截断方波杂音
            var volumeProvider = new SoftLimiterSampleProvider(sampleProvider, volume);

            using var wasapiOut = new WasapiOut(targetDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
            wasapiOut.Init(volumeProvider);
            wasapiOut.Play();

            while (wasapiOut.PlaybackState == PlaybackState.Playing && !token.IsCancellationRequested)
            {
                await Task.Delay(30, token);
            }

            wasapiOut.Stop();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"播放端点输出异常 ({targetDevice.FriendlyName}): {ex.Message}");
        }
    }

    private CancellationTokenSource BeginPlayback(CancellationToken ct)
    {
        lock (_playbackLock)
        {
            _activePlaybackCts?.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (Volatile.Read(ref _disposeStarted) != 0) request.Cancel();
            _activePlaybackCts = request;
            return request;
        }
    }

    private void EndPlayback(CancellationTokenSource request)
    {
        lock (_playbackLock)
        {
            if (ReferenceEquals(_activePlaybackCts, request)) _activePlaybackCts = null;
        }
        // CTS 由发起请求的 using 释放，不能在其仍等待/使用 Token 时由 Stop 释放。
    }

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            _activePlaybackCts?.Cancel();
            _activePlaybackCts = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        StopManagedServer();
        _httpClient.Dispose();
    }
}

// 播放侧的两个 Provider（软限幅 / 无界流式队列）已移入 Core：
// Services/Synthesis/PlaybackProviders.cs

