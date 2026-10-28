using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using VrcChatboxDemo.Services.Configuration;
using VrcChatboxDemo.Services.Speech;
using VrcChatboxDemo.Services.Tts;

namespace VrcChatboxDemo.Services;

public class LogEntryViewModel
{
    public string TimeString { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public Brush StatusColorBrush { get; set; } = new SolidColorBrush(Colors.Green);
}

public partial class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    public OscService OscService { get; } = new();
    public HotkeyService HotkeyService { get; } = new();
    public SpeechService SpeechService { get; } = new();
    public LiveCaptionsService LiveCaptionsService { get; } = new();
    public LiveCaptionsLanguageAutoSwitcher LiveCaptionsAutoSwitcher { get; }
    public OllamaService OllamaService { get; } = new();
    public InboundService InboundService { get; } = new();
    public PersistentTextService PersistentTextService { get; } = new();
    public VariableService VariableService { get; } = new();
    public VrcInGameGuard VrcInGameGuard { get; } = new();
    public ObservableCollection<LogEntryViewModel> Logs { get; } = new();

    public AppConfig Config { get; private set; } = new();


    // 语音识别/字幕相关属性（SpeechModelType / SpeechEngine / LiveCaptions* /
    // TargetAudioProcessName / UpdateLanguageVariable）已移到
    // SettingsService.Properties.Speech.cs。

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

    // ==========================================
    // IndexTTS 语音合成与虚拟麦克风推流属性
    // 已移到 SettingsService.Properties.Tts.cs
    // ==========================================

    // TTS 朗读编排（SpeakTextStreamAsync / SpeakLocalPreviewStreamAsync /
    // SplitIntoSpeechClauses / SpeakTextAsync）已移到 SettingsService.Tts.cs。

    // 实时状态（字幕文本由 SubtitlePipeline 维护，这里只做转发）
    public string LastRecognizedText => _subtitle.LastRecognizedText;
    public string LastTranslatedText => _subtitle.LastTranslatedText;
    public long LastTranslationLatencyMs => _subtitle.LastTranslationLatencyMs;
    public string SpeechStatus { get; private set; } = "就绪";

    public event Action<bool>? SpeechRecognitionStateChanged;
    public event Action<string>? SpeechRecognizedUpdated;
    public event Action<string>? SpeechHypothesisUpdated;
    public event Action<string, long>? TranslationUpdated;
    public event Action<string>? SpeechStatusUpdated;
    public event Action<bool>? TtsActiveStateChanged;
    public event Action<double>? TtsProgressChanged;
    public event Action<string>? TtsStatusUpdated;
    public event Action? DisplaySettingsChanged;
    public event Action<string>? RequestAutoFillInput;
    public event Action<bool>? ImmersiveModeToggled;
    public event Action<int>? RequestImmersiveResize;

    public void TriggerImmersiveResize(int height)
    {
        RequestImmersiveResize?.Invoke(height);
    }

    public void TriggerAutoFill(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
        {
            App.DispatcherQueueInstance.TryEnqueue(() => RequestAutoFillInput?.Invoke(text));
        }
        else
        {
            RequestAutoFillInput?.Invoke(text);
        }
    }

