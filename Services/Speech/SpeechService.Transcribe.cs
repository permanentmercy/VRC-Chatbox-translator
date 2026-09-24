using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace VrcChatboxDemo.Services;

public partial class SpeechService
{
    private enum SpeechQueueItemType
    {
        Hypothesis,
        FinalSentence
    }

    private record SpeechQueueItem(SpeechQueueItemType Type, byte[] PcmBytes, long SequenceId);

    private Channel<SpeechQueueItem>? _speechChannel;
    private long _sequenceCounter = 0;

    /// <summary>
    /// 直接转写本地音频文件
    /// </summary>
    public async Task<string> TranscribeFileAsync(string filePath, string? languageTag = null)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, filePath);
                if (File.Exists(candidate))
                {
                    filePath = candidate;
                }
                else
                {
                    var projCandidate = Path.Combine(@"H:\program\chatbox\VrcChatboxDemo", filePath);
                    if (File.Exists(projCandidate))
                    {
                        filePath = projCandidate;
                    }
                    else
                    {
                        throw new FileNotFoundException($"找不到指定的测试音频文件: {filePath}");
                    }
                }
            }

            StatusChanged?.Invoke($"正在转写音频文件: {Path.GetFileName(filePath)}...");
            await EnsureProcessorAsync(languageTag);

            using var memStream = new MemoryStream();
            using (var reader = new AudioFileReader(filePath))
            {
                var resampler = new WdlResamplingSampleProvider(reader, 16000);
                var mono = resampler.ToMono();
                var wave16 = new SampleToWaveProvider16(mono);

                using var writer = new WaveFileWriter(memStream, new WaveFormat(16000, 16, 1));
                var buffer = new byte[4096];
                int read;
                while ((read = wave16.Read(buffer.AsSpan())) > 0)
                {
                    writer.Write(buffer, 0, read);
                }
            }

            memStream.Position = 0;

            var sb = new StringBuilder();
            if (_processor != null)
            {
                await _transcribeLock.WaitAsync();
                try
                {
                    await foreach (var segment in _processor.ProcessAsync(memStream))
                    {
                        if (!string.IsNullOrWhiteSpace(segment.Text))
                        {
                            var segText = CleanWhisperText(segment.Text);
                            if (!string.IsNullOrWhiteSpace(segText))
                            {
                                sb.Append(segText);
                                SpeechHypothesis?.Invoke(sb.ToString());
                            }
                        }
                    }
                }
                finally
                {
                    _transcribeLock.Release();
                }
            }

            string result = CleanWhisperText(sb.ToString());
            StatusChanged?.Invoke($"音频文件识别完成: {result}");
            if (!string.IsNullOrWhiteSpace(result))
            {
                SpeechRecognized?.Invoke(result);
            }
            return result;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"音频文件识别失败: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// GPU 算力调度队列消费循环：
    /// 严格保证所有识别任务按顺序单线程进入 GPU 进行 Whisper 推理计算；
    /// 根据 GpuPerformanceMode (高性能/均衡模式/低负载) 精确控制推理间隔冷却与线程调度优先级，杜绝 GPU 瞬时高占用导致游戏卡顿。
    /// </summary>
    private async Task ProcessSentenceQueueLoopAsync(CancellationToken ct)
    {
        var channel = _speechChannel;
        if (channel == null) return;

        try
        {
            while (!ct.IsCancellationRequested && await channel.Reader.WaitToReadAsync(ct))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    if (ct.IsCancellationRequested) break;

                    // 若为中间假说，且队列后方已有更新的任务（无论假说还是终句），则跳过旧假说以消除无谓的 GPU 冲顶
                    if (item.Type == SpeechQueueItemType.Hypothesis && channel.Reader.TryPeek(out _))
                    {
                        continue;
                    }

                    // 低负载模式下将工作线程优先级降至 BelowNormal，优先保障 VRChat / 3D 游戏画面的 GPU 算力
                    var prevPriority = Thread.CurrentThread.Priority;
                    if (PerformanceMode == GpuPerformanceMode.Low)
                    {
                        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                    }

                    try
                    {
                        if (item.Type == SpeechQueueItemType.Hypothesis)
                        {
                            await TranscribeHypothesisAsync(item.PcmBytes, ct);
                        }
                        else
                        {
                            await TranscribeFinalSentenceAsync(item.PcmBytes, ct);
                        }
                    }
                    finally
                    {
                        if (PerformanceMode == GpuPerformanceMode.Low)
                        {
                            try { Thread.CurrentThread.Priority = prevPriority; } catch { }
                        }
                    }

                    // GPU 算力节奏冷却保护 (Pacing Cooldown)
                    int cooldownMs = PerformanceMode switch
                    {
                        GpuPerformanceMode.Low => 85,
                        GpuPerformanceMode.Medium => 25,
                        _ => 0
                    };

                    if (cooldownMs > 0 && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(cooldownMs, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpeechQueue] Loop error: {ex.Message}");
        }
    }

    /// <summary>
    /// 实时流式人声检测与滚动假说处理循环（模仿 Windows 实时字幕：边说边出字、动态自我纠偏）
    /// </summary>
    private async Task ProcessLiveAudioLoopAsync(CancellationToken ct)
    {
        if (_wave16 == null) return;

        var chunkBuffer = new byte[2560]; // 80ms 采样块
        using var speechStream = new MemoryStream();
        var bufferLock = new object();
        bool inSpeech = false;
        var lastVoiceTime = Stopwatch.StartNew();
        var speechStartTime = Stopwatch.StartNew();
        var lastHypothesisTime = Stopwatch.StartNew();

        const double voiceThresholdRms = 30.0;     // 灵敏度门限 (Windows数字静音通常为 0~5)
        const int silenceTimeoutMs = 650;          // 停顿 650ms 断句确认

        try
        {
            while (!ct.IsCancellationRequested && _isListening)
            {
                int sliceSecs = Math.Clamp(SliceDurationSeconds, 2, 30);
                int maxSpeechBytes = 16000 * 2 * sliceSecs;
                int maxSpeechMs = sliceSecs * 1000;

                int bytesRead = _wave16.Read(chunkBuffer.AsSpan());
                double rms = 0;

                if (bytesRead > 0)
                {
                    int sampleCount = bytesRead / 2;
                    double sum = 0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = (short)(chunkBuffer[i * 2] | (chunkBuffer[i * 2 + 1] << 8));
                        sum += sample * sample;
                    }
                    rms = Math.Sqrt(sum / sampleCount);
                }

                bool isVoice = rms >= voiceThresholdRms;

                if (isVoice)
                {
                    if (!inSpeech)
                    {
                        inSpeech = true;
                        speechStartTime.Restart();
                        lastHypothesisTime.Restart();
                        StatusChanged?.Invoke("正在听写...");
                    }
                    lastVoiceTime.Restart();
                    lock (bufferLock)
                    {
                        speechStream.Write(chunkBuffer, 0, bytesRead);
                    }

                    // 实时滚动假说：根据当前 GPU 负载模式动态调节采样推断频率
                    int hypothesisIntervalMs = PerformanceMode switch
                    {
                        GpuPerformanceMode.Low => 1200,
                        GpuPerformanceMode.Medium => 550,
                        _ => 280
                    };

                    if (speechStream.Length >= 16000 * 2 * 0.32 && lastHypothesisTime.ElapsedMilliseconds >= hypothesisIntervalMs)
                    {
                        byte[] snapshot;
                        lock (bufferLock)
                        {
                            snapshot = speechStream.ToArray();
                        }
                        lastHypothesisTime.Restart();

                        long seq = Interlocked.Increment(ref _sequenceCounter);
                        _speechChannel?.Writer.TryWrite(new SpeechQueueItem(SpeechQueueItemType.Hypothesis, snapshot, seq));
                    }
                }
                else
                {
                    if (inSpeech)
                    {
                        if (bytesRead > 0)
                        {
                            lock (bufferLock)
                            {
                                speechStream.Write(chunkBuffer, 0, bytesRead);
                            }
                        }

                        // 句子结束判定：停顿超过 650ms 或达到用户自定义的语音切片时限
                        if (lastVoiceTime.ElapsedMilliseconds >= silenceTimeoutMs ||
                            speechStream.Length >= maxSpeechBytes ||
                            speechStartTime.ElapsedMilliseconds >= maxSpeechMs)
                        {
                            byte[] pcmBytes;
                            lock (bufferLock)
                            {
                                pcmBytes = speechStream.ToArray();
                                speechStream.SetLength(0);
                            }
                            inSpeech = false;

                            if (pcmBytes.Length >= 16000 * 2 * 0.2)
                            {
                                long seq = Interlocked.Increment(ref _sequenceCounter);
                                _speechChannel?.Writer.TryWrite(new SpeechQueueItem(SpeechQueueItemType.FinalSentence, pcmBytes, seq));
                            }
                        }
                    }
                }

                await Task.Delay(15, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"语音循环异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 异步计算实时中间假说（非阻塞式快速推断，字词递进与自动纠偏）
    /// </summary>
    private async Task TranscribeHypothesisAsync(byte[] pcmBytes, CancellationToken ct)
    {
        if (_processor == null || pcmBytes.Length < 16000 * 2 * 0.15) return;

        // 若前序推断未结束则主动让步，避免队列积压
        if (!await _transcribeLock.WaitAsync(0, ct)) return;
        try
        {
            var proc = _processor;
            if (proc == null) return;

            using var wavStream = new MemoryStream();
            using (var writer = new WaveFileWriter(wavStream, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(pcmBytes, 0, pcmBytes.Length);
            }
            wavStream.Position = 0;

            var sb = new StringBuilder();
            await foreach (var segment in proc.ProcessAsync(wavStream, ct))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    var clean = CleanWhisperText(segment.Text);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        sb.Append(clean);
                    }
                }
            }

            string hypothesis = CleanWhisperText(sb.ToString());
            if (!string.IsNullOrWhiteSpace(hypothesis))
            {
                hypothesis = ConvertChineseVariant(hypothesis, ChineseVariant);
                SpeechHypothesis?.Invoke(hypothesis);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            _transcribeLock.Release();
        }
    }

    /// <summary>
    /// 语句结束时的最终确认转写
    /// </summary>
    private async Task TranscribeFinalSentenceAsync(byte[] pcmBytes, CancellationToken ct)
    {
        if (pcmBytes.Length == 0) return;

        await _transcribeLock.WaitAsync(ct);
        try
        {
            var proc = _processor;
            if (proc == null) return;

            using var wavStream = new MemoryStream();
            using (var writer = new WaveFileWriter(wavStream, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(pcmBytes, 0, pcmBytes.Length);
            }
            wavStream.Position = 0;

            var sb = new StringBuilder();
            await foreach (var segment in proc.ProcessAsync(wavStream, ct))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    var clean = CleanWhisperText(segment.Text);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        sb.Append(clean);
                    }
                }
            }

            string finalResult = CleanWhisperText(sb.ToString());
            if (!string.IsNullOrWhiteSpace(finalResult))
            {
                finalResult = ConvertChineseVariant(finalResult, ChineseVariant);
                SpeechRecognized?.Invoke(finalResult);
                StatusChanged?.Invoke($"已识别: {finalResult}");
            }
            else
            {
                StatusChanged?.Invoke($"Whisper AI 实时字幕监听中 ({CurrentLanguageName})...");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"转写错误: {ex.Message}");
        }
        finally
        {
            _transcribeLock.Release();
        }
    }

    private static string CleanWhisperText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = text.Replace("\r", "").Replace("\n", " ").Trim();
        if (text.StartsWith("[") && text.EndsWith("]")) return string.Empty;
        if (text.StartsWith("(") && text.EndsWith(")")) return string.Empty;
        if (text.Equals("you", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (text.Equals("Thank you.", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (text.Equals("谢谢。", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return text;
    }
}
