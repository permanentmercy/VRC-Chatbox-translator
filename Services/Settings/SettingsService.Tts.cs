using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VrcChatboxDemo.Services.Tts;

namespace VrcChatboxDemo.Services;

/// <summary>
/// TTS 朗读编排（分部类，机械拆分）：流式推流、本地试听、语义断句。
///
/// 从 SettingsService.cs 拆出来的原因：这段是「把一段文本变成朗读」的完整策略
/// （1.5 秒防抖、分句、状态事件、失败日志），与配置属性、语音引擎、退出流程无关。
/// 字段仍在主文件（partial 共享），本次不改变任何行为。
/// </summary>
public partial class SettingsService
{
    private string _lastTtsSpokenText = string.Empty;
    private DateTime _lastTtsSpokenTime = DateTime.MinValue;
    private readonly object _ttsDebounceLock = new();

    /// <summary>
    /// 流式推流管线：直接向 IndexTTS 发起 /tts/stream 流式输出请求，首块到达立即唤醒声卡推流，后续音频流在后台边生成边播放
    /// </summary>
    public async Task<bool> SpeakTextStreamAsync(string text, bool force = false)
    {
        if (!force && !IsTtsEnabled)
        {
            return false;
        }

        string cleanText = PrepareTextForTts(text);
        if (string.IsNullOrWhiteSpace(cleanText)) return false;

        // 短时间内相同文本防抖去重：避免连续回车或事件重入导致同一段文本被反复发送挤爆显存
        if (!force)
        {
            lock (_ttsDebounceLock)
            {
                if (string.Equals(cleanText, _lastTtsSpokenText, StringComparison.Ordinal) &&
                    (DateTime.UtcNow - _lastTtsSpokenTime).TotalMilliseconds < 1500)
                {
                    AddLog("TTS Debounce", $"忽略 1.5 秒内对相同文本的重复推流请求: \"{cleanText}\"", true);
                    return false;
                }
                _lastTtsSpokenText = cleanText;
                _lastTtsSpokenTime = DateTime.UtcNow;
            }
        }
        else
        {
            lock (_ttsDebounceLock)
            {
                _lastTtsSpokenText = cleanText;
                _lastTtsSpokenTime = DateTime.UtcNow;
            }
        }

        TtsActiveStateChanged?.Invoke(true);
        TtsProgressChanged?.Invoke(-1);
        TtsStatusUpdated?.Invoke("正在流式合成语音...");

        try
        {
            bool ok = await TtsService.PlayStreamAsync(
                cleanText,
                TtsVirtualMicDeviceId,
                TtsMonitorDeviceId,
                IsTtsMonitorEnabled,
                TtsOutputVolume,
                TtsMonitorVolume,
                onChunkReceived: (chunkIdx) =>
                {
                    TtsStatusUpdated?.Invoke($"正在流式推流播放 (第 {chunkIdx} 块)...");
                }
            );

            if (ok)
            {
                AddLog("TTS Speech", $"流式语音推流完成: \"{cleanText}\"", true);
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
            TtsStatusUpdated?.Invoke("就绪");
        }
    }

    /// <summary>
    /// 仅在本地耳机流式试听
    /// </summary>
    public async Task<bool> SpeakLocalPreviewStreamAsync(string text)
    {
        string cleanText = PrepareTextForTts(text);
        if (string.IsNullOrWhiteSpace(cleanText)) return false;

        lock (_ttsDebounceLock)
        {
            if (string.Equals(cleanText, _lastTtsSpokenText, StringComparison.Ordinal) &&
                (DateTime.UtcNow - _lastTtsSpokenTime).TotalMilliseconds < 1500)
            {
                AddLog("TTS Debounce", $"忽略 1.5 秒内对相同试听文本的重复请求: \"{cleanText}\"", true);
                return false;
            }
            _lastTtsSpokenText = cleanText;
            _lastTtsSpokenTime = DateTime.UtcNow;
        }

        TtsActiveStateChanged?.Invoke(true);
        TtsProgressChanged?.Invoke(-1);
        TtsStatusUpdated?.Invoke("正在合成试听语音...");

        try
        {
            return await TtsService.PlayStreamAsync(
                cleanText,
                virtualMicDeviceId: null,
                monitorDeviceId: TtsMonitorDeviceId,
                enableMonitor: true,
                micVolume: 0f,
                monitorVolume: TtsMonitorVolume,
                onChunkReceived: (chunkIdx) =>
                {
                    TtsStatusUpdated?.Invoke($"正在流式试听播放 (第 {chunkIdx} 块)...");
                }
            );
        }
        finally
        {
            TtsProgressChanged?.Invoke(100);
            TtsActiveStateChanged?.Invoke(false);
            TtsStatusUpdated?.Invoke("就绪");
        }
    }

    /// <summary>
    /// 智能语义断句算法 v2：将整段长文本切分为自然语音子句
    /// 核心原则：
    /// 1. 仅在句子级别边界（。！？.!?；;\n）断句，逗号由 TTS 模型自然处理停顿
    /// 2. 中文内嵌英文单词不拆分（如"这个program很好"不会在 program 处断开）
    /// 3. 超长句（>35字符）在逗号/连接词处二次细分
    /// 4. 过短片段（≤3字符）粘合到相邻句子，避免极短 TTS 请求产生不自然停顿
    /// </summary>
    public static List<string> SplitIntoSpeechClauses(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        // ── 第 1 步：仅在句子终止符（非逗号）处切分 ──
        // 句子终止符：。！？.!?；;\n\r…
        // 逗号（，,）和顿号（、）不作为一级切分点——它们在 TTS 模型中自然产生停顿
        var rawSentences = new List<string>();
        char[] sentenceEnders = { '。', '！', '？', '；', '.', '!', '?', ';', '\n', '\r' };
        int start = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (Array.IndexOf(sentenceEnders, c) >= 0)
            {
                // 特殊处理英文句号：如果紧跟字母或数字（如 "v1.5" "Dr."），不在此断句
                if (c == '.')
                {
                    bool leftIsLatinOrDigit = i > 0 && (char.IsLetterOrDigit(input[i - 1]));
                    bool rightIsLatinOrDigit = i + 1 < input.Length && (char.IsLetterOrDigit(input[i + 1]));
                    if (leftIsLatinOrDigit && rightIsLatinOrDigit)
                    {
                        continue; // "v1.5" 中的点号，跳过不切
                    }
                }

                string seg = input.Substring(start, i - start + 1).Trim();
                if (!string.IsNullOrWhiteSpace(seg))
                {
                    rawSentences.Add(seg);
                }
                start = i + 1;
            }
        }
        if (start < input.Length)
        {
            string tail = input.Substring(start).Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                rawSentences.Add(tail);
            }
        }

