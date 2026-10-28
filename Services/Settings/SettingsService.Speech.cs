using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VrcChatboxDemo.Services.Configuration;
using VrcChatboxDemo.Services.Speech;
using VrcChatboxDemo.Services.Translation;

namespace VrcChatboxDemo.Services;

public partial class SettingsService
{
    // 字幕队列与显示合成已移入 Core：SubtitlePipeline。
    // 在构造函数里创建（容量来自配置属性，不能写在字段初始化器里）。
    private readonly SubtitlePipeline _subtitle;

    private CancellationTokenSource? _finalTranslationCts;
    private CancellationTokenSource? _interimTranslationCts;
    private readonly object _translationLock = new();
    private string _lastInterimTranslationText = string.Empty;
    private DateTime _lastInterimTranslationTime = DateTime.MinValue;
    private long _finalTranslationSequence = 0;
    private long _interimTranslationSequence = 0;
    private long _sentTranslationSequence = 0;
    private bool _isNewSentence = true;
    private string _lastHypothesisSpeech = string.Empty;

    // 语音引擎启停/切换的状态机已移入 Core：SpeechEngineCoordinator。
    // 这里只保留门面转发与「宿主事实」的实现（配置属性、状态栏、UI 通知）。
    private SpeechEngineCoordinator? _speechEngines;

    private SpeechEngineCoordinator SpeechEngines => _speechEngines ??= new SpeechEngineCoordinator(
        new SpeechEngineContext(this),
        new WhisperEngineControl(SpeechService, () => (SpeechLanguageTag, AudioInputDeviceId)),
        new LiveCaptionsEngineControl(LiveCaptionsService, () => Config.LiveCaptionsHideNativeWindow));

    /// <summary>把 SettingsService 的配置/事件适配成协调器需要的上下文。</summary>
    private sealed class SpeechEngineContext : ISpeechEngineContext
    {
        private readonly SettingsService _owner;
        public SpeechEngineContext(SettingsService owner) => _owner = owner;

        public string Engine => _owner.SpeechEngine;

        public bool Enabled
        {
            get => _owner.IsSpeechRecognitionEnabled;
            set => _owner.IsSpeechRecognitionEnabled = value;
        }

        public void NotifyDisplaySettingsChanged() => _owner.DisplaySettingsChanged?.Invoke();

        public void NotifyRecognitionStateChanged() => _owner.NotifySpeechRecognitionState();

        public void SetStatus(string message)
        {
            _owner.SpeechStatus = message;
            _owner.SpeechStatusUpdated?.Invoke(message);
        }
    }

    private void NotifySpeechRecognitionState()
    {
        void Notify()
        {
            // 在 UI 队列真正执行时读取最新值，避免旧事件把开关再次弹回。
            SpeechRecognitionStateChanged?.Invoke(IsSpeechRecognitionEnabled);
            DisplaySettingsChanged?.Invoke();
        }

        if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
        {
            App.DispatcherQueueInstance.TryEnqueue(Notify);
        }
        else
        {
            Notify();
        }
    }

    public void ClearSubtitleQueue()
    {
        lock (_translationLock)
        {
            _finalTranslationCts?.Cancel();
            _finalTranslationCts?.Dispose();
            _finalTranslationCts = null;

            _interimTranslationCts?.Cancel();
            _interimTranslationCts?.Dispose();
            _interimTranslationCts = null;

            _lastInterimTranslationText = string.Empty;
            _lastInterimTranslationTime = DateTime.MinValue;
        }

        _subtitle.Clear();
        _isNewSentence = true;
        _lastHypothesisSpeech = string.Empty;
        LiveCaptionsService.ResetHistory();

        SpeechRecognizedUpdated?.Invoke(string.Empty);
        TranslationUpdated?.Invoke(string.Empty, 0);
        VariableService.SetVariable("speech", string.Empty);
        VariableService.SetVariable("translation", string.Empty);
    }

