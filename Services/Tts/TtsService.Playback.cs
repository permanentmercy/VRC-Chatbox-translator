using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VrcChatboxDemo.Services.Synthesis;

namespace VrcChatboxDemo.Services.Tts;

/// <summary>
/// TTS 播放管线（分部类，机械拆分）：流式推流到虚拟麦克风 + 耳返监听、
/// 整段 WAV 播放、声道对齐与单端点输出、播放队列的取消。
///
/// 从 TtsService.cs 拆出来的原因：这段是 WASAPI 播放的完整实现（双端点缓冲、
/// 预缓冲水位、欠载保护、设备停止检测、队列串行化），与进程管理、HTTP 合成都无关。
/// 所有字段仍在 TtsService.cs 中（partial 共享），本次不改变任何行为。
/// </summary>
public sealed partial class TtsService
{
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
}
