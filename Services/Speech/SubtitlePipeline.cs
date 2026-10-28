using System;
using System.Collections.Generic;
using System.Linq;

namespace VrcChatboxDemo.Services.Speech;

/// <summary>一条已定稿的字幕（原文 + 译文 + 翻译耗时）。</summary>
public record SubtitleEntry(string Original, string Translated, long LatencyMs);

/// <summary>
/// 字幕滚动管线：维护最近 N 句的原文/译文，并把「历史句 + 当前假说」合成为
/// 界面要显示的两行文本。
///
/// 从 SettingsService 拆出来的原因：这里是纯数据与字符串处理（容量裁剪、假说归一化、
/// 中途译文拼接、行数上限），原先与语音引擎启停、Ollama 请求、变量仓库混在一个文件里，
/// 既无法单测也无法安全改动显示规则。
/// </summary>
public sealed class SubtitlePipeline
{
    private readonly object _lock = new();
    private readonly List<SubtitleEntry> _entries = new();
    private readonly Func<int> _capacityProvider;

    private string? _interimTranslation;

    /// <param name="capacityProvider">每次取实时容量（用户在设置里可改）。</param>
    public SubtitlePipeline(Func<int> capacityProvider)
    {
        _capacityProvider = capacityProvider ?? throw new ArgumentNullException(nameof(capacityProvider));
    }

    public string LastRecognizedText { get; private set; } = string.Empty;
    public string LastTranslatedText { get; private set; } = string.Empty;
    public long LastTranslationLatencyMs { get; private set; }

    /// <summary>定稿文本更新（没有假说时）。</summary>
    public event Action<string>? RecognizedUpdated;

    /// <summary>带假说的滚动文本更新。</summary>
    public event Action<string>? HypothesisUpdated;

    /// <summary>译文与延迟更新。</summary>
    public event Action<string, long>? TranslationUpdated;

    /// <summary>显示内容变化（供界面统一刷新）。</summary>
    public event Action? DisplayChanged;

    private int Capacity => Math.Max(1, _capacityProvider());

    /// <summary>中途预翻译结果；null 表示当前没有。</summary>
    public string? InterimTranslation
    {
        get { lock (_lock) return _interimTranslation; }
        set { lock (_lock) _interimTranslation = string.IsNullOrWhiteSpace(value) ? null : value; }
    }

    /// <summary>提交一句定稿文本，并按容量裁剪历史。</summary>
    public void AddFinalSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_lock)
        {
            _entries.Add(new SubtitleEntry(text, string.Empty, 0));
            while (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    /// <summary>把某一句的译文回填到队列里（从最新往回找第一句匹配的原文）。</summary>
    public void ApplyFinalTranslation(string? original, string? translation, long latencyMs)
    {
        if (string.IsNullOrWhiteSpace(original)) return;

        lock (_lock)
        {
            int index = _entries.FindLastIndex(e => e.Original == original);
            if (index >= 0)
            {
                _entries[index] = new SubtitleEntry(original, translation ?? string.Empty, latencyMs);
            }
        }
    }

    /// <summary>清空历史、中途译文与缓存文本（不触发事件，调用方决定通知方式）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _interimTranslation = null;
        }
        LastRecognizedText = string.Empty;
        LastTranslatedText = string.Empty;
        LastTranslationLatencyMs = 0;
    }

    /// <summary>
    /// 合成显示文本并触发事件。
    /// 假说里若混入了已定稿的句子，只保留最后一个未闭合的分句。
    /// </summary>
    public void UpdateDisplay(string? currentHypothesis = null)
    {
        int capacity = Capacity;

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
        string? interimTrans;
        lock (_lock)
        {
            int historyToTake = string.IsNullOrEmpty(cleanHypothesis)
                ? capacity
                : Math.Max(0, capacity - 1);

            snapshot = _entries
                .Skip(Math.Max(0, _entries.Count - historyToTake))
                .ToList();
            interimTrans = _interimTranslation;
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
            if (!string.IsNullOrWhiteSpace(interimTrans))
            {
                translatedLines.Add(interimTrans.Trim() + " ...");
            }
        }

        // 强保障：最终呈现的行数绝对不可超过容量
        while (originalLines.Count > capacity) originalLines.RemoveAt(0);
        while (translatedLines.Count > capacity) translatedLines.RemoveAt(0);

        string fullOriginal = string.Join("\n", originalLines);
        string fullTranslated = string.Join("\n", translatedLines);

        LastRecognizedText = fullOriginal;
        LastTranslatedText = fullTranslated;
        LastTranslationLatencyMs = latestLatency;

        if (string.IsNullOrEmpty(cleanHypothesis))
        {
            RecognizedUpdated?.Invoke(fullOriginal);
        }
        else
        {
            HypothesisUpdated?.Invoke(fullOriginal);
        }

        TranslationUpdated?.Invoke(fullTranslated, latestLatency);
        DisplayChanged?.Invoke();
    }

    /// <summary>
    /// 找出最后一个句末标点的位置（用于把假说里"已经定稿的部分"切掉）。
    /// 小数点（3.14）不算句子结束。
    /// </summary>
    public static int FindLastSentenceCutIndex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;

        int lastCut = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '。' or '！' or '？' or '…' or '.' or '!' or '?')
            {
                if (c == '.' && i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                {
                    continue;
                }
                lastCut = i;
            }
        }
        return lastCut;
    }
}
