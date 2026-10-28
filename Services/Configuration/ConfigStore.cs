using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace VrcChatboxDemo.Services.Configuration;

/// <summary>
/// config.json 的唯一读写入口：定位、序列化、防抖保存、旧版字段迁移。
///
/// 这一层刻意不引用任何运行时服务（不碰 SpeechService / InboundService / WinUI），
/// 因此可以直接在测试里实例化，不需要解析源码或起 UI。
/// 需要把运行时状态同步进配置的调用方，自己在保存前完成同步。
/// </summary>
public sealed class ConfigStore : IDisposable
{
    /// <summary>用户配置文件位置（与可执行文件同目录）。</summary>
    public static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();
    private Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>加载结果：区分「没有文件」「文件损坏」「读取成功」，调用方据此决定是否写回默认值。</summary>
    public enum LoadOutcome
    {
        /// <summary>目标路径与开发目录都没有配置文件。</summary>
        Missing,
        /// <summary>文件存在但无法解析（JSON 损坏 / 反序列化为 null）。</summary>
        Corrupted,
        /// <summary>成功读取。</summary>
        Loaded
    }

    /// <summary>
    /// 读取配置。返回 <see cref="LoadOutcome.Missing"/> 或 <see cref="LoadOutcome.Corrupted"/> 时
    /// <paramref name="config"/> 为 null，调用方自行决定回退策略。
    /// </summary>
    public LoadOutcome Load(out AppConfig? config)
    {
        config = null;
        try
        {
            string? path = ResolveLoadPath();
            if (path == null) return LoadOutcome.Missing;

            string json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<AppConfig>(json);
            return config == null ? LoadOutcome.Corrupted : LoadOutcome.Loaded;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigStore] Load failed: {ex.Message}");
            config = null;
            return LoadOutcome.Corrupted;
        }
    }

    /// <summary>立即保存，并取消尚未触发的防抖保存。</summary>
    public void Save(AppConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        lock (_sync)
        {
            CancelDebounce_NoLock();
            WriteToDisk(config);
        }
    }

    /// <summary>
    /// 防抖保存。<paramref name="current"/> 在定时器真正触发时求值，
    /// 因此保存的一定是「那一刻」的最新配置，而不是调度时的快照。
    /// </summary>
    public void SaveDebounced(Func<AppConfig> current, int delayMs = 300)
    {
        if (current == null) throw new ArgumentNullException(nameof(current));

        lock (_sync)
        {
            if (_disposed) return;
            CancelDebounce_NoLock();
            _debounceTimer = new Timer(_ =>
            {
                try
                {
                    Save(current());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ConfigStore] Debounced save failed: {ex.Message}");
                }
            }, null, delayMs, Timeout.Infinite);
        }
    }

    private void CancelDebounce_NoLock()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    private static void WriteToDisk(AppConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, SerializerOptions);
            File.WriteAllText(ConfigPath, json);

            // 开发环境（源码目录）下同时镜像一份，便于调试时直接看仓库里的 config.json
            string? devPath = GetDevSourceConfigPath();
            if (!string.IsNullOrEmpty(devPath) &&
                !string.Equals(devPath, ConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.WriteAllText(devPath, json); } catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigStore] Save failed: {ex.Message}");
        }
    }

    private static string? ResolveLoadPath()
    {
        if (File.Exists(ConfigPath)) return ConfigPath;

        string? devPath = GetDevSourceConfigPath();
        return !string.IsNullOrEmpty(devPath) && File.Exists(devPath) ? devPath : null;
    }

    /// <summary>从 bin 目录向上找到源码目录（含 Services 子目录即认为是源码树）。</summary>
    private static string? GetDevSourceConfigPath()
    {
        try
        {
            string devDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\.."));
            if (Directory.Exists(Path.Combine(devDir, "Services")))
            {
                return Path.Combine(devDir, "config.json");
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 修正历史配置里的路径字段：清掉指向「别人机器」的绝对路径，改为探测本机实际位置。
    /// 返回是否发生了修改（调用方据此决定是否立即回写）。
    /// </summary>
    public static bool MigrateLegacyPaths(AppConfig cfg)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));

        bool changed = false;

        // 服务端脚本：优先保留用户自己设置且真实存在的路径，否则在常见的相对位置里探测。
        if (!PathExists(cfg.TtsServerScriptPath))
        {
            string? resolved = ProbeIndexttsScript();
            if (!string.Equals(cfg.TtsServerScriptPath, resolved ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                cfg.TtsServerScriptPath = resolved ?? string.Empty;
                changed = true;
            }
        }

        // Python 解释器：同样只在原值不可用时才改。
        if (!PathExists(cfg.TtsPythonExePath))
        {
            string? resolved = ProbeIndexttsPython();
            if (!string.Equals(cfg.TtsPythonExePath, resolved ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                cfg.TtsPythonExePath = resolved ?? string.Empty;
                changed = true;
            }
        }

        return changed;
    }

    private static bool PathExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>在可执行文件附近查找 indextts/tts_server.py（源码树与发布目录都覆盖）。</summary>
    private static string? ProbeIndexttsScript()
    {
        foreach (string candidate in EnumerateCandidates("tts_server.py"))
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>在可执行文件附近查找 conda 的 indextts 环境解释器。</summary>
    private static string? ProbeIndexttsPython()
    {
        foreach (string candidate in EnumerateCandidates(Path.Combine("envs", "indextts", "python.exe")))
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateCandidates(string relativeTail)
    {
        // 依次尝试：仓库根、仓库上级、程序目录本身
        string[] roots = { @"..\..\..\..\..", @"..\..\..\..", @"..\..\..", @"..\..", @"." };
        foreach (string root in roots)
        {
            string? candidate = null;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, root, "indextts", relativeTail));
            }
            catch { }
            if (!string.IsNullOrEmpty(candidate)) yield return candidate;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            CancelDebounce_NoLock();
        }
    }
}
