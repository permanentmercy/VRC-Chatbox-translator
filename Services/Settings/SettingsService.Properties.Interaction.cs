using System;
using VrcChatboxDemo.Services.Configuration;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 发送/交互/热键/字幕与翻译相关的配置属性（分部类，机械拆分）。
/// 全是「get/set → Config + 防抖保存 + 少量服务联动」的样板。
/// 无行为变化。
/// </summary>
public partial class SettingsService
{
    public string ChineseVariant
    {
        get => SpeechService.ChineseVariant;
        set
        {
            SpeechService.ChineseVariant = value;
            if (Config.ChineseVariant != value)
            {
                Config.ChineseVariant = value;
                SaveConfigDebounced();
            }
        }
    }

    public string Host
    {
        get => Config.Host;
        set
        {
            if (Config.Host != value)
            {
                Config.Host = value;
                OscService.Host = value;
                SaveConfigDebounced();
            }
        }
    }

    public int Port
    {
        get => Config.Port;
        set
        {
            if (Config.Port != value)
            {
                Config.Port = value;
                OscService.Port = value;
                SaveConfigDebounced();
            }
        }
    }

    public bool IsDirectSendEnabled
    {
        get => Config.IsDirectSendEnabled;
        set { if (Config.IsDirectSendEnabled != value) { Config.IsDirectSendEnabled = value; SaveConfigDebounced(); } }
    }

    /// <summary>
    /// 外部接口的用户意图开关。与运行时 <see cref="InboundService.IsRunning"/> 分离：
    /// 端口暂时被占用导致启动失败时，不能把用户“想开启”的意图永久写成 false。
    /// </summary>
    public bool InboundEnabled
    {
        get => Config.InboundEnabled;
        set
        {
            if (Config.InboundEnabled != value)
            {
                Config.InboundEnabled = value;
                SaveConfigDebounced();
            }
        }
    }

    public bool IsSoundEnabled
    {
        get => Config.IsSoundEnabled;
        set { if (Config.IsSoundEnabled != value) { Config.IsSoundEnabled = value; SaveConfigDebounced(); } }
    }

    public bool IsTypingEnabled
    {
        get => Config.IsTypingEnabled;
        set { if (Config.IsTypingEnabled != value) { Config.IsTypingEnabled = value; SaveConfigDebounced(); } }
    }

    public bool IsLiveTypingEnabled
    {
        get => Config.IsLiveTypingEnabled;
        set { if (Config.IsLiveTypingEnabled != value) { Config.IsLiveTypingEnabled = value; SaveConfigDebounced(); } }
    }

    public bool IsEnterSendEnabled
    {
        get => Config.IsEnterSendEnabled;
        set { if (Config.IsEnterSendEnabled != value) { Config.IsEnterSendEnabled = value; SaveConfigDebounced(); } }
    }

    public bool IsClearOnSendEnabled
    {
        get => Config.IsClearOnSendEnabled;
        set { if (Config.IsClearOnSendEnabled != value) { Config.IsClearOnSendEnabled = value; SaveConfigDebounced(); } }
    }

    public bool IsPersistentTextEnabled
    {
        get => Config.IsPersistentTextEnabled;
        set
        {
            if (Config.IsPersistentTextEnabled != value)
            {
                Config.IsPersistentTextEnabled = value;
                PersistentTextService.IsEnabled = value;
                SaveConfigDebounced();
            }
        }
    }

    public string PersistentCustomText
    {
        get => Config.PersistentCustomText;
        set
        {
            if (Config.PersistentCustomText != value)
            {
                Config.PersistentCustomText = value;
                PersistentTextService.CustomText = value;
                SaveConfigDebounced();
            }
        }
    }

    public bool InGameAvoidanceEnabled
    {
        get => Config.InGameAvoidanceEnabled;
        set
        {
            if (Config.InGameAvoidanceEnabled != value)
            {
                Config.InGameAvoidanceEnabled = value;
                PersistentTextService.EnableInGameAvoidance = value;
                VrcInGameGuard.IsEnabled = value;
                SaveConfigDebounced();
            }
        }
    }

