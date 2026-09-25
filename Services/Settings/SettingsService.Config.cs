using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace VrcChatboxDemo.Services;

public partial class SettingsService
{
    public static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private readonly object _saveLock = new();
    private Timer? _saveDebounceTimer;

    public void SaveConfigDebounced(int delayMs = 300)
    {
        lock (_saveLock)
        {
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = new Timer(_ =>
            {
                SaveConfigImmediately();
            }, null, delayMs, Timeout.Infinite);
        }
    }

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

    public void SaveConfigImmediately()
    {
        lock (_saveLock)
        {
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = null;

            try
            {
                Config.ChineseVariant = SpeechService.ChineseVariant;
                Config.SpeechSliceDurationSeconds = SpeechService.SliceDurationSeconds;
                Config.GpuUsageMode = SpeechService.PerformanceMode.ToString();
                Config.SpeechModelType = SpeechService.CurrentModelType;
                Config.InboundEnabled = InboundService.IsRunning;
                Config.InboundHttpPort = InboundService.HttpPort;
                Config.InboundOscPort = InboundService.OscPort;
                Config.InboundAutoSendToVrc = InboundService.AutoSendToVrc;

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string json = JsonSerializer.Serialize(Config, options);
                File.WriteAllText(ConfigPath, json);

                // 若处于开发源码目录环境，同步镜像保存到项目根目录
                string? devPath = GetDevSourceConfigPath();
                if (!string.IsNullOrEmpty(devPath) && !string.Equals(devPath, ConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.WriteAllText(devPath, json); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Save failed: {ex.Message}");
            }
        }
    }

    public void LoadConfig()
    {
        try
        {
            string? pathToLoad = null;
            if (File.Exists(ConfigPath))
            {
                pathToLoad = ConfigPath;
            }
            else
            {
                string? devPath = GetDevSourceConfigPath();
                if (!string.IsNullOrEmpty(devPath) && File.Exists(devPath))
                {
                    pathToLoad = devPath;
                }
            }

            if (!string.IsNullOrEmpty(pathToLoad))
            {
                string json = File.ReadAllText(pathToLoad);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json);
                if (loaded != null)
                {
                    Config = loaded;
                }
            }
            else
            {
                Config = new AppConfig();
                SaveConfigImmediately();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Load failed: {ex.Message}");
            Config = new AppConfig();
        }

        // 应用各服务初态
        OscService.Host = Config.Host;
        OscService.Port = Config.Port;
        SpeechService.ChineseVariant = Config.ChineseVariant;
        SpeechService.SliceDurationSeconds = Config.SpeechSliceDurationSeconds;
        SpeechService.PerformanceMode = SpeechService.ParseGpuMode(Config.GpuUsageMode);
        SpeechService.CurrentModelType = string.IsNullOrWhiteSpace(Config.SpeechModelType) ? "tiny" : Config.SpeechModelType;
        InboundService.HttpPort = Config.InboundHttpPort;
        InboundService.OscPort = Config.InboundOscPort;
        InboundService.AutoSendToVrc = Config.InboundAutoSendToVrc;
        PersistentTextService.EnableInGameAvoidance = Config.InGameAvoidanceEnabled;
        PersistentTextService.AvoidanceSeconds = Config.InGameAvoidanceSeconds;
        PersistentTextService.TransientDurationSeconds = Config.TransientTextDurationSeconds;

        if (Config.IsSpeechRecognitionEnabled)
        {
            SpeechStatus = "就绪 (启动中...)";
        }
    }
}
