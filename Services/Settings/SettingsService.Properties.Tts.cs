using System;
using VrcChatboxDemo.Services.Tts;

namespace VrcChatboxDemo.Services;

/// <summary>
/// IndexTTS 合成与虚拟麦克风推流相关的配置属性（分部类，机械拆分）。
/// 全是「get/set → Config + 防抖保存」的样板，与朗读编排、进程管理分开。
/// 无行为变化。
/// </summary>
public partial class SettingsService
{
    public TtsService TtsService => TtsService.Instance;

    public bool IsTtsEnabled
    {
        get => Config.IsTtsEnabled;
        set { if (Config.IsTtsEnabled != value) { Config.IsTtsEnabled = value; SaveConfigDebounced(); DisplaySettingsChanged?.Invoke(); } }
    }
    public string TtsServerEndpoint
    {
        get => Config.TtsServerEndpoint;
        set { if (Config.TtsServerEndpoint != value) { Config.TtsServerEndpoint = value; SaveConfigDebounced(); } }
    }
    public int TtsServerPort
    {
        get => Config.TtsServerPort;
        set { if (Config.TtsServerPort != value) { Config.TtsServerPort = value; SaveConfigDebounced(); } }
    }
    public bool AutoStartTtsServer
    {
        get => Config.AutoStartTtsServer;
        set { if (Config.AutoStartTtsServer != value) { Config.AutoStartTtsServer = value; SaveConfigDebounced(); } }
    }
    public string TtsPythonExePath
    {
        get => Config.TtsPythonExePath;
        set { if (Config.TtsPythonExePath != value) { Config.TtsPythonExePath = value; SaveConfigDebounced(); } }
    }
    public string TtsServerScriptPath
    {
        get => Config.TtsServerScriptPath;
        set { if (Config.TtsServerScriptPath != value) { Config.TtsServerScriptPath = value; SaveConfigDebounced(); } }
    }
    public string TtsModelName
    {
        get => Config.TtsModelName;
        set { if (Config.TtsModelName != value) { Config.TtsModelName = value; SaveConfigDebounced(); } }
    }

    /// <summary>音色特征文件名（prompts/ 目录下），与权重解耦</summary>
    public string TtsPromptFile
    {
        get => Config.TtsPromptFile;
        set { if (Config.TtsPromptFile != value) { Config.TtsPromptFile = value; SaveConfigDebounced(); } }
    }

    /// <summary>切换权重时是否自动跟随配套音色</summary>
    public bool TtsAutoFollowPrompt
    {
        get => Config.TtsAutoFollowPrompt;
        set { if (Config.TtsAutoFollowPrompt != value) { Config.TtsAutoFollowPrompt = value; SaveConfigDebounced(); } }
    }
    public string TtsVirtualMicDeviceId
    {
        get => Config.TtsVirtualMicDeviceId;
        set { if (Config.TtsVirtualMicDeviceId != value) { Config.TtsVirtualMicDeviceId = value; SaveConfigDebounced(); } }
    }
    public string TtsMonitorDeviceId
    {
        get => Config.TtsMonitorDeviceId;
        set { if (Config.TtsMonitorDeviceId != value) { Config.TtsMonitorDeviceId = value; SaveConfigDebounced(); } }
    }
    public bool IsTtsMonitorEnabled
    {
        get => Config.IsTtsMonitorEnabled;
        set { if (Config.IsTtsMonitorEnabled != value) { Config.IsTtsMonitorEnabled = value; SaveConfigDebounced(); } }
    }
    public float TtsOutputVolume
    {
        get => Config.TtsOutputVolume;
        set { if (Math.Abs(Config.TtsOutputVolume - value) > 0.001f) { Config.TtsOutputVolume = value; SaveConfigDebounced(); } }
    }
    public float TtsMonitorVolume
    {
        get => Config.TtsMonitorVolume;
        set { if (Math.Abs(Config.TtsMonitorVolume - value) > 0.001f) { Config.TtsMonitorVolume = value; SaveConfigDebounced(); } }
    }
    public bool IsTtsAutoReadSentMessage
    {
        get => Config.IsTtsAutoReadSentMessage;
        set { if (Config.IsTtsAutoReadSentMessage != value) { Config.IsTtsAutoReadSentMessage = value; SaveConfigDebounced(); } }
    }
    public bool IsTtsAutoReadTranslation
    {
        get => Config.IsTtsAutoReadTranslation;
        set { if (Config.IsTtsAutoReadTranslation != value) { Config.IsTtsAutoReadTranslation = value; SaveConfigDebounced(); } }
    }

    /// <summary>
    /// 发送消息时自动朗读「我发出去的消息的翻译」。
    /// 与 <see cref="IsTtsAutoReadSentMessage"/> 同时开启时优先朗读译文，避免重复播报。
    /// </summary>
    public bool IsTtsAutoReadSentTranslation
    {
        get => Config.IsTtsAutoReadSentTranslation;
        set { if (Config.IsTtsAutoReadSentTranslation != value) { Config.IsTtsAutoReadSentTranslation = value; SaveConfigDebounced(); } }
    }
}