    private void OnSpeechHypothesis(string interimText)
    {
        string cleanHypo = (interimText ?? string.Empty).Replace("\r", "").Replace("\n", " ").Trim();
        if (!string.IsNullOrWhiteSpace(cleanHypo) && !string.Equals(cleanHypo, _lastHypothesisSpeech, StringComparison.Ordinal))
        {
            _lastHypothesisSpeech = cleanHypo;
            if (_isNewSentence)
            {
                _isNewSentence = false;
                if (IsTranslationEnabled)
                {
                    VariableService.SetVariable("translation", string.Empty);
                }
            }
            VariableService.SetVariable("speech", cleanHypo);
        }

        _subtitle.UpdateDisplay(interimText);
        TryTriggerInterimTranslation(interimText);
    }

    // ======================================================================
    // Ollama 请求频率档位
    //
    // 说话过程中语音识别会不断吐出「假说」文本，若每次变化都去请求 Ollama，
    // 本地模型会被打满、反而拖慢识别与字幕刷新。所以按档位控制两个门槛：
    //   IntervalMs   —— 距离上次预翻译的最小间隔
    //   MinNewChars  —— 相比上次预翻译至少要多出几个字（避免同一个半句反复翻译）
    // 「定稿句」的翻译不经过这里，永远只发一次，不受档位影响。
    // ======================================================================

    // 档位表与解析逻辑已移入 Core：TranslationPolicy（纯策略，可独立测试）。
    // 这里保留同名成员做兼容转发，UI 无需改动。

    /// <summary>兼容转发：档位定义见 <see cref="TranslationPolicy.FrequencyOptions"/>。</summary>
    public readonly record struct RequestFrequencyOption(
        string Mode, string Label, int IntervalMs, int MinNewChars);

    /// <summary>兼容转发：默认档位。</summary>
    public const string DefaultRequestFrequencyMode = AppConfig.DefaultRequestFrequencyMode;

    /// <summary>兼容转发：可选档位表。</summary>
    public static readonly IReadOnlyList<RequestFrequencyOption> RequestFrequencyOptions = new[]
    {
        new RequestFrequencyOption("Off",    "关闭（只翻译定稿句，最省资源）", 0,    int.MaxValue),
        new RequestFrequencyOption("Low",    "低频（2.0 秒 / 次）",            2000, 8),
        new RequestFrequencyOption("Normal", "标准（0.8 秒 / 次，默认）",      800,  3),
        new RequestFrequencyOption("High",   "偏高（0.4 秒 / 次）",            400,  2),
        new RequestFrequencyOption("Ultra",  "高频（0.2 秒 / 次，最跟手）",    200,  1),
    };

    /// <summary>兼容转发：解析档位字符串，无法识别时回退到默认档位。</summary>
    public static RequestFrequencyOption ResolveRequestFrequency(string? mode)
    {
        var resolved = TranslationPolicy.ResolveFrequency(mode);
        foreach (var option in RequestFrequencyOptions)
        {
            if (string.Equals(option.Mode, resolved.Mode, StringComparison.OrdinalIgnoreCase)) return option;
        }
        return RequestFrequencyOptions[0];
    }

