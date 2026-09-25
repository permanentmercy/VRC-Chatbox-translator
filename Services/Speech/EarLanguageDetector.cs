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
    private string _loadedModelName = "ggml-tiny";

    public bool IsInitialized => _isInitialized;
    public string LoadedModelName => _loadedModelName;

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized) return true;

        await _lock.WaitAsync();
        try
        {
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

            try
            {
                _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
            }
            catch
            {
                _factory = WhisperFactory.FromPath(modelPath);
            }

            _processor = _factory.CreateBuilder()
                .WithLanguageDetection()
                .Build();

            _isInitialized = true;
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
        if (!_isInitialized || _processor == null || samples16k == null || samples16k.Length < 8000)
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

    private static EarLanguageResult MapToLiveCaptionsLanguage(string shortLang, float probability)
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
            _ => new EarLanguageResult("en-US", $"英语 (匹配: {shortLang})", probability)
        };
    }

    public void Dispose()
    {
        try { _processor?.Dispose(); } catch { }
        try { _factory?.Dispose(); } catch { }
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