    public int InGameAvoidanceSeconds
    {
        get => Config.InGameAvoidanceSeconds;
        set
        {
            if (Config.InGameAvoidanceSeconds != value)
            {
                Config.InGameAvoidanceSeconds = value;
                PersistentTextService.AvoidanceSeconds = value;
                SaveConfigDebounced();
            }
        }
    }

    public int TransientTextDurationSeconds
    {
        get => Config.TransientTextDurationSeconds;
        set
        {
            if (Config.TransientTextDurationSeconds != value)
            {
                Config.TransientTextDurationSeconds = value;
                PersistentTextService.TransientDurationSeconds = value;
                SaveConfigDebounced();
            }
        }
    }

    public bool IsHotkeyEnabled
    {
        get => Config.IsHotkeyEnabled;
        set { if (Config.IsHotkeyEnabled != value) { Config.IsHotkeyEnabled = value; SaveConfigDebounced(); } }
    }
    public uint CustomHotkeyModifiers
    {
        get => Config.CustomHotkeyModifiers;
        set { if (Config.CustomHotkeyModifiers != value) { Config.CustomHotkeyModifiers = value; SaveConfigDebounced(); } }
    }
    public uint CustomHotkeyKey
    {
        get => Config.CustomHotkeyKey;
        set { if (Config.CustomHotkeyKey != value) { Config.CustomHotkeyKey = value; SaveConfigDebounced(); } }
    }
    public string CustomHotkeyString
    {
        get => Config.CustomHotkeyString;
        set { if (Config.CustomHotkeyString != value) { Config.CustomHotkeyString = value; SaveConfigDebounced(); } }
    }

    public bool IsImmersiveHotkeyEnabled
    {
        get => Config.IsImmersiveHotkeyEnabled;
        set { if (Config.IsImmersiveHotkeyEnabled != value) { Config.IsImmersiveHotkeyEnabled = value; SaveConfigDebounced(); } }
    }
    public uint CustomImmersiveHotkeyModifiers
    {
        get => Config.CustomImmersiveHotkeyModifiers;
        set { if (Config.CustomImmersiveHotkeyModifiers != value) { Config.CustomImmersiveHotkeyModifiers = value; SaveConfigDebounced(); } }
    }
    public uint CustomImmersiveHotkeyKey
    {
        get => Config.CustomImmersiveHotkeyKey;
        set { if (Config.CustomImmersiveHotkeyKey != value) { Config.CustomImmersiveHotkeyKey = value; SaveConfigDebounced(); } }
    }
    public string CustomImmersiveHotkeyString
    {
        get => Config.CustomImmersiveHotkeyString;
        set { if (Config.CustomImmersiveHotkeyString != value) { Config.CustomImmersiveHotkeyString = value; SaveConfigDebounced(); } }
    }
    public bool IsImmersiveMode { get; private set; } = false;

