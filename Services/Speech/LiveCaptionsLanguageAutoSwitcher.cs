#pragma warning disable CA1416
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 环形滑动采样缓冲区，用于实时维护最新 N 秒的 16kHz float 音频数据
/// </summary>
public class AudioSlidingBuffer
{
    private readonly float[] _buffer;
    private int _head = 0;
    private int _count = 0;
    private readonly object _sync = new();

    public AudioSlidingBuffer(int capacity)
    {
        _buffer = new float[capacity];
    }

    public void AddRange(ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                _buffer[_head] = samples[i];
                _head = (_head + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }
        }
    }

    public float[]? GetLatestSnapshot(int minSamples = 32000)
    {
        lock (_sync)
        {
            if (_count < minSamples) return null;
            float[] result = new float[_count];
            int start = (_head - _count + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % _buffer.Length];
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
        }
    }
}

/// <summary>
/// Windows 11 实时字幕语言自动切换管理器 (基于 Ear 音频语种识别模型)
/// 核心精度优化特性：
/// 1. 扩大切片至 3.0 ~ 4.0 秒完整语境：为 Whisper 提供充足的音节与语法特征，杜绝短音频误判；
/// 2. 3:1 三角 FIR 抗混叠低通降采样：消除高频折叠失真，保留纯净清晰的梅尔频谱；
/// 3. 双端点独立流择优：扬声器与麦克风分流缓冲，杜绝音频交叉切碎杂音；
/// 4. 优先加载 base 模型：大幅度提升多国语种特征判别的置信度与精度；
/// 5. 防抖机制：连续 3 次确认不同语种，才自动执行后台字幕语言切换。
/// </summary>
public class LiveCaptionsLanguageAutoSwitcher : IDisposable
{
    private readonly LiveCaptionsService _liveCaptionsService;
    private readonly SettingsService _settingsService;
    private readonly EarLanguageDetector _earDetector = new();

    private bool _isEnabled;
    private int _intervalSeconds = 2;
    private CancellationTokenSource? _workerCts;

    private readonly List<IWaveIn> _activeCaptures = new();

    // 独立维持扬声器流与麦克风流，各容纳 4.0 秒 (64,000 samples)
    private readonly AudioSlidingBuffer _renderSlidingBuffer = new(64000);
    private readonly AudioSlidingBuffer _micSlidingBuffer = new(64000);

    private string? _candidateCode;
    private int _hitCount;
    private const int HitThreshold = 3;

    private string _currentStatusText = "待机中";
    private DateTime _lastRenderAudioTime = DateTime.MinValue;
    private DateTime _lastMicAudioTime = DateTime.MinValue;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsEnabled => _isEnabled;
    public int HitCount => _hitCount;
    public string? CandidateLanguageCode => _candidateCode;
    public int IntervalSeconds => _intervalSeconds;
    public string CurrentStatusText => _currentStatusText;

    public event Action<string, string, int, int>? StatusUpdated; // (statusText, langCode, hitCount, threshold)
    public event Action<string, string>? AutoSwitchTriggered;      // (langCode, langName)

    public LiveCaptionsLanguageAutoSwitcher(LiveCaptionsService liveCaptionsService, SettingsService settingsService)
    {
        _liveCaptionsService = liveCaptionsService;
        _settingsService = settingsService;
    }

    private void UpdateStatus(string statusText, string langCode, int hitCount, int threshold)
    {
        _currentStatusText = statusText;
        StatusUpdated?.Invoke(statusText, langCode, hitCount, threshold);
    }

