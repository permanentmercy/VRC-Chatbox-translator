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
    // 由 ComposeLiveCaptionsAutoSwitcher 在装配阶段赋值
    public LiveCaptionsLanguageAutoSwitcher LiveCaptionsAutoSwitcher { get; private set; } = null!;
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

    // 发送/交互/热键/字幕/翻译相关属性已移到
    // SettingsService.Properties.Interaction.cs。

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

        // 装配清单（实现在 SettingsService.Composition.cs，顺序即启动顺序）
        ComposeSubtitleEvents();
        ComposeLiveCaptionsAutoSwitcher();
        ComposeOscLogging();
        ComposeInboundServices();
        ComposeInGameGuard();
        ComposeInboundMessaging();
        ComposeHotkeys();
        ComposeSpeechEvents();
        ComposeLiveCaptionsEvents();
        ComposePersistentText();

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