    // 语音识别与翻译选项
    // 用户期望状态；引擎的迟到启动事件不能把一次明确的关闭改回开启。
    public bool IsSpeechRecognitionEnabled
    {
        get => Config.IsSpeechRecognitionEnabled;
        set
        {
            if (Config.IsSpeechRecognitionEnabled != value)
            {
                Config.IsSpeechRecognitionEnabled = value;
                SpeechEngines.InvalidateIntent();
                SaveConfigDebounced();
            }
        }
    }
    public string SpeechLanguageTag
    {
        get => Config.SpeechLanguageTag;
        set { if (Config.SpeechLanguageTag != value) { Config.SpeechLanguageTag = value; SaveConfigDebounced(); UpdateLanguageVariable(); } }
    }
    public string AudioInputDeviceId
    {
        get => Config.AudioInputDeviceId;
        set { if (Config.AudioInputDeviceId != value) { Config.AudioInputDeviceId = value; SaveConfigDebounced(); } }
    }
    public int SubtitleQueueCapacity
    {
        get => Config.SubtitleQueueCapacity;
        set
        {
            if (Config.SubtitleQueueCapacity != value)
            {
                Config.SubtitleQueueCapacity = value;
                SaveConfigDebounced();
                // 容量裁剪与重绘由管线负责（它会读取最新容量）
                _subtitle.UpdateDisplay();
            }
        }
    }
    public bool ShowRecognizedText
    {
        get => Config.ShowRecognizedText;
        set { if (Config.ShowRecognizedText != value) { Config.ShowRecognizedText = value; SaveConfigDebounced(); } }
    }
    public bool IsTranslationEnabled
    {
        get => Config.IsTranslationEnabled;
        set { if (Config.IsTranslationEnabled != value) { Config.IsTranslationEnabled = value; SaveConfigDebounced(); } }
    }
    public bool ShowTranslatedText
    {
        get => Config.ShowTranslatedText;
        set { if (Config.ShowTranslatedText != value) { Config.ShowTranslatedText = value; SaveConfigDebounced(); } }
    }
    public bool ShowTranslationLatency
    {
        get => Config.ShowTranslationLatency;
        set { if (Config.ShowTranslationLatency != value) { Config.ShowTranslationLatency = value; SaveConfigDebounced(); } }
    }
    public bool IsAutoFillEnabled
    {
        get => Config.IsAutoFillEnabled;
        set { if (Config.IsAutoFillEnabled != value) { Config.IsAutoFillEnabled = value; SaveConfigDebounced(); } }
    }
    public int SpeechSliceDurationSeconds
    {
        get => Config.SpeechSliceDurationSeconds;
        set
        {
            if (Config.SpeechSliceDurationSeconds != value)
            {
                Config.SpeechSliceDurationSeconds = value;
                SpeechService.SliceDurationSeconds = value;
                SaveConfigDebounced();
            }
        }
    }
    public string GpuUsageMode
    {
        get => Config.GpuUsageMode;
        set
        {
            if (Config.GpuUsageMode != value)
            {
                Config.GpuUsageMode = value;
                SpeechService.PerformanceMode = SpeechService.ParseGpuMode(value);
                SaveConfigDebounced();
            }
        }
    }

    public string OllamaEndpoint
    {
        get => Config.OllamaEndpoint;
        set { if (Config.OllamaEndpoint != value) { Config.OllamaEndpoint = value; SaveConfigDebounced(); } }
    }
    public string OllamaModel
    {
        get => Config.OllamaModel;
        set { if (Config.OllamaModel != value) { Config.OllamaModel = value; SaveConfigDebounced(); } }
    }

    /// <summary>
    /// Ollama 请求频率档位（语音假说预翻译的节流）。
    /// 档位到「最小间隔 / 最小新增字数」的映射见
    /// <see cref="RequestFrequencyOptions"/> 与 <see cref="ResolveRequestFrequency"/>。
    /// </summary>
    public string OllamaRequestFrequency
    {
        get => Config.OllamaRequestFrequency;
        set { if (Config.OllamaRequestFrequency != value) { Config.OllamaRequestFrequency = value; SaveConfigDebounced(); } }
    }
    public string TargetLanguage
    {
        get => Config.TargetLanguage;
        set { if (Config.TargetLanguage != value) { Config.TargetLanguage = value; SaveConfigDebounced(); } }
    }

    /// <summary>
    /// 发送消息时自动翻译「我发出去的文字」。
    /// 结果写入变量 {sent_translation}，可被常驻模板引用或交给 TTS 朗读。
    /// </summary>
    public bool IsSentMessageTranslationEnabled
    {
        get => Config.IsSentMessageTranslationEnabled;
        set { if (Config.IsSentMessageTranslationEnabled != value) { Config.IsSentMessageTranslationEnabled = value; SaveConfigDebounced(); } }
    }
    /// <summary>兼容旧调用点：默认提示词的定义已移到 <see cref="AppConfig"/>。</summary>
    public const string DefaultOllamaPromptTemplate = AppConfig.DefaultOllamaPromptTemplate;
    public string OllamaPromptTemplate
    {
        get => Config.OllamaPromptTemplate;
        set { if (Config.OllamaPromptTemplate != value) { Config.OllamaPromptTemplate = value; SaveConfigDebounced(); } }
    }
}
