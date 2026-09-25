#pragma warning disable CA1416
#pragma warning disable CS0618

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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

    public float[]? GetLatestSnapshot(int minSamples = 16000)
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
/// 2. 使用最新音频环形滑动窗口 (Ring Buffer)，杜绝 FIFO 队列积压延迟，每次检测取当下最新的 2.5 秒真实声音；
/// 3. 支持用户自定义定时检测频率 (1s, 2s, 3s, 5s)；
/// 4. 防抖机制：连续 3 次确认不同语种，才自动执行后台字幕语言切换；
/// 5. 实时向上层抛出检测状态、语种名称、音量能量及命中进度 (如: 2/3 次)。
/// </summary>
public class LiveCaptionsLanguageAutoSwitcher : IDisposable
{
    private readonly LiveCaptionsService _liveCaptionsService;
    private readonly EarLanguageDetector _earDetector = new();

    private bool _isEnabled;
    private int _intervalSeconds = 2;
    private CancellationTokenSource? _workerCts;

    private IWaveIn? _audioCapture;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private SampleToWaveProvider16? _wave16;
    private readonly AudioSlidingBuffer _slidingBuffer = new(48000); // 3 秒 16kHz float

    private string? _candidateCode;
    private int _hitCount;
    private const int HitThreshold = 3;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsEnabled => _isEnabled;
    public int HitCount => _hitCount;
    public string? CandidateLanguageCode => _candidateCode;
    public int IntervalSeconds => _intervalSeconds;

    public event Action<string, string, int, int>? StatusUpdated; // (statusText, langCode, hitCount, threshold)
    public event Action<string, string>? AutoSwitchTriggered;      // (langCode, langName)

    public LiveCaptionsLanguageAutoSwitcher(LiveCaptionsService liveCaptionsService)
    {
        _liveCaptionsService = liveCaptionsService;
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
                StatusUpdated?.Invoke("正在加载 Ear 音频语种识别模型...", string.Empty, 0, HitThreshold);
                bool ok = await _earDetector.InitializeAsync();
                if (!ok)
                {
                    StatusUpdated?.Invoke("Ear 模型加载失败，请检查 Models 目录", string.Empty, 0, HitThreshold);
                    _isEnabled = false;
                    return;
                }

                await StartWorkerInternalAsync();
                StatusUpdated?.Invoke("Ear 音频语种自动检测已就绪", SettingsService.Instance.LiveCaptionsLanguageCode, 0, HitThreshold);
            }
            else
            {
                StatusUpdated?.Invoke("自动语种检测已关闭", SettingsService.Instance.LiveCaptionsLanguageCode, 0, HitThreshold);
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
            _audioCapture = await CreateAudioCaptureAsync();

            _bufferedWaveProvider = new BufferedWaveProvider(_audioCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            var sampleProvider = _bufferedWaveProvider.ToSampleProvider();
            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            var mono = resampler.ToMono();
            _wave16 = new SampleToWaveProvider16(mono);

            _slidingBuffer.Clear();

            _audioCapture.DataAvailable += (s, e) =>
            {
                _bufferedWaveProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            _audioCapture.StartRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoSwitcher] Audio capture start error: {ex.Message}");
            StatusUpdated?.Invoke($"音频监听启动异常: {ex.Message}", string.Empty, 0, HitThreshold);
            return;
        }

        _ = Task.Run(() => DetectionLoopAsync(token), token);
    }