    public async Task SetEnabledAsync(bool enabled, int intervalSeconds = 2)
    {
        await _lock.WaitAsync();
        try
        {
            _isEnabled = enabled;
            _intervalSeconds = Math.Max(1, intervalSeconds);
            _hitCount = 0;
            _candidateCode = null;

            StopWorkerInternal();

            if (_isEnabled)
            {
                UpdateStatus("正在加载 Ear 音频语种识别模型...", string.Empty, 0, HitThreshold);
                bool ok = await _earDetector.InitializeAsync();
                if (!ok)
                {
                    UpdateStatus("Ear 模型加载失败，请检查 Models 目录", string.Empty, 0, HitThreshold);
                    _isEnabled = false;
                    return;
                }

                await StartWorkerInternalAsync();
            }
            else
            {
                UpdateStatus("自动语种检测已关闭", _settingsService.LiveCaptionsLanguageCode, 0, HitThreshold);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void UpdateInterval(int intervalSeconds)
    {
        _intervalSeconds = Math.Max(1, intervalSeconds);
    }

    public async Task RebindAudioCaptureAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!_isEnabled) return;
            StopWorkerInternal();
            await StartWorkerInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StartWorkerInternalAsync()
    {
        _workerCts = new CancellationTokenSource();
        var token = _workerCts.Token;

        try
        {
            _renderSlidingBuffer.Clear();
            _micSlidingBuffer.Clear();
            _lastRenderAudioTime = DateTime.MinValue;
            _lastMicAudioTime = DateTime.MinValue;

            await InitializeAudioCapturesAsync();

            if (_activeCaptures.Count == 0)
            {
                UpdateStatus("未找到可用音频输入或播放设备", _settingsService.LiveCaptionsLanguageCode, 0, HitThreshold);
                return;
            }

            string modelTag = _earDetector.LoadedModelName.Contains("base", StringComparison.OrdinalIgnoreCase) ? "base高精模型" : "tiny轻量模型";
            UpdateStatus($"Ear [{modelTag}] 实时监听就绪 (3.5秒完整语境切片)...", _settingsService.LiveCaptionsLanguageCode, 0, HitThreshold);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoSwitcher] Audio capture start error: {ex.Message}");
            UpdateStatus($"音频监听启动异常: {ex.Message}", string.Empty, 0, HitThreshold);
            return;
        }

        _ = Task.Run(() => DetectionLoopAsync(token), token);
    }

    private async Task InitializeAudioCapturesAsync()
    {
        string devId = _settingsService.AudioInputDeviceId;
        string procName = _settingsService.TargetAudioProcessName;

        // 1. 指定了进程回路隔离
        if (!string.IsNullOrEmpty(devId) && devId.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
        {
            string target = devId.Substring("process:".Length).Trim();
            if (string.IsNullOrEmpty(target)) target = procName;
            if (string.IsNullOrEmpty(target)) target = "VRChat";

            var plc = await ProcessLoopbackCapture.CreateAsync(target);
            AttachAndStartCapture(plc, isMic: false);
            return;
        }

        var enumerator = new MMDeviceEnumerator();

        // 2. 指定了特定的音频端点设备
        if (!string.IsNullOrEmpty(devId))
        {
            try
            {
                var targetDevice = enumerator.GetDevice(devId);
                if (targetDevice != null)
                {
                    bool isCapture = targetDevice.DataFlow == DataFlow.Capture || AudioDeviceService.IsCaptureEndpoint(targetDevice.ID);
                    IWaveIn cap = isCapture ? new WasapiCapture(targetDevice) : new WasapiLoopbackCapture(targetDevice);
                    AttachAndStartCapture(cap, isMic: isCapture);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSwitcher] Failed to bind specific device: {ex.Message}");
            }
        }

        // 3. 系统默认设备：启用独立双流监听 (扬声器/耳机 Loopback + 默认麦克风 Capture)
        try
        {
            var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultRender != null)
            {
                var loopback = new WasapiLoopbackCapture(defaultRender);
                AttachAndStartCapture(loopback, isMic: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoSwitcher] Failed to attach default render loopback: {ex.Message}");
        }

        try
        {
            var defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (defaultCapture != null)
            {
                var mic = new WasapiCapture(defaultCapture);
                AttachAndStartCapture(mic, isMic: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoSwitcher] Failed to attach default mic capture: {ex.Message}");
        }
    }

    private void AttachAndStartCapture(IWaveIn capture, bool isMic)
    {
        var fmt = capture.WaveFormat;
        capture.DataAvailable += (s, e) =>
        {
            ProcessIncomingAudio(e.Buffer, e.BytesRecorded, fmt, isMic);
        };
        capture.StartRecording();
        _activeCaptures.Add(capture);
    }

    /// <summary>
    /// 带 3 点 FIR 抗混叠低通滤波的直接 PCM 降采样，高保真还原 16kHz 人声梅尔频谱
    /// </summary>
    private void ProcessIncomingAudio(byte[] buffer, int bytesRecorded, WaveFormat format, bool isMic)
    {
        if (bytesRecorded <= 0 || buffer == null) return;

        int sampleRate = format.SampleRate;
        int channels = format.Channels;
        if (channels <= 0 || sampleRate <= 0) return;

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32;
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = bytesPerSample * channels;
        if (frameSize <= 0) return;

        int totalFrames = bytesRecorded / frameSize;
        if (totalFrames <= 0) return;

        float[] resampled;
        int outCount = 0;

        // 针对最常见的 48000Hz 输入，执行精确 3:1 三角 FIR 抗混叠滤波降采样 (0.25*f0 + 0.5*f1 + 0.25*f2)
        if (sampleRate == 48000)
        {
            int targetSamples = totalFrames / 3;
            if (targetSamples <= 0) return;

            resampled = new float[targetSamples];
            for (int i = 0; i < targetSamples; i++)
            {
                int f0 = i * 3;
                int f1 = f0 + 1;
                int f2 = f0 + 2;

                float s0 = ExtractMonoSample(buffer, f0 * frameSize, channels, isFloat, bytesPerSample, bytesRecorded);
                float s1 = ExtractMonoSample(buffer, f1 * frameSize, channels, isFloat, bytesPerSample, bytesRecorded);
                float s2 = ExtractMonoSample(buffer, f2 * frameSize, channels, isFloat, bytesPerSample, bytesRecorded);

                resampled[i] = s0 * 0.25f + s1 * 0.50f + s2 * 0.25f;
            }
            outCount = targetSamples;
        }
        else
        {
            // 通用插值降采样
            double step = (double)sampleRate / 16000.0;
            int targetSamples = (int)(totalFrames / step);
            if (targetSamples <= 0) return;

            resampled = new float[targetSamples];
            for (double frameIdx = 0; frameIdx < totalFrames && outCount < targetSamples; frameIdx += step)
            {
                int f = (int)frameIdx;
                resampled[outCount++] = ExtractMonoSample(buffer, f * frameSize, channels, isFloat, bytesPerSample, bytesRecorded);
            }
        }

        if (isMic)
        {
            _micSlidingBuffer.AddRange(resampled.AsSpan(0, outCount));
            _lastMicAudioTime = DateTime.UtcNow;
        }
        else
        {
            _renderSlidingBuffer.AddRange(resampled.AsSpan(0, outCount));
            _lastRenderAudioTime = DateTime.UtcNow;
        }
    }

    private static float ExtractMonoSample(byte[] buffer, int frameOffset, int channels, bool isFloat, int bytesPerSample, int maxBytes)
    {
        float sum = 0f;
        for (int ch = 0; ch < channels; ch++)
        {
            int offset = frameOffset + ch * bytesPerSample;
            if (offset + bytesPerSample <= maxBytes)
            {
                if (isFloat)
                {
                    sum += BitConverter.ToSingle(buffer, offset);
                }
                else if (bytesPerSample == 2)
                {
                    short s = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                    sum += s / 32768f;
                }
            }
        }
        return sum / channels;
    }

    private static float CalculateRms(float[] samples)
    {
        if (samples == null || samples.Length == 0) return 0f;
        float sum = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private async Task DetectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isEnabled)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);

                var now = DateTime.UtcNow;
                bool isRenderActive = (now - _lastRenderAudioTime).TotalSeconds < 3.5;
                bool isMicActive = (now - _lastMicAudioTime).TotalSeconds < 3.5;

                // 提取至少 2.0 秒 (32,000 采样)，最多 4.0 秒 (64,000 采样) 的高阶完整切片
                float[]? renderSnap = isRenderActive ? _renderSlidingBuffer.GetLatestSnapshot(minSamples: 32000) : null;
                float[]? micSnap = isMicActive ? _micSlidingBuffer.GetLatestSnapshot(minSamples: 32000) : null;

                float renderRms = renderSnap != null ? CalculateRms(renderSnap) : 0f;
                float micRms = micSnap != null ? CalculateRms(micSnap) : 0f;

                // VAD 智能择优：选择能量明显更高、人声特征更强的纯净音频流，绝不交错切碎
                float[]? activeSnapshot = null;
                string streamSourceTag = "音频";
                float bestRms = 0f;

                if (micRms >= renderRms && micRms >= 0.0006f)
                {
                    activeSnapshot = micSnap;
                    streamSourceTag = "麦克风人声";
                    bestRms = micRms;
                }
                else if (renderRms >= 0.0006f)
                {
                    activeSnapshot = renderSnap;
                    streamSourceTag = "扬声器声音";
                    bestRms = renderRms;
                }

                if (activeSnapshot == null)
                {
                    UpdateStatus("Ear 实时监听就绪 (等待声音输入/说话)...", _settingsService.LiveCaptionsLanguageCode, _hitCount, HitThreshold);
                    continue;
                }

                double sliceDurationSecs = Math.Round(activeSnapshot.Length / 16000.0, 1);
                var result = await _earDetector.DetectFromAudioAsync(activeSnapshot);

                if (result == null)
                {
                    UpdateStatus($"Ear 正在分析{streamSourceTag} ({sliceDurationSecs}秒切片，RMS: {bestRms:F4})...", _settingsService.LiveCaptionsLanguageCode, _hitCount, HitThreshold);
                    continue;
                }

                string detectedCode = result.Code;
                string currentCode = _settingsService.LiveCaptionsLanguageCode;

                if (string.Equals(detectedCode, currentCode, StringComparison.OrdinalIgnoreCase))
                {
                    if (_hitCount > 0)
                    {
                        _hitCount = 0;
                        _candidateCode = null;
                    }
                    UpdateStatus($"当前语种稳定: {result.DisplayName} (置信度: {(int)(result.Confidence * 100)}%，{sliceDurationSecs}秒切片)", detectedCode, 0, HitThreshold);
                    continue;
                }

                // 识别到与当前不同的语种
                if (string.Equals(detectedCode, _candidateCode, StringComparison.OrdinalIgnoreCase))
                {
                    _hitCount++;
                }
                else
                {
                    _candidateCode = detectedCode;
                    _hitCount = 1;
                }

                UpdateStatus($"检测到不同语种: {result.DisplayName} (置信度: {(int)(result.Confidence * 100)}%，{sliceDurationSecs}秒切片，命中 {_hitCount}/{HitThreshold} 次)", detectedCode, _hitCount, HitThreshold);

                // 连续达到 3 次阈值，触发自动热切换
                if (_hitCount >= HitThreshold && !string.IsNullOrEmpty(_candidateCode))
                {
                    string targetToSwitch = _candidateCode;
                    _hitCount = 0;
                    _candidateCode = null;

                    UpdateStatus($"已连续 3 次确认语种，正在自动切换字幕至: {result.DisplayName}...", targetToSwitch, 0, HitThreshold);
                    AutoSwitchTriggered?.Invoke(targetToSwitch, result.DisplayName);

                    _ = Task.Run(async () =>
                    {
                        await _liveCaptionsService.SwitchLanguageAsync(targetToSwitch);
                        _settingsService.SetLiveCaptionsLanguageCodeSilent(targetToSwitch);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSwitcher] Loop error: {ex.Message}");
                UpdateStatus($"检测异常: {ex.Message}", _settingsService.LiveCaptionsLanguageCode, _hitCount, HitThreshold);
            }
        }
    }

    private void StopWorkerInternal()
    {
        try
        {
            _workerCts?.Cancel();
            _workerCts?.Dispose();
            _workerCts = null;
        }
        catch { }

        foreach (var cap in _activeCaptures)
        {
            try
            {
                cap.StopRecording();
                cap.Dispose();
            }
            catch { }
        }
        _activeCaptures.Clear();

        _renderSlidingBuffer.Clear();
        _micSlidingBuffer.Clear();
        _lastRenderAudioTime = DateTime.MinValue;
        _lastMicAudioTime = DateTime.MinValue;
    }

    public void Dispose()
    {
        StopWorkerInternal();
        _earDetector.Dispose();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
