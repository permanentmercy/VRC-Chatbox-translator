#pragma warning disable CA1416
#pragma warning disable CS0618

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VrcChatboxDemo.Services;

/// <summary>
/// Windows 11 实时字幕语言自动切换管理器 (基于 Ear 音频语种识别模型)
/// 核心特性：
/// 1. 采用 Ear 声学轻量识别模型，直接对音频流抽取特征，即使当前字幕语言完全听不懂也能准确判别；
/// 2. 支持用户自定义定时检测频率 (1s, 2s, 3s, 5s)；
/// 3. 防抖机制：连续超过 3 次识别到不同语种，才自动执行后台字幕语言切换；
/// 4. 实时向上层抛出检测状态、语种名称及命中进度 (如: 2/3 次)。
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
                StatusUpdated?.Invoke("Ear 音频语种自动检测已启动", SettingsService.Instance.LiveCaptionsLanguageCode, 0, HitThreshold);
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

    private async Task StartWorkerInternalAsync()
    {
        _workerCts = new CancellationTokenSource();
        var token = _workerCts.Token;

        try
        {
            // 初始化音频捕获 (优先使用配置的隔离进程回路，否则使用系统默认回路)
            string procName = SettingsService.Instance.TargetAudioProcessName;
            string devId = SettingsService.Instance.AudioInputDeviceId;

            if (!string.IsNullOrEmpty(devId) && devId.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
            {
                _audioCapture = await ProcessLoopbackCapture.CreateAsync(procName);
            }
            else
            {
                _audioCapture = new WasapiLoopbackCapture();
            }

            _bufferedWaveProvider = new BufferedWaveProvider(_audioCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            var sampleProvider = _bufferedWaveProvider.ToSampleProvider();
            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            var mono = resampler.ToMono();
            _wave16 = new SampleToWaveProvider16(mono);

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

    private async Task DetectionLoopAsync(CancellationToken ct)
    {
        // 每次采集 1.5 秒音频 (16kHz 16bit mono: 16000 * 2 * 1.5 = 48,000 字节)
        byte[] byteBuffer = new byte[48000];

        while (!ct.IsCancellationRequested && _isEnabled)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);

                if (_wave16 == null) continue;

                int bytesRead = _wave16.Read(byteBuffer.AsSpan());
                int sampleCount = bytesRead / 2;
                if (sampleCount < 8000)
                {
                    continue; // 声音采样过短，继续累积
                }

                // 转换为 float[-1.0f, 1.0f] 送入 Ear 识别器
                float[] audioSlice = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short s = (short)(byteBuffer[i * 2] | (byteBuffer[i * 2 + 1] << 8));
                    audioSlice[i] = s / 32768f;
                }

                var result = await _earDetector.DetectFromAudioAsync(audioSlice);
                if (result == null || result.Confidence < 0.5f)
                {
                    continue; // 未检测到有效人声或置信度较低
                }

                string detectedCode = result.Code;
                string currentCode = SettingsService.Instance.LiveCaptionsLanguageCode;

                if (string.Equals(detectedCode, currentCode, StringComparison.OrdinalIgnoreCase))
                {
                    if (_hitCount > 0)
                    {
                        _hitCount = 0;
                        _candidateCode = null;
                        StatusUpdated?.Invoke($"当前语种稳定: {result.DisplayName}", detectedCode, 0, HitThreshold);
                    }
                    continue;
                }

                // 识别到不同的语言
                if (string.Equals(detectedCode, _candidateCode, StringComparison.OrdinalIgnoreCase))
                {
                    _hitCount++;
                }
                else
                {
                    _candidateCode = detectedCode;
                    _hitCount = 1;
                }

                StatusUpdated?.Invoke($"检测到音频语种: {result.DisplayName} (命中 {_hitCount}/{HitThreshold} 次)", detectedCode, _hitCount, HitThreshold);

                // 超过三次（>= 3）触发自动更换字幕识别语言
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
                        SettingsService.Instance.LiveCaptionsLanguageCode = targetCodeNormalized(targetToSwitch);
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

    private static string targetCodeNormalized(string code) => code switch
    {
        "zh" => "zh-CN",
        "en" => "en-US",
        "ja" => "ja-JP",
        "ko" => "ko-KR",
        "fr" => "fr-FR",
        "de" => "de-DE",
        "es" => "es-ES",
        "it" => "it-IT",
        _ => code
    };

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
    }

    public void Dispose()
    {
        StopWorkerInternal();
        _earDetector.Dispose();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
