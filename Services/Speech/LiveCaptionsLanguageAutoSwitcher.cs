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

    public float[]? GetLatestSnapshot(int minSamples = 8000)
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
/// 核心特性：
/// 1. 采用 Ear 声学轻量识别模型，直接对音频流抽取特征，即使当前字幕语言完全听不懂也能准确判别；
/// 2. 高性能直接 PCM 线性降采样至 16kHz float，杜绝任何外部 Provider 套娃阻塞；
/// 3. 支持系统默认智能双流监听 (同时监听扬声器 Loopback 游戏/视频声音 + 麦克风用户说话声音)；
/// 4. 环形滑动窗口零积压零延迟，实时反映当前人声状态与语种判定；
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
    private readonly AudioSlidingBuffer _slidingBuffer = new(48000); // 3 秒 16kHz float

    private string? _candidateCode;
    private int _hitCount;
    private const int HitThreshold = 3;

    private string _currentStatusText = "待机中";
    private DateTime _lastAudioTime = DateTime.MinValue;
    private long _totalSamplesReceived = 0;

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
            _slidingBuffer.Clear();
            _totalSamplesReceived = 0;
            _lastAudioTime = DateTime.MinValue;

            await InitializeAudioCapturesAsync();

            if (_activeCaptures.Count == 0)
            {
                UpdateStatus("未找到可用音频输入或播放设备", _settingsService.LiveCaptionsLanguageCode, 0, HitThreshold);
                return;
            }

            UpdateStatus("Ear 实时监听就绪 (等待声音输入/说话)...", _settingsService.LiveCaptionsLanguageCode, 0, HitThreshold);
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
            AttachAndStartCapture(plc);
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
                    AttachAndStartCapture(cap);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSwitcher] Failed to bind specific device: {ex.Message}");
            }
        }

        // 3. 系统默认设备：启用智能双流监听 (扬声器/耳机 Loopback + 默认麦克风 Capture)
        // 这样他人发声与自己说话均能无缝送入语种分析模型，彻底解决单端点静默问题
        try
        {
            var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultRender != null)
            {
                var loopback = new WasapiLoopbackCapture(defaultRender);
                AttachAndStartCapture(loopback);
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
                AttachAndStartCapture(mic);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoSwitcher] Failed to attach default mic capture: {ex.Message}");
        }
    }

    private void AttachAndStartCapture(IWaveIn capture)
    {
        var fmt = capture.WaveFormat;
        capture.DataAvailable += (s, e) =>
        {
            ProcessIncomingAudio(e.Buffer, e.BytesRecorded, fmt);
        };
        capture.StartRecording();
        _activeCaptures.Add(capture);
    }

    private void ProcessIncomingAudio(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0 || buffer == null) return;

        int sampleRate = format.SampleRate;
        int channels = format.Channels;
        if (channels <= 0 || sampleRate <= 0) return;

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32;
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = bytesPerSample * channels;
        if (frameSize <= 0) return;

        int frameCount = bytesRecorded / frameSize;
        if (frameCount <= 0) return;

        // 快速线性降采样至 16000Hz (如 48kHz -> 16kHz, step = 3.0)
        double step = (double)sampleRate / 16000.0;
        int targetSamples = (int)(frameCount / step);
        if (targetSamples <= 0) return;

        float[] resampled = new float[targetSamples];
        int outIdx = 0;

        for (double frameIdx = 0; frameIdx < frameCount && outIdx < targetSamples; frameIdx += step)
        {
            int f = (int)frameIdx;
            int frameOffset = f * frameSize;

            float sum = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                int chOffset = frameOffset + ch * bytesPerSample;
                if (chOffset + bytesPerSample <= bytesRecorded)
                {
                    if (isFloat)
                    {
                        sum += BitConverter.ToSingle(buffer, chOffset);
                    }
                    else if (bytesPerSample == 2)
                    {
                        short s = (short)(buffer[chOffset] | (buffer[chOffset + 1] << 8));
                        sum += s / 32768f;
                    }
                }
            }
            resampled[outIdx++] = sum / channels;
        }

        _slidingBuffer.AddRange(resampled.AsSpan(0, outIdx));
        Interlocked.Add(ref _totalSamplesReceived, outIdx);
        _lastAudioTime = DateTime.UtcNow;
    }

    private async Task DetectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isEnabled)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);

                double secondsSinceAudio = (DateTime.UtcNow - _lastAudioTime).TotalSeconds;

                // 提取最近 0.5 ~ 3 秒的真实音频快照
                float[]? snapshot = _slidingBuffer.GetLatestSnapshot(minSamples: 8000);
                if (snapshot == null || secondsSinceAudio > 3.5)
                {
                    UpdateStatus("Ear 实时监听就绪 (等待声音输入/说话)...", _settingsService.LiveCaptionsLanguageCode, _hitCount, HitThreshold);
                    continue;
                }

                float currentRms = 0f;
                var result = await _earDetector.DetectFromAudioAsync(snapshot, rms => currentRms = rms);

                if (result == null)
                {
                    if (currentRms < 0.0035f)
                    {
                        UpdateStatus($"Ear 监听中 (音量偏低 RMS: {currentRms:F3})", _settingsService.LiveCaptionsLanguageCode, _hitCount, HitThreshold);
                    }
                    else
                    {
                        UpdateStatus($"Ear 正在分析声音特征 (音量 RMS: {currentRms:F3})...", _settingsService.LiveCaptionsLanguageCode, _hitCount, HitThreshold);
                    }
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
                    UpdateStatus($"当前语种稳定: {result.DisplayName} (置信度: {(int)(result.Confidence * 100)}%)", detectedCode, 0, HitThreshold);
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

                UpdateStatus($"检测到不同语种: {result.DisplayName} (置信度: {(int)(result.Confidence * 100)}%，命中 {_hitCount}/{HitThreshold} 次)", detectedCode, _hitCount, HitThreshold);

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

        _slidingBuffer.Clear();
        _totalSamplesReceived = 0;
        _lastAudioTime = DateTime.MinValue;
    }

    public void Dispose()
    {
        StopWorkerInternal();
        _earDetector.Dispose();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
