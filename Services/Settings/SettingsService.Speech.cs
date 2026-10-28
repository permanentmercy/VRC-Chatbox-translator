using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public partial class SettingsService
{
    public record SubtitleEntry(string Original, string Translated, long LatencyMs);
    private readonly List<SubtitleEntry> _subtitleEntries = new();
    private CancellationTokenSource? _finalTranslationCts;
    private CancellationTokenSource? _interimTranslationCts;
    private readonly object _translationLock = new();
    private string? _currentInterimTranslation;
    private string _lastInterimTranslationText = string.Empty;
    private DateTime _lastInterimTranslationTime = DateTime.MinValue;
    private long _finalTranslationSequence = 0;
    private long _interimTranslationSequence = 0;
    private long _sentTranslationSequence = 0;
    private bool _isNewSentence = true;
    private string _lastHypothesisSpeech = string.Empty;

    // 语音引擎启停/切换是异步的（Whisper 加载、LiveCaptions 进程重启）。
    // 用户快速切换引擎或连续拨动开关时，若并发执行会出现“旧引擎刚停、新引擎又被
    // 另一条任务停掉”的交错，最终两个引擎都可能处于运行/停止的错误状态。
    private readonly SemaphoreSlim _speechStateLock = new(1, 1);
    private long _speechStateVersion;
    private int _speechStateApplying;

    private void OnSpeechRunningStateChanged(string engine, bool running)
    {
        // Apply 内的停旧引擎/启动结果是过渡态，由 Apply 根据最新用户意图统一收敛。
        if (Volatile.Read(ref _speechStateApplying) != 0 ||
            !string.Equals(SpeechEngine, engine, StringComparison.OrdinalIgnoreCase)) return;

        // 非切换期间的意外停止仍应关闭开关，不能把后台故障伪装成正在监听。
        // true 只代表 actual 成功，绝不覆盖用户已经写下的 false。
        if (!running) IsSpeechRecognitionEnabled = false;
        NotifySpeechRecognitionState();
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

            _currentInterimTranslation = null;
            _lastInterimTranslationText = string.Empty;
            _lastInterimTranslationTime = DateTime.MinValue;
        }
        lock (_subtitleEntries)
        {
            _subtitleEntries.Clear();
        }
        _isNewSentence = true;
        _lastHypothesisSpeech = string.Empty;
        LiveCaptionsService.ResetHistory();
        LastRecognizedText = string.Empty;
        LastTranslatedText = string.Empty;
        LastTranslationLatencyMs = 0;
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

        UpdateCombinedSubtitles(interimText);
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

    public readonly record struct RequestFrequencyOption(
        string Mode, string Label, int IntervalMs, int MinNewChars);

    /// <summary>默认档位。0.8 秒 / 3 字，与加入本设置之前写死的节流参数完全一致。</summary>
    public const string DefaultRequestFrequencyMode = AppConfig.DefaultRequestFrequencyMode;

    /// <summary>
    /// 可选档位。IntervalMs &lt;= 0 表示关闭假说预翻译。
    /// 顺序即下拉框的显示顺序。
    /// </summary>
    public static readonly IReadOnlyList<RequestFrequencyOption> RequestFrequencyOptions = new[]
    {
        new RequestFrequencyOption("Off",    "关闭（只翻译定稿句，最省资源）", 0,    int.MaxValue),
        new RequestFrequencyOption("Low",    "低频（2.0 秒 / 次）",            2000, 8),
        new RequestFrequencyOption("Normal", "标准（0.8 秒 / 次，默认）",      800,  3),
        new RequestFrequencyOption("High",   "偏高（0.4 秒 / 次）",            400,  2),
        new RequestFrequencyOption("Ultra",  "高频（0.2 秒 / 次，最跟手）",    200,  1),
    };

    /// <summary>
    /// 把配置里的档位字符串解析成具体参数；无法识别时回退到「标准」。
    /// </summary>
    public static RequestFrequencyOption ResolveRequestFrequency(string? mode)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            foreach (var opt in RequestFrequencyOptions)
            {
                if (string.Equals(opt.Mode, mode, StringComparison.OrdinalIgnoreCase))
                {
                    return opt;
                }
            }
        }
        // 按名字回退到「标准」，而不是写死下标，免得以后调整档位顺序时踩坑
        foreach (var opt in RequestFrequencyOptions)
        {
            if (opt.Mode == DefaultRequestFrequencyMode) return opt;
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
                    _currentInterimTranslation = cleanTrans;
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

        // 提交当前识别出的完整句子到字幕队列
        var entry = new SubtitleEntry(text, string.Empty, 0);
        lock (_subtitleEntries)
        {
            _subtitleEntries.Add(entry);
            while (_subtitleEntries.Count > Math.Max(1, SubtitleQueueCapacity))
            {
                _subtitleEntries.RemoveAt(0);
            }
        }

        lock (_translationLock)
        {
            // 完整句子已到，取消假说预翻译
            _interimTranslationCts?.Cancel();
            _interimTranslationCts?.Dispose();
            _interimTranslationCts = null;

            _currentInterimTranslation = null;
            _lastInterimTranslationText = string.Empty;
        }

        _isNewSentence = true;
        _lastHypothesisSpeech = string.Empty;

        UpdateCombinedSubtitles();
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
                    lock (_subtitleEntries)
                    {
                        int idx = _subtitleEntries.FindLastIndex(e => e.Original == text);
                        if (idx >= 0)
                        {
                            _subtitleEntries[idx] = new SubtitleEntry(text, cleanTrans, result.LatencyMs);
                        }
                    }

                    // 终态精准覆盖变量
                    VariableService.SetVariable("translation", cleanTrans);
                    UpdateCombinedSubtitles();

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

    private void UpdateCombinedSubtitles(string? currentHypothesis = null)
    {
        int capacity = Math.Max(1, SubtitleQueueCapacity);

        // 对临时假说进行严格规范化：
        // 1. 过滤内部换行符
        // 2. 如果假说中混入了包含句子终结符的已完成句子，仅保留最后一个未闭合的分句
        string cleanHypothesis = string.Empty;
        if (!string.IsNullOrWhiteSpace(currentHypothesis))
        {
            string h = currentHypothesis.Replace("\r", "").Replace("\n", " ").Trim();
            int cutIdx = FindLastSentenceCutIndex(h);
            if (cutIdx >= 0 && cutIdx < h.Length - 1)
            {
                h = h.Substring(cutIdx + 1).TrimStart(' ', '\t', '，', ',', '、', '；', ';', '。', '！', '？', '…', '.', '!', '?');
            }
            cleanHypothesis = h;
        }

        List<SubtitleEntry> snapshot;
        lock (_subtitleEntries)
        {
            int historyToTake = string.IsNullOrEmpty(cleanHypothesis)
                ? capacity
                : Math.Max(0, capacity - 1);

            snapshot = _subtitleEntries
                .Skip(Math.Max(0, _subtitleEntries.Count - historyToTake))
                .ToList();
        }

        var originalLines = new List<string>();
        var translatedLines = new List<string>();
        long latestLatency = 0;

        foreach (var item in snapshot)
        {
            string orig = (item.Original ?? string.Empty).Replace("\r", "").Replace("\n", " ").Trim();
            string trans = (item.Translated ?? string.Empty).Replace("\r", "").Replace("\n", " ").Trim();
            if (!string.IsNullOrWhiteSpace(orig)) originalLines.Add(orig);
            if (!string.IsNullOrWhiteSpace(trans)) translatedLines.Add(trans);
            if (item.LatencyMs > 0) latestLatency = item.LatencyMs;
        }

        string? interimTrans = null;
        lock (_translationLock)
        {
            interimTrans = _currentInterimTranslation;
        }

        if (!string.IsNullOrWhiteSpace(cleanHypothesis))
        {
            originalLines.Add(cleanHypothesis.Trim() + " ...");
            // 若当前存在中途预翻译结果，同步呈现在翻译行中
            if (!string.IsNullOrWhiteSpace(interimTrans))
            {
                translatedLines.Add(interimTrans.Trim() + " ...");
            }
        }

        // 强保障：无论如何，最终呈现的行数绝对不可超过 capacity 限制
        while (originalLines.Count > capacity)
        {
            originalLines.RemoveAt(0);
        }
        while (translatedLines.Count > capacity)
        {
            translatedLines.RemoveAt(0);
        }

        string fullOriginal = string.Join("\n", originalLines);
        string fullTranslated = string.Join("\n", translatedLines);

        LastRecognizedText = fullOriginal;
        LastTranslatedText = fullTranslated;
        LastTranslationLatencyMs = latestLatency;

        if (string.IsNullOrEmpty(cleanHypothesis))
        {
            SpeechRecognizedUpdated?.Invoke(fullOriginal);
        }
        else
        {
            SpeechHypothesisUpdated?.Invoke(fullOriginal);
        }

        TranslationUpdated?.Invoke(fullTranslated, latestLatency);
        DisplaySettingsChanged?.Invoke();
    }

    private static int FindLastSentenceCutIndex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;

        int lastCut = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '。' || c == '！' || c == '？' || c == '…' || c == '.' || c == '!' || c == '?')
            {
                // 小数点防护（如 3.14）
                if (c == '.' && i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                {
                    continue;
                }
                lastCut = i;
            }
        }

        return lastCut;
    }

    private static string ParseTargetLanguage(string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage)) return "English";
        string lower = targetLanguage.ToLowerInvariant();
        if (lower.Contains("日") || lower.Contains("japan")) return "Japanese";
        if (lower.Contains("中") || lower.Contains("chinese")) return "Chinese";
        if (lower.Contains("韩") || lower.Contains("korean")) return "Korean";
        if (lower.Contains("俄") || lower.Contains("russian")) return "Russian";
        if (lower.Contains("法") || lower.Contains("french")) return "French";
        if (lower.Contains("德") || lower.Contains("german")) return "German";
        if (lower.Contains("西") || lower.Contains("spanish")) return "Spanish";
        return "English";
    }

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

    public async Task ApplySpeechStateAsync()
    {
        await _speechStateLock.WaitAsync();
        Interlocked.Exchange(ref _speechStateApplying, 1);
        long version = Volatile.Read(ref _speechStateVersion);
        string engine = SpeechEngine;
        try
        {
            DisplaySettingsChanged?.Invoke();

            // await 期间用户仍可修改开关/引擎。旧启动完成后先重新读取意图，
            // 必要时在释放锁之前停止它，而不是用 actual 事件把关闭改回开启。
            while (true)
            {
                version = Volatile.Read(ref _speechStateVersion);
                engine = SpeechEngine;
                if (!IsSpeechRecognitionEnabled)
                {
                    await SpeechService.StopAsync();
                    await LiveCaptionsService.StopAsync();
                }
                else
                {
                    bool isLiveCaptions = string.Equals(engine, "LiveCaptions", StringComparison.OrdinalIgnoreCase);
                    if (isLiveCaptions) await SpeechService.StopAsync();
                    else await LiveCaptionsService.StopAsync();

                    if (version != Volatile.Read(ref _speechStateVersion)) continue;

                    bool started = isLiveCaptions
                        ? await LiveCaptionsService.StartAsync(Config.LiveCaptionsHideNativeWindow)
                        : await SpeechService.StartAsync(SpeechLanguageTag, AudioInputDeviceId);

                    if (version != Volatile.Read(ref _speechStateVersion)) continue;

                    bool running = isLiveCaptions ? LiveCaptionsService.IsRunning : SpeechService.IsListening;
                    if (!started || !running) IsSpeechRecognitionEnabled = false;
                }

                if (version == Volatile.Read(ref _speechStateVersion)) break;
            }
        }
        catch (Exception ex)
        {
            // 仅回滚同一请求；迟到的失败也不能覆盖更新的开启/引擎选择。
            if (version == Volatile.Read(ref _speechStateVersion) &&
                string.Equals(engine, SpeechEngine, StringComparison.OrdinalIgnoreCase))
            {
                IsSpeechRecognitionEnabled = false;
            }
            SpeechStatus = $"应用语音状态失败: {ex.Message}";
            SpeechStatusUpdated?.Invoke(SpeechStatus);
        }
        finally
        {
            Interlocked.Exchange(ref _speechStateApplying, 0);
            _speechStateLock.Release();
            NotifySpeechRecognitionState();
        }
    }

    public async Task SwitchSpeechLanguageAsync(string languageTag)
    {
        SpeechLanguageTag = languageTag;
        if (IsSpeechRecognitionEnabled)
        {
            await SpeechService.SwitchLanguageAsync(languageTag);
        }
    }
}