    private SettingsService()
    {
        _subtitle = new SubtitlePipeline(() => SubtitleQueueCapacity);

        LoadConfig();

        // 字幕管线的显示事件转发给界面订阅者（UI 只认 SettingsService 的公开事件）
        _subtitle.RecognizedUpdated += text => SpeechRecognizedUpdated?.Invoke(text);
        _subtitle.HypothesisUpdated += text => SpeechHypothesisUpdated?.Invoke(text);
        _subtitle.TranslationUpdated += (text, latency) => TranslationUpdated?.Invoke(text, latency);
        _subtitle.DisplayChanged += () => DisplaySettingsChanged?.Invoke();

        LiveCaptionsAutoSwitcher = new LiveCaptionsLanguageAutoSwitcher(LiveCaptionsService, this);
        if (Config.LiveCaptionsAutoDetectLanguage)
        {
            _ = LiveCaptionsAutoSwitcher.SetEnabledAsync(true, Config.LiveCaptionsAutoDetectIntervalSeconds);
        }

        OscService.MessageSent += OnMessageSent;

        InboundService.VariableService = VariableService;
        InboundService.PersistentTextService = PersistentTextService;

        VariableService.VariableUpdated += (varName, val) =>
        {
            _ = PersistentTextService.OnVariableUpdatedAsync(varName, VariableService, OscService);
        };

        VrcInGameGuard.InGameMessageSent += () =>
        {
            PersistentTextService.NotifyInGameMessageSent();
        };
        VrcInGameGuard.InGameTyping += () =>
        {
            PersistentTextService.NotifyInGameTyping();
        };
        VrcInGameGuard.IsEnabled = Config.InGameAvoidanceEnabled;
        PersistentTextService.EnableInGameAvoidance = Config.InGameAvoidanceEnabled;
        PersistentTextService.AvoidanceSeconds = Config.InGameAvoidanceSeconds;
        PersistentTextService.TransientDurationSeconds = Config.TransientTextDurationSeconds;
        VrcInGameGuard.Start();

        InboundService.MessageReceived += (text, source) =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.StartsWith("/avatar/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/tracking/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/input/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddLog($"API Inbound ({source})", text, true);
            if (InboundService.AutoSendToVrc)
            {
                _ = FinalizeSendAsync(text);
            }
            else
            {
                TriggerAutoFill(text);
            }
        };

        try
        {
            if (Config.InboundEnabled)
            {
                InboundService.Start(Config.InboundHttpPort, Config.InboundOscPort);
            }
        }
        catch { }

        HotkeyService.ImmersiveHotkeyPressed += () =>
        {
            if (App.DispatcherQueueInstance != null)
            {
                App.DispatcherQueueInstance.TryEnqueue(ToggleImmersiveMode);
            }
            else
            {
                ToggleImmersiveMode();
            }
        };

        SpeechService.SpeechRecognized += text =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechRecognized(text));
            }
            else
            {
                OnSpeechRecognized(text);
            }
        };

        SpeechService.SpeechHypothesis += interimText =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechHypothesis(interimText));
            }
            else
            {
                OnSpeechHypothesis(interimText);
            }
        };

        SpeechService.StatusChanged += status =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = status;
                    SpeechStatusUpdated?.Invoke(status);
                });
            }
            else
            {
                SpeechStatus = status;
                SpeechStatusUpdated?.Invoke(status);
            }
        };

        SpeechService.ErrorOccurred += err =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = err;
                    AddLog("Speech", err, false);
                    SpeechStatusUpdated?.Invoke(err);
                });
            }
            else
            {
                SpeechStatus = err;
                AddLog("Speech", err, false);
                SpeechStatusUpdated?.Invoke(err);
            }
        };

        SpeechService.ListeningStateChanged += listening => SpeechEngines.OnEngineRunningChanged("Whisper", listening);

        LiveCaptionsService.SpeechHypothesis += interimText =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechHypothesis(interimText));
            }
            else
            {
                OnSpeechHypothesis(interimText);
            }
        };

        LiveCaptionsService.SpeechRecognized += text =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechRecognized(text));
            }
            else
            {
                OnSpeechRecognized(text);
            }
        };

        LiveCaptionsService.StatusChanged += status =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = status;
                    SpeechStatusUpdated?.Invoke(status);
                });
            }
            else
            {
                SpeechStatus = status;
                SpeechStatusUpdated?.Invoke(status);
            }
        };

        LiveCaptionsService.ErrorOccurred += err =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = err;
                    AddLog("LiveCaptions", err, false);
                    SpeechStatusUpdated?.Invoke(err);
                });
            }
            else
            {
                SpeechStatus = err;
                AddLog("LiveCaptions", err, false);
                SpeechStatusUpdated?.Invoke(err);
            }
        };

        LiveCaptionsService.RunningStateChanged += running => SpeechEngines.OnEngineRunningChanged("LiveCaptions", running);

        PersistentTextService.IsEnabled = Config.IsPersistentTextEnabled;
        PersistentTextService.CustomText = Config.PersistentCustomText;
        if (Config.IsPersistentTextEnabled && !string.IsNullOrWhiteSpace(Config.PersistentCustomText))
        {
            PersistentTextService.RestartLoop(VariableService, OscService);
        }

        UpdateLanguageVariable();
    }

    public void ToggleImmersiveMode()
    {
        SetImmersiveMode(!IsImmersiveMode);
    }

    public void SetImmersiveMode(bool enable)
    {
        if (IsImmersiveMode == enable) return;
        IsImmersiveMode = enable;
        ImmersiveModeToggled?.Invoke(enable);
    }

    public void NotifyDisplaySettingsChanged()
    {
        DisplaySettingsChanged?.Invoke();
    }

    public void AddLog(string address, string content, bool success, string? error = null)
    {
        void DoAdd()
        {
            var brush = success
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 38, 166, 91))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 220, 53, 69));

            var log = new LogEntryViewModel
            {
                TimeString = DateTime.Now.ToString("HH:mm:ss"),
                Address = address,
                Content = content,
                StatusText = success ? "成功" : (string.IsNullOrEmpty(error) ? "失败" : $"失败: {error}"),
                StatusColorBrush = brush
            };

            Logs.Insert(0, log);
            if (Logs.Count > 100) Logs.RemoveAt(Logs.Count - 1);
        }

        if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
        {
            App.DispatcherQueueInstance.TryEnqueue(DoAdd);
        }
        else
        {
            DoAdd();
        }
    }

    public void ResetToDefault()
    {
        Host = "127.0.0.1";
        Port = 9000;
        IsDirectSendEnabled = true;
        IsSoundEnabled = true;
        IsTypingEnabled = false;
        IsLiveTypingEnabled = true;
        IsEnterSendEnabled = true;
        IsClearOnSendEnabled = true;
        IsPersistentTextEnabled = false;
        PersistentCustomText = string.Empty;
        _ = PersistentTextService.ClearAsync(OscService);

        IsHotkeyEnabled = true;
        CustomHotkeyModifiers = HotkeyService.MOD_CONTROL | HotkeyService.MOD_SHIFT;
        CustomHotkeyKey = 0x43;
        CustomHotkeyString = "Ctrl + Shift + C";

        IsImmersiveHotkeyEnabled = true;
        CustomImmersiveHotkeyModifiers = HotkeyService.MOD_CONTROL | HotkeyService.MOD_SHIFT;
        CustomImmersiveHotkeyKey = 0x5A;
        CustomImmersiveHotkeyString = "Ctrl + Shift + Z";
        ApplyHotkey();

        IsSpeechRecognitionEnabled = false;
        SpeechEngine = "LiveCaptions";
        LiveCaptionsHideNativeWindow = true;
        SpeechLanguageTag = string.Empty;
        AudioInputDeviceId = string.Empty;
        SubtitleQueueCapacity = 3;
        ClearSubtitleQueue();
        ShowRecognizedText = true;
        IsTranslationEnabled = false;
        ShowTranslatedText = true;
        ShowTranslationLatency = true;
        IsAutoFillEnabled = false;
        OllamaEndpoint = "http://127.0.0.1:11434";
        OllamaModel = "RogerBen/HY-MT2-1.8B:latest";
        TargetLanguage = "英语 (English)";
        OllamaPromptTemplate = DefaultOllamaPromptTemplate;
        _ = ApplySpeechStateAsync();

        Config = new AppConfig();
        SaveConfigImmediately();
    }
}
