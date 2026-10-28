using System;
using VrcChatboxDemo.Services.Configuration;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 配置门面：保留 UI 与旧调用点使用的成员名，实际持久化委托给 <see cref="ConfigStore"/>。
///
/// 这里只剩两类逻辑：
/// 1. 把运行时状态（当前识别偏好、Inbound 端口等）同步进配置模型；
/// 2. 加载后把配置应用到各服务。
/// 文件定位、序列化、防抖与旧字段迁移都属于 <see cref="ConfigStore"/>。
/// </summary>
public partial class SettingsService
{
    public static readonly string ConfigPath = ConfigStore.ConfigPath;

    private readonly ConfigStore _configStore = new();

    /// <summary>把运行时状态写回配置模型（保存前调用，保证落盘内容与当前实际一致）。</summary>
    private void SyncRuntimeStateToConfig()
    {
        Config.ChineseVariant = SpeechService.ChineseVariant;
        Config.SpeechSliceDurationSeconds = SpeechService.SliceDurationSeconds;
        Config.GpuUsageMode = SpeechService.PerformanceMode.ToString();
        // 注意：不要用 SpeechService.CurrentModelType 覆盖 Config.SpeechModelType。
        // 前者是“已加载完成”的模型，后者是“用户选择的模型”；切换/下载未完成时
        // 任何一次自动保存都会把磁盘上的选择改回旧模型。
        Config.InboundHttpPort = InboundService.HttpPort;
        Config.InboundOscPort = InboundService.OscPort;
        Config.InboundAutoSendToVrc = InboundService.AutoSendToVrc;
    }

    public void SaveConfigDebounced(int delayMs = 300)
    {
        _configStore.SaveDebounced(() =>
        {
            SyncRuntimeStateToConfig();
            return Config;
        }, delayMs);
    }

    public void SaveConfigImmediately()
    {
        SyncRuntimeStateToConfig();
        _configStore.Save(Config);
    }

    public void LoadConfig()
    {
        switch (_configStore.Load(out var loaded))
        {
            case ConfigStore.LoadOutcome.Loaded when loaded != null:
                Config = loaded;
                if (ConfigStore.MigrateLegacyPaths(Config))
                {
                    SaveConfigImmediately();
                }
                break;

            case ConfigStore.LoadOutcome.Missing:
                // 首次运行：落一份默认配置，用户下次启动就有文件可改。
                Config = new AppConfig();
                SaveConfigImmediately();
                break;

            case ConfigStore.LoadOutcome.Corrupted:
                // 文件损坏时不覆盖它（用户可能还想手工抢救），只以默认值启动。
                Config = new AppConfig();
                break;
        }

        ApplyConfigToServices();
    }

    /// <summary>把配置应用到各服务初态。加载完成与「恢复默认」之后都要走一遍。</summary>
    private void ApplyConfigToServices()
    {
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

    /// <summary>兼容旧调用点：迁移逻辑已移入 <see cref="ConfigStore"/>。</summary>
    public static bool MigrateLegacyTtsPaths(AppConfig cfg) => ConfigStore.MigrateLegacyPaths(cfg);
}
