using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public partial class SettingsService
{
    public record SubtitleEntry(string Original, string Translated, long LatencyMs);
    private readonly List<SubtitleEntry> _subtitleEntries = new();

    public void ClearSubtitleQueue()
    {
        lock (_subtitleEntries)
        {
            _subtitleEntries.Clear();
        }
        LiveCaptionsService.ResetHistory();
        LastRecognizedText = string.Empty;
        LastTranslatedText = string.Empty;
        LastTranslationLatencyMs = 0;
        SpeechRecognizedUpdated?.Invoke(string.Empty);
        TranslationUpdated?.Invoke(string.Empty, 0);
    }

    private void OnSpeechHypothesis(string interimText)
    {
        UpdateCombinedSubtitles(interimText);
    }

    private async void OnSpeechRecognized(string text)
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
        UpdateCombinedSubtitles();
        VariableService.SetVariable("speech", text);

        if (IsTranslationEnabled)
        {
            SpeechStatusUpdated?.Invoke("正在请求 Ollama AI 翻译...");
            try
            {
                string targetLang = ParseTargetLanguage(TargetLanguage);
                var result = await OllamaService.TranslateAsync(text, targetLang, OllamaModel, OllamaEndpoint, OllamaPromptTemplate);
                AddLog("Translate", $"{result.Text} ({result.LatencyMs}ms)", true);
                SpeechStatusUpdated?.Invoke($"翻译完成 ({result.LatencyMs}ms)");

                // 更新队列中该句对应的翻译内容与延迟
                string cleanTrans = (result.Text ?? string.Empty).Replace("\r", "").Replace("\n", " ").Trim();
                lock (_subtitleEntries)
                {
                    int idx = _subtitleEntries.FindLastIndex(e => e.Original == text);
                    if (idx >= 0)
                    {
                        _subtitleEntries[idx] = new SubtitleEntry(text, cleanTrans, result.LatencyMs);
                    }
                }
                UpdateCombinedSubtitles();
                VariableService.SetVariable("translation", cleanTrans);
            }
            catch (Exception ex)
            {
                string err = $"翻译失败: {ex.Message}";
                AddLog("Translate", err, false);
                SpeechStatusUpdated?.Invoke(err);
            }
        }
    }

    private void UpdateCombinedSubtitles(string? currentHypothesis = null)
    {
        int capacity = Math.Max(1, SubtitleQueueCapacity);

        // 对临时假说进行严格规范化：
        // 1. 过滤内部换行符
        // 2. 如果假说中混入了包含句子终结符或达到2个逗号的已完成句子，仅保留最后一个未闭合的分句
        string cleanHypothesis = string.Empty;
        if (!string.IsNullOrWhiteSpace(currentHypothesis))
        {
            string h = currentHypothesis.Replace("\r", "").Replace("\n", " ").Trim();
            int cutIdx = FindLastSentenceCutIndex(h);
            if (cutIdx >= 0 && cutIdx < h.Length - 1)
            {
                h = h.Substring(cutIdx + 1).TrimStart(' ', '\t', '，', ',', '、', '；', ';', '。', '！', '？', '…', '.', '!', '?');
            }
            else if (cutIdx == h.Length - 1)
            {
                h = string.Empty;
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

        if (!string.IsNullOrWhiteSpace(cleanHypothesis))
        {
            originalLines.Add(cleanHypothesis.Trim() + " ...");
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
        int commaCount = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '。' || c == '！' || c == '？' || c == '…' || c == '.' || c == '!' || c == '?')
            {
                lastCut = i;
                commaCount = 0;
            }
            else if (c == '，' || c == ',' || c == '、' || c == '；' || c == ';')
            {
                commaCount++;
                if (commaCount >= 2)
                {
                    lastCut = i;
                    commaCount = 0;
                }
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
