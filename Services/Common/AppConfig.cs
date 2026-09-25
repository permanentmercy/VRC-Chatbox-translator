using System;
using System.Collections.Generic;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 应用程序全局配置模型，用于序列化并持久化存储至 config.json
/// </summary>
public class AppConfig
{
    // ==========================================
    // 窗口尺寸、位置与外观状态
    // ==========================================
    public int WindowWidth { get; set; } = 820;
    public int WindowHeight { get; set; } = 560;
    public int? WindowX { get; set; } = null;
    public int? WindowY { get; set; } = null;
    public bool IsAlwaysOnTop { get; set; } = false;
    public bool IsMaximized { get; set; } = false;

    // ==========================================
    // 左侧导航功能栏项自定义排列顺序 (Tag 列表)
    // ==========================================
    public List<string> NavOrder { get; set; } = new()
    {
        "chat",
        "speech",
        "translation",
        "hotkey",
        "interaction",
        "network"
    };

    // ==========================================
    // VRChat OSC 输出网络设置
    // ==========================================
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;

    // ==========================================
    // 交互与发送选项
    // ==========================================
    public bool IsDirectSendEnabled { get; set; } = true;
    public bool IsSoundEnabled { get; set; } = true;
    public bool IsTypingEnabled { get; set; } = false;
    public bool IsLiveTypingEnabled { get; set; } = true;
    public bool IsEnterSendEnabled { get; set; } = true;
    public bool IsClearOnSendEnabled { get; set; } = true;
    public bool IsPersistentTextEnabled { get; set; } = false;
    public string PersistentCustomText { get; set; } = string.Empty;
    public bool InGameAvoidanceEnabled { get; set; } = true;
    public int InGameAvoidanceSeconds { get; set; } = 12;
    public int TransientTextDurationSeconds { get; set; } = 10;

    // ==========================================
    // 快捷热键配置
    // ==========================================
    public bool IsHotkeyEnabled { get; set; } = true;
    public uint CustomHotkeyModifiers { get; set; } = 6; // MOD_CONTROL(2) | MOD_SHIFT(4)
    public uint CustomHotkeyKey { get; set; } = 0x43; // 'C'
    public string CustomHotkeyString { get; set; } = "Ctrl + Shift + C";

    public bool IsImmersiveHotkeyEnabled { get; set; } = true;
    public uint CustomImmersiveHotkeyModifiers { get; set; } = 6; // MOD_CONTROL(2) | MOD_SHIFT(4)
    public uint CustomImmersiveHotkeyKey { get; set; } = 0x5A; // 'Z'
    public string CustomImmersiveHotkeyString { get; set; } = "Ctrl + Shift + Z";

    // ==========================================
    // 语音识别与字幕
    // ==========================================
    public bool IsSpeechRecognitionEnabled { get; set; } = false;
    public string SpeechLanguageTag { get; set; } = string.Empty;
    public string ChineseVariant { get; set; } = "Simplified"; // "Simplified", "Traditional", "Original"
    public string AudioInputDeviceId { get; set; } = string.Empty;
    public int SubtitleQueueCapacity { get; set; } = 3;
    public bool ShowRecognizedText { get; set; } = true;
    public bool IsAutoFillEnabled { get; set; } = false;
    public int SpeechSliceDurationSeconds { get; set; } = 6; // 语音切片单句最长时限（秒）：3, 4, 5, 6, 8, 10, 12, 15
    public string GpuUsageMode { get; set; } = "High"; // "High" (高性能保证及时性), "Medium" (均衡模式), "Low" (低负载防卡顿慢慢算)
    public string SpeechModelType { get; set; } = "tiny"; // "tiny", "base", "small"
    public string SpeechEngine { get; set; } = "LiveCaptions"; // "LiveCaptions", "Whisper"
    public bool LiveCaptionsHideNativeWindow { get; set; } = true;
    public string LiveCaptionsLanguageCode { get; set; } = "zh-CN";
    public string TargetAudioProcessName { get; set; } = "VRChat";

    // ==========================================
    // AI 翻译 (Ollama)
    // ==========================================
    public bool IsTranslationEnabled { get; set; } = false;
    public bool ShowTranslatedText { get; set; } = true;
    public bool ShowTranslationLatency { get; set; } = true;
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";
    public string OllamaModel { get; set; } = "RogerBen/HY-MT2-1.8B:latest";
    public string TargetLanguage { get; set; } = "英语 (English)";
    public string OllamaPromptTemplate { get; set; } = SettingsService.DefaultOllamaPromptTemplate;

    // ==========================================
    // 外部 Inbound 服务端口与设置
    // ==========================================
    public bool InboundEnabled { get; set; } = true;
    public int InboundHttpPort { get; set; } = 9002;
    public int InboundOscPort { get; set; } = 9001;
    public bool InboundAutoSendToVrc { get; set; } = true;
}
