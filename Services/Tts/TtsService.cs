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

    // 其余播放成员（PlayStreamAsync 重载 / PlayAudioAsync / MatchDeviceChannelCount /
    // PlayToSingleEndpointAsync / BeginPlayback / EndPlayback / StopPlayback）
    // 已移到 TtsService.Playback.cs。

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        StopManagedServer();
        _httpClient.Dispose();
    }
}

// 播放侧的两个 Provider（软限幅 / 无界流式队列）已移入 Core：
// Services/Synthesis/PlaybackProviders.cs

