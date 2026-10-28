using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace VrcChatboxDemo.Services;

public record EarLanguageResult(string Code, string DisplayName, float Confidence);

/// <summary>
/// Ear 轻量级音频语种识别器 (基于离线音频模型与语音特征层 Language Identification)
/// 直接从 PCM 音频数据中抽取梅尔频谱特征并执行微秒级声学语种判别，
/// 突破传统文本模型无法识别未知语种文字的缺陷，实现真正的跨语种听音辨意。
/// </summary>
public class EarLanguageDetector : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isInitialized;
    private int _isDisposed;
    private string _loadedModelName = "ggml-tiny";

    public bool IsInitialized => Volatile.Read(ref _isDisposed) == 0 && _isInitialized;
    public string LoadedModelName => _loadedModelName;

    public async Task<bool> InitializeAsync()
    {
        if (Volatile.Read(ref _isDisposed) != 0) return false;
        if (_isInitialized) return true;

        await _lock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _isDisposed) != 0) return false;
            if (_isInitialized) return true;

            string baseDir = AppContext.BaseDirectory;
            string modelPath = Path.Combine(baseDir, "Models", "ggml-base.bin");
            if (!File.Exists(modelPath))
            {
                modelPath = Path.Combine(Directory.GetCurrentDirectory(), "Models", "ggml-base.bin");
            }

            if (!File.Exists(modelPath))
            {
                modelPath = Path.Combine(baseDir, "Models", "ggml-tiny.bin");
                if (!File.Exists(modelPath))
                {
                    modelPath = Path.Combine(Directory.GetCurrentDirectory(), "Models", "ggml-tiny.bin");
                }
            }

            if (!File.Exists(modelPath))
            {
                return false;
            }

            _loadedModelName = Path.GetFileNameWithoutExtension(modelPath);
            SpeechService.EnsureCudaEnvironment();

            // WhisperFactory/Processor 的构建会读取上百 MB 权重并初始化推理后端，
            // 不能在 UI 线程同步执行，否则用户一打开“自动检测语言”界面就会卡住数秒。
            var built = await Task.Run(() =>
            {
                WhisperFactory factory;
                try
                {
                    factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
                }
                catch
                {
                    factory = WhisperFactory.FromPath(modelPath);
                }

                var processor = factory.CreateBuilder()
                    .WithLanguageDetection()
                    .Build();
                return (factory, processor);
            });

            _factory = built.factory;
            _processor = built.processor;
            _isInitialized = true;
            // Dispose 可能在模型加载期间已经返回；不允许迟到的构建复活实例。
            // 当前仍持有推理锁，局部/已发布句柄均可在这里安全回收。
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                DisposeHandlesUnderLock();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EarLanguageDetector] Init error: {ex.Message}");
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 对 16kHz 单声道 PCM 浮点音频执行轻量声学语种识别
    /// </summary>
    public async Task<EarLanguageResult?> DetectFromAudioAsync(float[] samples16k, Action<float>? rmsCallback = null)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || !_isInitialized || _processor == null || samples16k == null || samples16k.Length < 8000)
        {
            return null;
        }

        // 计算音频能量有效值 (RMS) 与峰值 (Peak)
        float sumSq = 0f;
        float peak = 0f;
        for (int i = 0; i < samples16k.Length; i++)
        {
            float s = samples16k[i];
            sumSq += s * s;
            float abs = Math.Abs(s);
            if (abs > peak) peak = abs;
        }
        float rms = (float)Math.Sqrt(sumSq / samples16k.Length);
        rmsCallback?.Invoke(rms);

        // 调整静音底噪门槛至 0.0006f (-64 dBFS)，灵敏感知普通麦克风输入与远场轻声说话
        if (rms < 0.0006f || peak < 0.0012f)
        {
            return null;
        }

        // 自适应增益归一化：若声音较小 (如 RMS 在 0.001~0.003)，适度放大至理想电平 (Peak ~0.7f)，大幅提升 Whisper 声学特征提取精度
        float[] inferenceSamples = samples16k;
        if (peak > 0f && peak < 0.35f)
        {
            float gain = Math.Min(300f, 0.7f / peak);
            inferenceSamples = new float[samples16k.Length];
            for (int i = 0; i < samples16k.Length; i++)
            {
                inferenceSamples[i] = Math.Clamp(samples16k[i] * gain, -1.0f, 1.0f);
            }
        }

        await _lock.WaitAsync();
        try
        {
            // 等锁期间可能已 Dispose，必须在使用原生句柄前再次验证生命周期。
            if (Volatile.Read(ref _isDisposed) != 0 || !_isInitialized || _processor == null) return null;
            var (lang, prob) = _processor.DetectLanguageWithProbability(inferenceSamples);
            if (string.IsNullOrWhiteSpace(lang) || prob < 0.35f)
            {
                return null;
            }

            return MapToLiveCaptionsLanguage(lang, prob);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EarLanguageDetector] Detection error: {ex.Message}");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static EarLanguageResult? MapToLiveCaptionsLanguage(string shortLang, float probability)
    {
        string norm = shortLang.Trim().ToLowerInvariant();
        return norm switch
        {
            "zh" => new EarLanguageResult("zh-CN", "中文 (简体，中国)", probability),
            "en" => new EarLanguageResult("en-US", "英语 (美国)", probability),
            "ja" => new EarLanguageResult("ja-JP", "日本語 (日本)", probability),
            "ko" => new EarLanguageResult("ko-KR", "한국어 (대한민국)", probability),
            "fr" => new EarLanguageResult("fr-FR", "Français (France)", probability),
            "de" => new EarLanguageResult("de-DE", "Deutsch (Deutschland)", probability),
            "es" => new EarLanguageResult("es-ES", "Español (España)", probability),
            "it" => new EarLanguageResult("it-IT", "Italiano (Italia)", probability),
            // LiveCaptions 不支持的语种（ru/pt/nl/pl/tr 等）绝不能回退成 en-US：
            // 否则连续命中 3 次会把字幕强制切成英语，并把配置永久改成 en-US。
            _ => null
        };
    }

    private void DisposeHandlesUnderLock()
    {
        _isInitialized = false;
        try { _processor?.Dispose(); } catch { }
        try { _factory?.Dispose(); } catch { }
        _processor = null;
        _factory = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        _isInitialized = false;

        if (_lock.Wait(500))
        {
            try { DisposeHandlesUnderLock(); }
            finally { _lock.Release(); }
        }
        else
        {
            // 推理/加载超过退出预算时延迟回收，绝不能在原生代码仍使用句柄时强制释放。
            _ = Task.Run(async () =>
            {
                await _lock.WaitAsync();
                try { DisposeHandlesUnderLock(); }
                finally { _lock.Release(); }
            });
        }

        // 不 Dispose _lock：并发的检测/初始化仍可能在 finally 里 Release。
        GC.SuppressFinalize(this);
    }
}