    private void TryTriggerInterimTranslation(string? interimText)
    {
        if (!IsTranslationEnabled) return;
        if (string.IsNullOrWhiteSpace(interimText)) return;

        // 请求频率档位：Off 档直接不做假说预翻译
        var freq = ResolveRequestFrequency(OllamaRequestFrequency);
        if (freq.IntervalMs <= 0) return;

        string clean = interimText.Replace("\r", "").Replace("\n", " ").Trim();
        if (string.IsNullOrWhiteSpace(clean)) return;

        // 判定是否达到“中途”条件：
        // 1. 包含逗号/分句符（， , 、 ； ;），表明前半句语义已成型；
        // 2. 或者有效字数达到门槛（>= 6 个字符）；
        bool hasComma = clean.IndexOfAny(new[] { '，', ',', '、', '；', ';' }) >= 0;
        bool isHalfway = hasComma || clean.Length >= 6;
        if (!isHalfway) return;

        // 防抖节流检查：按当前档位要求的最小间隔 + 最小新增字数
        DateTime now = DateTime.UtcNow;
        lock (_translationLock)
        {
            if (now - _lastInterimTranslationTime < TimeSpan.FromMilliseconds(freq.IntervalMs))
            {
                return;
            }

            if (!string.IsNullOrEmpty(_lastInterimTranslationText) &&
                clean.Length < _lastInterimTranslationText.Length + freq.MinNewChars &&
                clean.EndsWith(_lastInterimTranslationText, StringComparison.Ordinal))
            {
                return;
            }

            _lastInterimTranslationTime = now;
            _lastInterimTranslationText = clean;
        }

        long seq = System.Threading.Interlocked.Increment(ref _interimTranslationSequence);
        CancellationTokenSource cts;
        lock (_translationLock)
        {
            // 核心修复：仅取消上一个假说预翻译任务，绝对不取消或干扰已定稿句子的最终翻译！
            _interimTranslationCts?.Cancel();
            _interimTranslationCts?.Dispose();
            _interimTranslationCts = new CancellationTokenSource();
            cts = _interimTranslationCts;
        }

        _ = Task.Run(async () =>
        {
            var token = cts.Token;
            try
            {
                string targetLang = ParseTargetLanguage(TargetLanguage);
                var result = await OllamaService.TranslateAsync(clean, targetLang, OllamaModel, OllamaEndpoint, OllamaPromptTemplate, token);
                if (token.IsCancellationRequested || seq < Volatile.Read(ref _interimTranslationSequence)) return;

                string cleanTrans = (result.Text ?? string.Empty).Replace("\r", "").Replace("\n", " ").Trim();
                if (string.IsNullOrWhiteSpace(cleanTrans)) return;

                lock (_translationLock)
                {
                    _subtitle.InterimTranslation = cleanTrans;
                }

                // 提前更新模板变量，让游戏内头顶气泡即时展现预翻译
                VariableService.SetVariable("translation", cleanTrans);

                // 同步刷新假说字幕与延迟显示
                UpdateCombinedSubtitles(interimText);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InterimTranslation] Error: {ex.Message}");
            }
        });
    }

    private void OnSpeechRecognized(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Replace("\r", "").Replace("\n", " ").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        AddLog("Speech", text, true);

        // 提交当前识别出的完整句子到字幕队列（容量裁剪由管线负责）
        _subtitle.AddFinalSentence(text);

        lock (_translationLock)
        {
            // 完整句子已到，取消假说预翻译
            _interimTranslationCts?.Cancel();
            _interimTranslationCts?.Dispose();
            _interimTranslationCts = null;

            _subtitle.InterimTranslation = null;
            _lastInterimTranslationText = string.Empty;
        }

        _isNewSentence = true;
        _lastHypothesisSpeech = string.Empty;

        _subtitle.UpdateDisplay();
        VariableService.SetVariable("speech", text);

        if (IsTranslationEnabled)
        {
            long seq = System.Threading.Interlocked.Increment(ref _finalTranslationSequence);
            CancellationTokenSource cts;
            lock (_translationLock)
            {
                _finalTranslationCts?.Cancel();
                _finalTranslationCts?.Dispose();
                _finalTranslationCts = new CancellationTokenSource();
                cts = _finalTranslationCts;
            }

            _ = Task.Run(async () =>
            {
                var token = cts.Token;
                string targetLang = ParseTargetLanguage(TargetLanguage);
                SpeechStatusUpdated?.Invoke($"正在请求 Ollama AI 翻译 ({OllamaModel})...");
                AddLog("Translate Request", $"正在向 Ollama 请求翻译: \"{text}\" -> {targetLang} ({OllamaModel})", true);

                try
                {
                    var result = await OllamaService.TranslateAsync(text, targetLang, OllamaModel, OllamaEndpoint, OllamaPromptTemplate, token);
                    if (token.IsCancellationRequested || seq < Volatile.Read(ref _finalTranslationSequence)) return;

                    AddLog("Translate Response", $"{result.Text} ({result.LatencyMs}ms)", true);
                    SpeechStatusUpdated?.Invoke($"翻译完成 ({result.LatencyMs}ms)");

                    // 更新队列中该句对应的翻译内容与延迟，精准覆盖
                    string cleanTrans = (result.Text ?? string.Empty).Replace("\r", "").Replace("\n", " ").Trim();
                    _subtitle.ApplyFinalTranslation(text, cleanTrans, result.LatencyMs);

                    // 终态精准覆盖变量
                    VariableService.SetVariable("translation", cleanTrans);
                    _subtitle.UpdateDisplay();

                    // 若开启了翻译结果自动 TTS 语音朗读，触发推流 (读给外国玩家听)
                    if (IsTtsEnabled && IsTtsAutoReadTranslation && !string.IsNullOrWhiteSpace(cleanTrans))
                    {
                        string cleanTts = PrepareTextForTts(cleanTrans);
                        if (!string.IsNullOrWhiteSpace(cleanTts))
                        {
                            _ = SpeakTextAsync(cleanTts);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    AddLog("Translate Canceled", "前序翻译请求已取消", false);
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        string err = $"翻译失败: {ex.Message}";
                        AddLog("Translate Error", err, false, ex.Message);
                        SpeechStatusUpdated?.Invoke(err);
                    }
                }
            });
        }
    }

    /// <summary>
    /// 翻译「我发出去的消息」。结果写入变量 {sent_translation}，
    /// 常驻模板可选用；开启对应开关时还会自动朗读译文。
    /// </summary>
    private async Task TranslateSentMessageAsync(string sentText)
    {
        if (string.IsNullOrWhiteSpace(sentText)) return;

        // 连续快速发送时，只允许最后一次请求的结果落到变量与 TTS，
        // 否则先发消息的慢响应可能覆盖后发消息的译文。
        long seq = System.Threading.Interlocked.Increment(ref _sentTranslationSequence);

        string targetLang = ParseTargetLanguage(TargetLanguage);
        try
        {
            AddLog("Translate Sent", $"正在翻译我发送的消息: \"{sentText}\" -> {targetLang} ({OllamaModel})", true);
            var result = await OllamaService.TranslateAsync(
                sentText, targetLang, OllamaModel, OllamaEndpoint, OllamaPromptTemplate);

            if (seq != Volatile.Read(ref _sentTranslationSequence)) return;

            string cleanTrans = (result.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(cleanTrans))
            {
                // 译文为空时退回朗读原文，避免“两个开关都开了却完全没有播报”
                SpeakSentOriginalFallback(sentText);
                return;
            }

            VariableService.SetVariable("sent_translation", cleanTrans, "我发送消息的翻译",
                "本程序最近一次发送的聊天文本翻译成目标语言后的结果");
            AddLog("Translate Sent Response", $"{cleanTrans} ({result.LatencyMs}ms)", true);

            // 常驻模板接管消息显示时，把译文也并入模板（未显式引用则自动追加瞬态块）
            if (PersistentTextService.IsManagingSentMessages(VariableService))
            {
                await PersistentTextService.NotifySentTranslationAsync(cleanTrans, VariableService, OscService);
            }

            if (IsTtsEnabled && IsTtsAutoReadSentTranslation)
            {
                string cleanTts = PrepareTextForTts(cleanTrans);
                if (!string.IsNullOrWhiteSpace(cleanTts))
                {
                    _ = SpeakTextAsync(cleanTts);
                }
            }
        }
        catch (Exception ex)
        {
            if (seq == Volatile.Read(ref _sentTranslationSequence))
            {
                SpeakSentOriginalFallback(sentText);
            }
            AddLog("Translate Sent Error", $"翻译发送消息失败: {ex.Message}", false, ex.Message);
        }
    }

    /// <summary>
    /// 发送消息的翻译失败/为空时的兜底：若用户同时开启了“朗读已发送消息”，改读原文。
    /// </summary>
    private void SpeakSentOriginalFallback(string sentText)
    {
        if (!IsTtsEnabled || !IsTtsAutoReadSentMessage) return;

        string cleanTts = PrepareTextForTts(sentText);
        if (!string.IsNullOrWhiteSpace(cleanTts))
        {
            _ = SpeakTextAsync(cleanTts);
        }
    }

    /// <summary>
    /// 手动发起一次 Ollama 连通性与翻译效果测试
    /// </summary>
    public async Task<TranslationResult> TestOllamaTranslationAsync(string testText = "你好，欢迎来到 VRChat！")
    {
        string targetLang = ParseTargetLanguage(TargetLanguage);
        AddLog("Translate Test", $"正在发起 Ollama 连通性测试: \"{testText}\" -> {targetLang} ({OllamaModel})", true);
        try
        {
            var result = await OllamaService.TranslateAsync(testText, targetLang, OllamaModel, OllamaEndpoint, OllamaPromptTemplate);
            AddLog("Translate Test Success", $"{result.Text} ({result.LatencyMs}ms)", true);
            return result;
        }
        catch (Exception ex)
        {
            AddLog("Translate Test Failed", $"测试失败: {ex.Message}", false, ex.Message);
            throw;
        }
    }

    /// <summary>兼容转发：显示合成规则见 <see cref="SubtitlePipeline.UpdateDisplay"/>。</summary>
    private void UpdateCombinedSubtitles(string? currentHypothesis = null) =>
        _subtitle.UpdateDisplay(currentHypothesis);

    /// <summary>兼容转发：句末标点定位见 <see cref="SubtitlePipeline.FindLastSentenceCutIndex"/>。</summary>
    private static int FindLastSentenceCutIndex(string text) =>
        SubtitlePipeline.FindLastSentenceCutIndex(text);

    /// <summary>兼容转发：目标语言归一化规则见 <see cref="TranslationPolicy.ParseTargetLanguage"/>。</summary>
    private static string ParseTargetLanguage(string? targetLanguage) =>
        TranslationPolicy.ParseTargetLanguage(targetLanguage);

    public async Task<string> TestTranscribeFileAsync(string? filePath = null)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            filePath = "Models\\me.wav";
        }

        SpeechStatusUpdated?.Invoke($"正在测试识别音频文件: {System.IO.Path.GetFileName(filePath)}...");
        string result = await SpeechService.TranscribeFileAsync(filePath, SpeechLanguageTag);
        return result;
    }

    /// <summary>
    /// 在应用程序完成窗口和事件绑定后，异步初始化需要自启动的服务（如已持久化开启的语音识别等）
    /// </summary>
    public async Task InitializeStartupServicesAsync()
    {
        try
        {
            if (Config.IsSpeechRecognitionEnabled)
            {
                SpeechStatus = string.Equals(SpeechEngine, "LiveCaptions", StringComparison.OrdinalIgnoreCase)
                    ? "正在启动 Windows 11 实时字幕监听..."
                    : "正在初始化 Whisper 模型...";
                SpeechStatusUpdated?.Invoke(SpeechStatus);
                DisplaySettingsChanged?.Invoke();
                await ApplySpeechStateAsync();
            }

            if (Config.IsTtsEnabled && Config.AutoStartTtsServer)
            {
                _ = TtsService.StartManagedServerAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup] Speech recognition auto-start error: {ex.Message}");
        }
    }

    /// <summary>按当前意图收敛两个语音引擎的实际状态（实现在 SpeechEngineCoordinator）。</summary>
    public Task ApplySpeechStateAsync() => SpeechEngines.ApplyAsync();

    public async Task SwitchSpeechLanguageAsync(string languageTag)
    {
        SpeechLanguageTag = languageTag;
        if (IsSpeechRecognitionEnabled)
        {
            await SpeechService.SwitchLanguageAsync(languageTag);
        }
    }
}
