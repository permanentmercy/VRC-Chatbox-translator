using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
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


    public string SpeechModelType
    {
        get => SpeechService.CurrentModelType;
        set
        {
            if (SpeechService.CurrentModelType != value)
            {
                Config.SpeechModelType = value;
                _ = SpeechService.SwitchModelAsync(value);
                SaveConfigDebounced();
            }
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
    public bool IsSpeechRecognitionEnabled
    {
        get => Config.IsSpeechRecognitionEnabled;
        set { if (Config.IsSpeechRecognitionEnabled != value) { Config.IsSpeechRecognitionEnabled = value; SaveConfigDebounced(); } }
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
                lock (_subtitleEntries)
                {
                    while (_subtitleEntries.Count > Math.Max(1, value))
                    {
                        _subtitleEntries.RemoveAt(0);
                    }
                }
                UpdateCombinedSubtitles();
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
    public string TargetLanguage
    {
        get => Config.TargetLanguage;
        set { if (Config.TargetLanguage != value) { Config.TargetLanguage = value; SaveConfigDebounced(); } }
    }
    public const string DefaultOllamaPromptTemplate = "Translate the following text into {targetLanguage}. Output ONLY the translated text without explanation, notes, or quotes:\n\n{text}";
    public string OllamaPromptTemplate
    {
        get => Config.OllamaPromptTemplate;
        set { if (Config.OllamaPromptTemplate != value) { Config.OllamaPromptTemplate = value; SaveConfigDebounced(); } }
    }

    // ==========================================
    // IndexTTS 语音合成与虚拟麦克风推流属性
    // ==========================================
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
    /// 流式分句推流管线：对长句或多句进行语义断句，首句合成完成立即推流，后续分句边播放边后台生成无缝衔接
    /// </summary>
    public async Task<bool> SpeakTextStreamAsync(string text, bool force = false)
    {
        if (!force && !IsTtsEnabled)
        {
            return false;
        }

        var clauses = SplitIntoSpeechClauses(text);
        if (clauses.Count == 0) return false;

        TtsActiveStateChanged?.Invoke(true);
        TtsProgressChanged?.Invoke(-1);
        SpeechStatusUpdated?.Invoke(clauses.Count > 1 ? $"正在合成语音 (1/{clauses.Count})..." : "正在合成语音...");

        try
        {
            bool ok = await TtsService.PlayStreamAsync(
                clauses,
                TtsVirtualMicDeviceId,
                TtsMonitorDeviceId,
                IsTtsMonitorEnabled,
                TtsOutputVolume,
                TtsMonitorVolume,
                onClauseSynthesized: (idx, total) =>
                {
                    double pct = (idx + 1.0) / total * 100.0;
                    TtsProgressChanged?.Invoke(pct);
                    SpeechStatusUpdated?.Invoke($"正在合成语音 ({idx + 1}/{total})...");
                },
                onClausePlaying: (idx, total) =>
                {
                    SpeechStatusUpdated?.Invoke($"正在推流播放 ({idx}/{total})...");
                }
            );

            if (ok)
            {
                AddLog("TTS Speech", $"流式语音推流完成: \"{text}\" ({clauses.Count}个分句)", true);
            }
            return ok;
        }
        catch (Exception ex)
        {
            AddLog("TTS Error", $"流式推流异常: {ex.Message}", false, ex.Message);
            return false;
        }
        finally
        {
            TtsProgressChanged?.Invoke(100);
            TtsActiveStateChanged?.Invoke(false);
            SpeechStatusUpdated?.Invoke("就绪");
        }
    }

    /// <summary>
    /// 仅在本地耳机流式试听
    /// </summary>
    public async Task<bool> SpeakLocalPreviewStreamAsync(string text)
    {
        var clauses = SplitIntoSpeechClauses(text);
        if (clauses.Count == 0) return false;

        TtsActiveStateChanged?.Invoke(true);
        TtsProgressChanged?.Invoke(-1);
        SpeechStatusUpdated?.Invoke("正在合成试听语音...");

        try
        {
            return await TtsService.PlayStreamAsync(
                clauses,
                virtualMicDeviceId: null,
                monitorDeviceId: TtsMonitorDeviceId,
                enableMonitor: true,
                micVolume: 0f,
                monitorVolume: TtsMonitorVolume,
                onClauseSynthesized: (idx, total) =>
                {
                    TtsProgressChanged?.Invoke((idx + 1.0) / total * 100.0);
                    SpeechStatusUpdated?.Invoke($"正在合成试听 ({idx + 1}/{total})...");
                },
                onClausePlaying: (idx, total) =>
                {
                    SpeechStatusUpdated?.Invoke($"正在试听播放 ({idx}/{total})...");
                }
            );
        }
        finally
        {
            TtsProgressChanged?.Invoke(100);
            TtsActiveStateChanged?.Invoke(false);
            SpeechStatusUpdated?.Invoke("就绪");
        }
    }

    /// <summary>
    /// 智能语义断句算法：将整段长文本切分为呼吸停顿自然的子句，并自适应补齐句号，防止模型自回归尾音发散
    /// 1. 先按显式标点与换行切分；
    /// 2. 对无标点或单句过长（>12-14字）的长段落，在空格、常见逻辑连接词（然后、但是、而且等）或适度字数处进行语义细分；
    /// 3. 合理控制颗粒度，使首句能够极速（~200-300ms）出声，后续分句边播放边后台生成无缝衔接。
    /// </summary>
    public static List<string> SplitIntoSpeechClauses(string input)
    {
        var rawClauses = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return rawClauses;

        // 1. 根据显式标点与换行切分
        char[] delims = new[] { '。', '！', '？', '；', '，', '、', '.', '!', '?', ';', ',', '\n', '\r', '…', '~' };
        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (Array.IndexOf(delims, c) >= 0)
            {
                string clause = input.Substring(start, i - start + 1).Trim();
                if (!string.IsNullOrWhiteSpace(clause))
                {
                    rawClauses.Add(clause);
                }
                start = i + 1;
            }
        }
        if (start < input.Length)
        {
            string tail = input.Substring(start).Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                rawClauses.Add(tail);
            }
        }

        if (rawClauses.Count == 0)
        {
            rawClauses.Add(input);
        }

        // 2. 二次智能细分：若某个分句过长（超过 14 个字符且无断点），按空格、常见转折/并列连词或自然字数切分
        var subDivided = new List<string>();
        string[] connectors = new[] { "然后", "但是", "而且", "所以", "并且", "因为", "如果", "虽然", "不过", "以及", "或者", "就是", "还有", "另外", "同时", "接着", "之后" };

        foreach (var clause in rawClauses)
        {
            string trimmed = clause.Trim();
            if (trimmed.Length <= 14)
            {
                subDivided.Add(trimmed);
                continue;
            }

            // 针对超长句进行逐段切割
            string remaining = trimmed;
            while (remaining.Length > 14)
            {
                int splitIdx = -1;

                // 2.1 尝试寻找空格
                int spaceIdx = remaining.IndexOf(' ', 6);
                if (spaceIdx > 0 && spaceIdx <= 14)
                {
                    splitIdx = spaceIdx;
                }

                // 2.2 尝试寻找中文逻辑连接词
                if (splitIdx < 0)
                {
                    foreach (var conn in connectors)
                    {
                        int cIdx = remaining.IndexOf(conn, 5, StringComparison.Ordinal);
                        if (cIdx >= 5 && cIdx <= 14)
                        {
                            splitIdx = cIdx;
                            break;
                        }
                    }
                }

                // 2.3 无明显连接词，则直接在自然边界（第 10-12 字）切分
                if (splitIdx < 0)
                {
                    splitIdx = Math.Min(12, remaining.Length / 2);
                }

                string chunk = remaining.Substring(0, splitIdx).Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    subDivided.Add(chunk);
                }
                remaining = remaining.Substring(splitIdx).Trim();
            }

            if (!string.IsNullOrWhiteSpace(remaining))
            {
                subDivided.Add(remaining);
            }
        }

        // 3. 智能粘合过短的单个字或语气词（纯文字长度 <= 1，如单个“好”或“嗯”，避免切得太碎导致音素失真）
        var merged = new List<string>();
        for (int i = 0; i < subDivided.Count; i++)
        {
            string item = subDivided[i];
            string plain = item.Trim('。', '.', '！', '!', '？', '?', '，', ',', '、', '…', '~', ' ');
            if (merged.Count > 0 && plain.Length <= 1)
            {
                merged[^1] = merged[^1].TrimEnd('。', '.', '！', '!', '？', '?', '，', ',', '、') + "，" + item;
            }
            else
            {
                merged.Add(item);
            }
        }

        // 4. 对每个分句规范化标点与声学清洗
        var result = new List<string>();
        foreach (var c in merged)
        {
            string prepared = PrepareTextForTts(c);
            if (!string.IsNullOrWhiteSpace(prepared))
            {
                result.Add(prepared);
            }
        }

        return result;
    }

    /// <summary>
    /// 兼容调用：默认通过流式分句管线推流输出
    /// </summary>
    public async Task<TtsSynthesisResult> SpeakTextAsync(string text, bool force = false)
    {
        bool ok = await SpeakTextStreamAsync(text, force);
        return new TtsSynthesisResult(ok, null, 0, 0, 24000, ok ? null : "流式合成推流未完成");
    }

    // 实时状态
    public string LastRecognizedText { get; private set; } = string.Empty;
    public string LastTranslatedText { get; private set; } = string.Empty;
    public long LastTranslationLatencyMs { get; private set; } = 0;
    public string SpeechStatus { get; private set; } = "就绪";

    public event Action<bool>? SpeechRecognitionStateChanged;
    public event Action<string>? SpeechRecognizedUpdated;
    public event Action<string>? SpeechHypothesisUpdated;
    public event Action<string, long>? TranslationUpdated;
    public event Action<string>? SpeechStatusUpdated;
    public event Action<bool>? TtsActiveStateChanged;
    public event Action<double>? TtsProgressChanged;
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
        LoadConfig();

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

        SpeechService.ListeningStateChanged += listening =>
        {
            if (string.Equals(SpeechEngine, "Whisper", StringComparison.OrdinalIgnoreCase))
            {
                IsSpeechRecognitionEnabled = listening;
                if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
                {
                    App.DispatcherQueueInstance.TryEnqueue(() =>
                    {
                        SpeechRecognitionStateChanged?.Invoke(listening);
                        DisplaySettingsChanged?.Invoke();
                    });
                }
                else
                {
                    SpeechRecognitionStateChanged?.Invoke(listening);
                    DisplaySettingsChanged?.Invoke();
                }
            }
        };

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

        LiveCaptionsService.RunningStateChanged += running =>
        {
            if (string.Equals(SpeechEngine, "LiveCaptions", StringComparison.OrdinalIgnoreCase))
            {
                IsSpeechRecognitionEnabled = running;
                if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
                {
                    App.DispatcherQueueInstance.TryEnqueue(() =>
                    {
                        SpeechRecognitionStateChanged?.Invoke(running);
                        DisplaySettingsChanged?.Invoke();
                    });
                }
                else
                {
                    SpeechRecognitionStateChanged?.Invoke(running);
                    DisplaySettingsChanged?.Invoke();
                }
            }
        };

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
