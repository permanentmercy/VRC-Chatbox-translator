using System;
using System.Linq;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 语音识别/字幕相关的配置属性（分部类，机械拆分）。
///
/// 从 SettingsService.cs 拆出来的原因：这些是纯属性转发（get/set → Config + 保存 + 少量联动），
/// 与 OSC 发送、TTS 朗读、退出流程无关。按域集中后主文件只剩「状态、事件、装配」。
/// 无行为变化。
/// </summary>
public partial class SettingsService
{
    public string SpeechModelType
    {
        get => SpeechService.CurrentModelType;
        set
        {
            string targetModel = (value ?? "tiny").Trim().ToLowerInvariant();
            if (targetModel != "tiny" && targetModel != "base" && targetModel != "small") targetModel = "tiny";

            // CurrentModelType 是已加载的模型，不能用它过滤尚在排队的用户意图：
            // tiny→base 加载期间再选 tiny 也必须入队。服务会在模型锁内安全去重。
            Config.SpeechModelType = targetModel;
            _ = SpeechService.SwitchModelAsync(targetModel);
            SaveConfigDebounced();
        }
    }

    public string SpeechEngine
    {
        get => string.IsNullOrWhiteSpace(Config.SpeechEngine) ? "LiveCaptions" : Config.SpeechEngine;
        set
        {
            if (Config.SpeechEngine != value)
            {
                Config.SpeechEngine = value;
                SpeechEngines.InvalidateIntent();
                SaveConfigDebounced();
                UpdateLanguageVariable();
                if (IsSpeechRecognitionEnabled)
                {
                    _ = ApplySpeechStateAsync();
                }
            }
        }
    }

    public bool LiveCaptionsHideNativeWindow
    {
        get => Config.LiveCaptionsHideNativeWindow;
        set
        {
            if (Config.LiveCaptionsHideNativeWindow != value)
            {
                Config.LiveCaptionsHideNativeWindow = value;
                SaveConfigDebounced();
            }
        }
    }

    public string LiveCaptionsLanguageCode
    {
        get => string.IsNullOrWhiteSpace(Config.LiveCaptionsLanguageCode) ? "zh-CN" : Config.LiveCaptionsLanguageCode;
        set
        {
            if (Config.LiveCaptionsLanguageCode != value)
            {
                Config.LiveCaptionsLanguageCode = value;
                SaveConfigDebounced();
                UpdateLanguageVariable();
                _ = LiveCaptionsService.SwitchLanguageAsync(value);
            }
        }
    }

    public void SetLiveCaptionsLanguageCodeSilent(string code)
    {
        string norm = string.IsNullOrWhiteSpace(code) ? "zh-CN" : code;
        if (Config.LiveCaptionsLanguageCode != norm)
        {
            Config.LiveCaptionsLanguageCode = norm;
            SaveConfigDebounced();
            UpdateLanguageVariable();
        }
    }

    public void UpdateLanguageVariable()
    {
        string langName = "中文";
        string langCode = "zh-CN";

        if (string.Equals(SpeechEngine, "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            string tag = string.IsNullOrWhiteSpace(SpeechLanguageTag) ? "auto" : SpeechLanguageTag;
            langCode = tag;
            langName = tag switch
            {
                "zh" => "中文",
                "en" => "English",
                "ja" => "日本語",
                "ko" => "한국어",
                "ru" => "Русский",
                "fr" => "Français",
                "de" => "Deutsch",
                "es" => "Español",
                _ => "自动识别"
            };
        }
        else
        {
            langCode = LiveCaptionsLanguageCode;
            var match = LiveCaptionsService.SupportedLanguages.FirstOrDefault(l => string.Equals(l.Code, langCode, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                langName = match.MatchKeyword;
            }
            else
            {
                langName = langCode;
            }
        }

        VariableService.SetVariable("language", langName, "当前字幕语言", "当前语音识别/字幕引擎生效的语言名称 (如 中文, 日本語, English)");
        VariableService.SetVariable("language_code", langCode, "当前字幕语言代码", "当前字幕引擎生效的语言代码 (如 zh-CN, ja-JP, en-US)");
    }

    public bool LiveCaptionsAutoDetectLanguage
    {
        get => Config.LiveCaptionsAutoDetectLanguage;
        set
        {
            Config.LiveCaptionsAutoDetectLanguage = value;
            SaveConfigDebounced();
            _ = LiveCaptionsAutoSwitcher.SetEnabledAsync(value, Config.LiveCaptionsAutoDetectIntervalSeconds);
            LiveCaptionsAutoDetectChanged?.Invoke(value);
        }
    }

    public int LiveCaptionsAutoDetectIntervalSeconds
    {
        get => Config.LiveCaptionsAutoDetectIntervalSeconds <= 0 ? 2 : Config.LiveCaptionsAutoDetectIntervalSeconds;
        set
        {
            if (Config.LiveCaptionsAutoDetectIntervalSeconds != value)
            {
                Config.LiveCaptionsAutoDetectIntervalSeconds = value;
                SaveConfigDebounced();
                LiveCaptionsAutoSwitcher.UpdateInterval(value);
            }
        }
    }

    public event Action<bool>? LiveCaptionsAutoDetectChanged;

    public string TargetAudioProcessName
    {
        get => string.IsNullOrWhiteSpace(Config.TargetAudioProcessName) ? "VRChat" : Config.TargetAudioProcessName;
        set
        {
            var clean = AudioProcessService.NormalizeProcessName(value);
            if (string.IsNullOrWhiteSpace(clean)) clean = "VRChat";

            if (!string.Equals(Config.TargetAudioProcessName, clean, StringComparison.OrdinalIgnoreCase))
            {
                Config.TargetAudioProcessName = clean;
                SaveConfigDebounced();
                TargetAudioProcessChanged?.Invoke(clean);
                if (IsSpeechRecognitionEnabled && AudioInputDeviceId != null && AudioInputDeviceId.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = SwitchAudioDeviceAsync("process:" + clean, $"[游戏进程隔离] 仅监听游戏: {clean}");
                }
            }
        }
    }

    public event Action<string>? TargetAudioProcessChanged;
}
