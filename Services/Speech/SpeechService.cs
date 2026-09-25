#pragma warning disable CS0618
#pragma warning disable CA1416

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace VrcChatboxDemo.Services;

public enum GpuPerformanceMode
{
    High,
    Medium,
    Low
}

public record SpeechLanguageInfo(string LanguageTag, string DisplayName);

/// <summary>
/// 现代 Windows 本地 AI 语音识别服务 (基于 OpenAI Whisper 离线神经网络模型)
/// 支持类似 Windows 字幕的极速流式出字、实时假说生成、后文自动纠错
/// </summary>
public partial class SpeechService : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string _currentLanguageTag = "auto";
    private string _currentEndpointId = string.Empty;
    private string _currentModelType = "tiny"; // 默认使用 tiny 获得极速流式响应 (可无缝切换 base, small)
    private bool _isListening;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);
    private readonly SemaphoreSlim _modelSwitchLock = new(1, 1);
    private CancellationTokenSource? _listeningCts;

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private SampleToWaveProvider16? _wave16;

    public bool IsListening => _isListening;
    public string CurrentModelType
    {
        get => _currentModelType;
        set => _currentModelType = value;
    }

    public string CurrentLanguageName { get; private set; } = "Whisper 本地 AI (自动语言检测)";

    public int SliceDurationSeconds { get; set; } = 6;
    public GpuPerformanceMode PerformanceMode { get; set; } = GpuPerformanceMode.High;

    public static GpuPerformanceMode ParseGpuMode(string? mode)
    {
        if (string.Equals(mode, "Low", StringComparison.OrdinalIgnoreCase)) return GpuPerformanceMode.Low;
        if (string.Equals(mode, "Medium", StringComparison.OrdinalIgnoreCase)) return GpuPerformanceMode.Medium;
        return GpuPerformanceMode.High;
    }

    public event Action<string>? SpeechRecognized;
    public event Action<string>? SpeechHypothesis;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<bool>? ListeningStateChanged;

    /// <summary>
    /// 启动实时连续语音识别
    /// </summary>
    public async Task<bool> StartAsync(string? languageTag = null, string? audioEndpointId = null)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_isListening)
            {
                if (string.Equals(_currentLanguageTag, NormalizeLanguageTag(languageTag), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_currentEndpointId, audioEndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                await StopInternalAsync();
            }

            StatusChanged?.Invoke("正在启动 Whisper 模型...");
            await EnsureProcessorAsync(languageTag);

            _currentEndpointId = audioEndpointId ?? string.Empty;
            _listeningCts = new CancellationTokenSource();

            var enumerator = new MMDeviceEnumerator();
            MMDevice? targetDevice = null;

            if (!string.IsNullOrEmpty(audioEndpointId))
            {
                try
                {
                    targetDevice = enumerator.GetDevice(audioEndpointId);
                }
                catch { }
            }

            bool isCapture = false;
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
                throw new InvalidOperationException("未找到可用的系统音频设备");
            }

            if (isCapture)
            {
                _capture = new WasapiCapture(targetDevice);
            }
            else
            {
                _capture = new WasapiLoopbackCapture(targetDevice);
            }

            _bufferedWaveProvider = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            var sampleProvider = _bufferedWaveProvider.ToSampleProvider();
            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            var mono = resampler.ToMono();
            _wave16 = new SampleToWaveProvider16(mono);

            _capture.DataAvailable += (s, e) =>
            {
                _bufferedWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            _capture.RecordingStopped += (s, e) =>
            {
                if (_isListening && e.Exception != null)
                {
                    ErrorOccurred?.Invoke($"音频捕获异常中断: {e.Exception.Message}");
                    _ = StopAsync();
                }
            };

            _capture.StartRecording();
            _isListening = true;
            ListeningStateChanged?.Invoke(true);

            _speechChannel = System.Threading.Channels.Channel.CreateBounded<SpeechQueueItem>(new System.Threading.Channels.BoundedChannelOptions(50)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });

            _ = Task.Run(() => ProcessSentenceQueueLoopAsync(_listeningCts.Token));
            _ = Task.Run(() => ProcessLiveAudioLoopAsync(_listeningCts.Token));

            string deviceTypeDesc = isCapture ? "麦克风" : "扬声器/耳机";
            StatusChanged?.Invoke($"Whisper AI 实时字幕监听中 ({targetDevice.FriendlyName} [{deviceTypeDesc}] - {CurrentLanguageName})...");
            return true;
        }
        catch (Exception ex)
        {
            _isListening = false;
            ListeningStateChanged?.Invoke(false);
            ErrorOccurred?.Invoke($"启动语音识别失败: {ex.Message}");
            await StopInternalAsync();
            return false;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task StopAsync()
    {
        try
        {
            if (await _sessionLock.WaitAsync(1000))
            {
                try
                {
                    await StopInternalAsync();
                }
                finally
                {
                    _sessionLock.Release();
                }
            }
            else
            {
                await StopInternalAsync();
            }
        }
        catch
        {
            await StopInternalAsync();
        }
    }

    private Task StopInternalAsync()
    {
        if (!_isListening && _capture == null)
        {
            return Task.CompletedTask;
        }

        _isListening = false;
        ListeningStateChanged?.Invoke(false);

        try
        {
            _listeningCts?.Cancel();
            _listeningCts?.Dispose();
            _listeningCts = null;
        }
        catch { }

        try
        {
            _speechChannel?.Writer.TryComplete();
            _speechChannel = null;
        }
        catch { }

        if (_capture != null)
        {
            try
            {
                _capture.StopRecording();
            }
            catch { }

            try
            {
                _capture.Dispose();
            }
            catch { }

            _capture = null;
        }

        _bufferedWaveProvider = null;
        _wave16 = null;

        StatusChanged?.Invoke("语音识别已停止");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _isListening = false; } catch { }
        try { _listeningCts?.Cancel(); } catch { }
        try { _listeningCts?.Dispose(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _speechChannel?.Writer.TryComplete(); } catch { }
        try { _processor?.Dispose(); } catch { }
        try { _factory?.Dispose(); } catch { }
        try { _sessionLock.Dispose(); } catch { }
        try { _transcribeLock.Dispose(); } catch { }
        try { _modelSwitchLock.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
