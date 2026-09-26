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
    private bool _isNewSentence = true;
    private string _lastHypothesisSpeech = string.Empty;

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

    private void TryTriggerInterimTranslation(string? interimText)
    {
        if (!IsTranslationEnabled) return;
        if (string.IsNullOrWhiteSpace(interimText)) return;

        string clean = interimText.Replace("\r", "").Replace("\n", " ").Trim();
        if (string.IsNullOrWhiteSpace(clean)) return;

        // 判定是否达到“中途”条件：
        // 1. 包含逗号/分句符（， , 、 ； ;），表明前半句语义已成型；
        // 2. 或者有效字数达到门槛（>= 6 个字符）；
        bool hasComma = clean.IndexOfAny(new[] { '，', ',', '、', '；', ';' }) >= 0;
        bool isHalfway = hasComma || clean.Length >= 6;
        if (!isHalfway) return;

        // 防抖节流检查：距离上次发起预翻译至少 800ms，且内容相较于上次预翻译新增 >= 3 个字（或者产生新标点）
        DateTime now = DateTime.UtcNow;
        lock (_translationLock)
        {
            if (now - _lastInterimTranslationTime < TimeSpan.FromMilliseconds(800))
            {
                return;
            }

            if (!string.IsNullOrEmpty(_lastInterimTranslationText) &&
                clean.Length < _lastInterimTranslationText.Length + 3 &&
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
        DisplaySettingsChanged?.Invoke();

        if (IsSpeechRecognitionEnabled)
        {
            if (string.Equals(SpeechEngine, "LiveCaptions", StringComparison.OrdinalIgnoreCase))
            {
                await SpeechService.StopAsync();
                await LiveCaptionsService.StartAsync(Config.LiveCaptionsHideNativeWindow);
            }
            else
            {
                await LiveCaptionsService.StopAsync();
                await SpeechService.StartAsync(SpeechLanguageTag, AudioInputDeviceId);
            }
        }
        else
        {
            await SpeechService.StopAsync();
            await LiveCaptionsService.StopAsync();
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