        if (rawSentences.Count == 0)
        {
            rawSentences.Add(input.Trim());
        }

        // ── 第 2 步：对超长句子（>35字符）二次细分 ──
        // 此时才使用逗号、顿号和中文连接词作为切分点
        var subDivided = new List<string>();
        char[] commaDelims = { '，', ',', '、' };
        string[] connectors = { "然后", "但是", "而且", "所以", "并且", "因为", "如果", "虽然", "不过", "以及", "或者", "就是", "还有", "另外", "同时", "接着", "之后" };

        foreach (var sentence in rawSentences)
        {
            if (sentence.Length <= 35)
            {
                subDivided.Add(sentence);
                continue;
            }

            // 在逗号和连接词处细分超长句
            var parts = new List<string>();
            int segStart = 0;
            for (int i = 0; i < sentence.Length; i++)
            {
                bool shouldSplit = false;
                int splitLen = 1;

                // 检查逗号/顿号
                if (Array.IndexOf(commaDelims, sentence[i]) >= 0)
                {
                    // 只有当前段足够长（>8字符）才在逗号处切
                    if (i - segStart >= 8)
                    {
                        shouldSplit = true;
                    }
                }

                // 检查中文连接词
                if (!shouldSplit && i - segStart >= 8)
                {
                    foreach (var conn in connectors)
                    {
                        if (i + conn.Length <= sentence.Length &&
                            sentence.AsSpan(i, conn.Length).SequenceEqual(conn.AsSpan()))
                        {
                            shouldSplit = true;
                            splitLen = 0; // 连接词留在下一段开头
                            break;
                        }
                    }
                }

                if (shouldSplit)
                {
                    string part = sentence.Substring(segStart, i - segStart + splitLen).Trim();
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        parts.Add(part);
                    }
                    segStart = i + splitLen;
                }
            }
            if (segStart < sentence.Length)
            {
                string lastPart = sentence.Substring(segStart).Trim();
                if (!string.IsNullOrWhiteSpace(lastPart))
                {
                    parts.Add(lastPart);
                }
            }

            subDivided.AddRange(parts.Count > 0 ? parts : new List<string> { sentence });
        }

        // ── 第 3 步：粘合过短片段（纯文字长度 ≤ 3，如单个"好""嗯""OK"等） ──
        // 避免极短的 TTS 请求产生不自然的长停顿
        var merged = new List<string>();
        foreach (var item in subDivided)
        {
            string plain = item.Trim('。', '.', '！', '!', '？', '?', '，', ',', '、', '；', ';', '…', '~', ' ');
            if (merged.Count > 0 && plain.Length <= 3)
            {
                // 粘合到上一个分句
                string prev = merged[^1];
                // 移除上一句的末尾句号，用逗号连接
                prev = prev.TrimEnd('。', '.', '！', '!', '？', '?');
                merged[^1] = prev + "，" + item;
            }
            else
            {
                merged.Add(item);
            }
        }

        // ── 第 4 步：规范化标点与声学清洗 ──
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
}
