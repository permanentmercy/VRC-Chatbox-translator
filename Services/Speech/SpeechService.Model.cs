using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace VrcChatboxDemo.Services;

public partial class SpeechService
{
    public static IReadOnlyList<SpeechLanguageInfo> GetSupportedLanguages()
    {
        return new List<SpeechLanguageInfo>
        {
            new("auto", "自动识别语言 (Auto Detect)"),
            new("zh", "中文 (Chinese)"),
            new("en", "English (英语)"),
            new("ja", "日本語 (Japanese)"),
            new("ko", "한국어 (Korean)"),
            new("ru", "Русский (俄语)"),
            new("fr", "Français (法语)"),
            new("de", "Deutsch (德语)"),
            new("es", "Español (西班牙语)")
        };
    }

    public static async Task OpenSpeechPrivacySettingsAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-speech"));
        }
        catch { }
    }

    public static async Task OpenSoundSettingsAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
        }
        catch { }
    }

    /// <summary>
    /// 寻找或自动准备本地 Whisper AI 权重模型 (支持 tiny, base, small 三档模型)
    /// </summary>
    private async Task<string> EnsureModelPathAsync(string? modelType = null)
    {
        string targetType = (modelType ?? _currentModelType).ToLowerInvariant();
        string modelFileName = targetType switch
        {
            "base" => "ggml-base.bin",
            "small" => "ggml-small.bin",
            _ => "ggml-tiny.bin"
        };

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Models", modelFileName),
            Path.Combine(AppContext.BaseDirectory, modelFileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Models", modelFileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", modelFileName),
            Path.Combine(Environment.CurrentDirectory, "Models", modelFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcChatboxDemo", "Models", modelFileName)
        };

        foreach (var path in candidates)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full) && new FileInfo(full).Length > 10_000_000)
                {
                    return full;
                }
            }
            catch { }
        }

        // 若不存在，则自动下载
        var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcChatboxDemo", "Models");
        Directory.CreateDirectory(targetDir);
        var targetPath = Path.Combine(targetDir, modelFileName);

        var ggmlType = targetType switch
        {
            "base" => GgmlType.Base,
            "small" => GgmlType.Small,
            _ => GgmlType.Tiny
        };

        StatusChanged?.Invoke($"正在准备 Whisper {targetType} 语音模型权重，请稍候...");
        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType);

        // 先写临时文件再原子替换：File.OpenWrite 不会截断旧文件，若上次下载中断留下
        // 半截文件，继续覆盖会保留尾部脏数据，导致模型永久损坏。临时文件也能保证
        // 下载失败时不会破坏已有的可用模型。
        string tempPath = targetPath + ".download";
        try
        {
            using (var fileWriter = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await modelStream.CopyToAsync(fileWriter);
            }
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }

        StatusChanged?.Invoke($"Whisper {targetType} 模型下载完毕就绪");
        return targetPath;
    }

    public async Task SwitchModelAsync(string modelType)
    {
        string targetModel = modelType.ToLowerInvariant();
        if (targetModel != "tiny" && targetModel != "base" && targetModel != "small")
        {
            targetModel = "tiny";
        }

        await _modelSwitchLock.WaitAsync();
        try
        {
            if (string.Equals(_currentModelType, targetModel, StringComparison.OrdinalIgnoreCase) && _factory != null)
            {
                return;
            }

            StatusChanged?.Invoke($"正在切换语音识别模型至 {targetModel}...");

            EnsureCudaEnvironment();
            string modelPath = await EnsureModelPathAsync(targetModel);

            string languageTag = _currentLanguageTag;
            var built = await Task.Run(() =>
            {
                WhisperFactory factory;
                try
                {
                    factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"GPU 加速加载异常 ({ex.Message})，自动回退至 CPU 推理");
                    factory = WhisperFactory.FromPath(modelPath);
                }

                // 始终构建 processor：即使当前没有在监听，也不能把 _processor 置 null。
                // StartAsync 可能在“已建好 processor、尚未把 _isListening 置 true”的窗口里
                // 被切换模型，旧逻辑会把 _processor 清空，导致启动后永久零字幕。
                var processor = factory.CreateBuilder().WithLanguage(languageTag).Build();
                return (factory, processor);
            });

            WhisperFactory newFactory = built.factory;
            WhisperProcessor newProcessor = built.processor;

            WhisperProcessor? oldProcessor = null;
            WhisperFactory? oldFactory = null;

            await _transcribeLock.WaitAsync();
            try
            {
                oldProcessor = _processor;
                oldFactory = _factory;

                _factory = newFactory;
                _processor = newProcessor;
                _currentModelType = targetModel;

                var loadedLib = RuntimeOptions.LoadedLibrary;
                StatusChanged?.Invoke($"已切换至 Whisper {targetModel} 模型 [推理加速: {loadedLib}]");
            }
            finally
            {
                _transcribeLock.Release();
            }

            _ = Task.Run(() =>
            {
                try { oldProcessor?.Dispose(); } catch { }
                try { oldFactory?.Dispose(); } catch { }
            });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"切换模型失败: {ex.Message}");
        }
        finally
        {
            _modelSwitchLock.Release();
        }
    }

    public async Task SwitchLanguageAsync(string? languageTag)
    {
        string targetLang = NormalizeLanguageTag(languageTag);
        StatusChanged?.Invoke($"正在切换识别语言至: {GetLanguageDisplayName(targetLang)}...");

        if (_factory == null)
        {
            await EnsureProcessorAsync(targetLang);
            return;
        }

        // 与 SwitchModelAsync/EnsureProcessorAsync 共用同一把模型锁：
        // 否则语言切换可能在模型切换释放旧工厂的瞬间调用 CreateBuilder()，
        // 或把旧工厂产出的 processor 安装到 _processor 后旧工厂被 Dispose。
        await _modelSwitchLock.WaitAsync();
        try
        {
            // 必须在锁内比较：前一个语言请求可能仍在构建处理器，锁外的旧值
            // 会把 A→B→A 中最后一次 A 误判成无需切换。
            if (_processor != null && string.Equals(_currentLanguageTag, targetLang, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var factory = _factory;
            if (factory == null) return;

            var newProc = await Task.Run(() => factory.CreateBuilder()
                .WithLanguage(targetLang)
                .Build());

            WhisperProcessor? oldProc;
            await _transcribeLock.WaitAsync();
            try
            {
                oldProc = _processor;
                _processor = newProc;
                _currentLanguageTag = targetLang;
                CurrentLanguageName = GetLanguageDisplayName(targetLang);
                StatusChanged?.Invoke($"已切换识别语言至: {CurrentLanguageName}");
            }
            finally
            {
                _transcribeLock.Release();
            }

            _ = Task.Run(() => { try { oldProc?.Dispose(); } catch { } });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"切换语言失败: {ex.Message}");
        }
        finally
        {
            _modelSwitchLock.Release();
        }
    }

    /// <summary>
    /// 初始化或更新 Whisper 处理器
    /// </summary>
    private async Task EnsureProcessorAsync(string? languageTag = null)
    {
        string targetLang = NormalizeLanguageTag(languageTag);

        await _modelSwitchLock.WaitAsync();
        try
        {
            if (_processor != null && string.Equals(_currentLanguageTag, targetLang, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_factory == null)
            {
                EnsureCudaEnvironment();
                StatusChanged?.Invoke("正在加载 Whisper 模型 (尝试启用 GPU/CUDA 加速)...");
                string modelPath = await EnsureModelPathAsync(_currentModelType);

                // 模型加载/处理器构建是重 IO + CPU/GPU 初始化，必须离开 UI 线程，
                // 否则用户拨动语音开关时窗口会冻结数秒。
                var factory = await Task.Run(() =>
                {
                    try
                    {
                        var f = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
                        var loadedLib = RuntimeOptions.LoadedLibrary;
                        StatusChanged?.Invoke($"Whisper模型就绪 [推理加速: {loadedLib}]");
                        return f;
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke($"GPU 加速加载异常 ({ex.Message})，自动回退至 CPU 推理");
                        return WhisperFactory.FromPath(modelPath);
                    }
                });
                _factory = factory;
            }

            var factoryForProcessor = _factory!;
            var newProc = await Task.Run(() => factoryForProcessor.CreateBuilder()
                .WithLanguage(targetLang)
                .Build());

            WhisperProcessor? oldProc;
            await _transcribeLock.WaitAsync();
            try
            {
                oldProc = _processor;
                _processor = newProc;
                _currentLanguageTag = targetLang;
                CurrentLanguageName = GetLanguageDisplayName(targetLang);
            }
            finally
            {
                _transcribeLock.Release();
            }

            _ = Task.Run(() => { try { oldProc?.Dispose(); } catch { } });
        }
        finally
        {
            _modelSwitchLock.Release();
        }
    }

    private static string NormalizeLanguageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "auto";
        tag = tag.Trim().ToLowerInvariant();
        if (tag.StartsWith("zh")) return "zh";
        if (tag.StartsWith("en")) return "en";
        if (tag.StartsWith("ja")) return "ja";
        if (tag.StartsWith("ko")) return "ko";
        if (tag.StartsWith("ru")) return "ru";
        if (tag.StartsWith("fr")) return "fr";
        if (tag.StartsWith("de")) return "de";
        if (tag.StartsWith("es")) return "es";
        return tag;
    }

    private static string GetLanguageDisplayName(string tag)
    {
        return tag switch
        {
            "zh" => "中文 (Chinese)",
            "en" => "English (英语)",
            "ja" => "日本語 (Japanese)",
            "ko" => "한국어 (Korean)",
            "ru" => "Русский (俄语)",
            "fr" => "Français (法语)",
            "de" => "Deutsch (德语)",
            "es" => "Español (西班牙语)",
            _ => "自动语言识别 (Auto)"
        };
    }
}