    private async Task<IWaveIn> CreateAudioCaptureAsync()
    {
        string devId = SettingsService.Instance.AudioInputDeviceId;
        string procName = SettingsService.Instance.TargetAudioProcessName;

        if (!string.IsNullOrEmpty(devId) && devId.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
        {
            string target = devId.Substring("process:".Length).Trim();
            if (string.IsNullOrEmpty(target)) target = procName;
            if (string.IsNullOrEmpty(target)) target = "VRChat";
            return await ProcessLoopbackCapture.CreateAsync(target);
        }

        var enumerator = new MMDeviceEnumerator();
        MMDevice? targetDevice = null;
        bool isCapture = false;

        if (!string.IsNullOrEmpty(devId))
        {
            try
            {
                targetDevice = enumerator.GetDevice(devId);
            }
            catch { }
        }

        if (targetDevice != null)
        {
            isCapture = targetDevice.DataFlow == DataFlow.Capture || AudioDeviceService.IsCaptureEndpoint(targetDevice.ID);
        }
        else
        {
            try
            {
                targetDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch
            {
                targetDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                isCapture = true;
            }
        }

        if (targetDevice == null)
        {
            return new WasapiLoopbackCapture();
        }

        return isCapture ? new WasapiCapture(targetDevice) : new WasapiLoopbackCapture(targetDevice);
    }

    private async Task DetectionLoopAsync(CancellationToken ct)
    {
        byte[] chunk = new byte[8192];
        float[] convertBuffer = new float[4096];

        while (!ct.IsCancellationRequested && _isEnabled)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);

                if (_wave16 == null) continue;

                // 1. 一次性彻底排空底层 FIFO 缓冲区中积攒的全部音频数据，避免任何旧数据积压
                int bytesRead;
                while ((bytesRead = _wave16.Read(chunk.AsSpan())) > 0)
                {
                    int samplesInChunk = bytesRead / 2;
                    for (int i = 0; i < samplesInChunk; i++)
                    {
                        short s = (short)(chunk[i * 2] | (chunk[i * 2 + 1] << 8));
                        convertBuffer[i] = s / 32768f;
                    }
                    _slidingBuffer.AddRange(convertBuffer.AsSpan(0, samplesInChunk));
                }

                // 2. 提取最近 1.5 ~ 3 秒的真实音频快照
                float[]? snapshot = _slidingBuffer.GetLatestSnapshot(minSamples: 16000);
                if (snapshot == null)
                {
                    continue; // 声音样本不足 1 秒，等待累积
                }

                float currentRms = 0f;
                var result = await _earDetector.DetectFromAudioAsync(snapshot, rms => currentRms = rms);

                if (result == null)
                {
                    if (currentRms < 0.0035f)
                    {
                        StatusUpdated?.Invoke("Ear 监听中 (待机静音/低音量)", SettingsService.Instance.LiveCaptionsLanguageCode, _hitCount, HitThreshold);
                    }
                    continue;
                }

                string detectedCode = result.Code;
                string currentCode = SettingsService.Instance.LiveCaptionsLanguageCode;

                if (string.Equals(detectedCode, currentCode, StringComparison.OrdinalIgnoreCase))
                {
                    if (_hitCount > 0)
                    {
                        _hitCount = 0;
                        _candidateCode = null;
                    }
                    StatusUpdated?.Invoke($"当前语种稳定: {result.DisplayName} (置信度: {(int)(result.Confidence * 100)}%)", detectedCode, 0, HitThreshold);
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

                StatusUpdated?.Invoke($"检测到不同语种: {result.DisplayName} (置信度: {(int)(result.Confidence * 100)}%，命中 {_hitCount}/{HitThreshold} 次)", detectedCode, _hitCount, HitThreshold);

                // 连续达到 3 次阈值，触发自动热切换
                if (_hitCount >= HitThreshold && !string.IsNullOrEmpty(_candidateCode))
                {
                    string targetToSwitch = _candidateCode;
                    _hitCount = 0;
                    _candidateCode = null;

                    StatusUpdated?.Invoke($"已连续 3 次确认语种，正在自动切换字幕至: {result.DisplayName}...", targetToSwitch, 0, HitThreshold);
                    AutoSwitchTriggered?.Invoke(targetToSwitch, result.DisplayName);

                    _ = Task.Run(async () =>
                    {
                        await _liveCaptionsService.SwitchLanguageAsync(targetToSwitch);
                        SettingsService.Instance.SetLiveCaptionsLanguageCodeSilent(targetToSwitch);
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

        try
        {
            _audioCapture?.StopRecording();
            _audioCapture?.Dispose();
            _audioCapture = null;
        }
        catch { }

        _bufferedWaveProvider = null;
        _wave16 = null;
        _slidingBuffer.Clear();
    }

    public void Dispose()
    {
        StopWorkerInternal();
        _earDetector.Dispose();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
