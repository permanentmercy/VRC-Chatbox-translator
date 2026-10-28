using System;
using System.Collections.Generic;

namespace VrcChatboxDemo.Services.Translation;

/// <summary>假说预翻译的节流档位。</summary>
public readonly record struct RequestFrequencyOption(
    string Mode,
    string Label,
    int IntervalMs,
    int MinNewChars);

/// <summary>
/// 翻译相关的纯策略：节流档位表与目标语言解析。
///
/// 抽出来的原因：这些是「规则」，不是「状态」——不依赖任何服务、可枚举、可断言。
/// 原先它们埋在 681 行的 SettingsService.Speech.cs 里，UI 还要通过门面反向访问。
/// </summary>
public static class TranslationPolicy
{
    /// <summary>默认档位（0.8 秒 / 3 字）。</summary>
    public const string DefaultMode = AppConfig.DefaultRequestFrequencyMode;

    /// <summary>
    /// 可选档位。<see cref="RequestFrequencyOption.IntervalMs"/> &lt;= 0 表示关闭假说预翻译。
    /// 顺序即下拉框显示顺序。
    /// </summary>
    public static readonly IReadOnlyList<RequestFrequencyOption> FrequencyOptions = new[]
    {
        new RequestFrequencyOption("Off",    "关闭（只翻译定稿句，最省资源）", 0,    int.MaxValue),
        new RequestFrequencyOption("Low",    "低频（2.0 秒 / 次）",            2000, 8),
        new RequestFrequencyOption("Normal", "标准（0.8 秒 / 次，默认）",      800,  3),
        new RequestFrequencyOption("High",   "偏高（0.4 秒 / 次）",            400,  2),
        new RequestFrequencyOption("Ultra",  "高频（0.2 秒 / 次，最跟手）",    200,  1),
    };

    /// <summary>
    /// 把配置里的档位字符串解析成具体参数；无法识别时回退到默认档位。
    /// 回退按名字查找而不是写死下标，避免以后调整档位顺序时踩坑。
    /// </summary>
    public static RequestFrequencyOption ResolveFrequency(string? mode)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            // 配置可能被手工编辑过（前后空格、大小写混用），先规范化再比对，
            // 否则会静默回退到默认档位，用户改的档位看起来"没生效"。
            string normalized = mode.Trim();
            foreach (var option in FrequencyOptions)
            {
                if (string.Equals(option.Mode, normalized, StringComparison.OrdinalIgnoreCase)) return option;
            }
        }

        foreach (var option in FrequencyOptions)
        {
            if (option.Mode == DefaultMode) return option;
        }
        return FrequencyOptions[0];
    }

    /// <summary>
    /// 把 UI 上的目标语言显示名（如「英语 (English)」「日语」）归一化成给模型的提示词。
    /// 识别不到时按英语处理。
    /// </summary>
    public static string ParseTargetLanguage(string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage)) return "English";

        string lower = targetLanguage.ToLowerInvariant();
        if (lower.Contains('日') || lower.Contains("japan")) return "Japanese";
        if (lower.Contains('中') || lower.Contains("chinese")) return "Chinese";
        if (lower.Contains('韩') || lower.Contains("korean")) return "Korean";
        if (lower.Contains('俄') || lower.Contains("russian")) return "Russian";
        if (lower.Contains('法') || lower.Contains("french")) return "French";
        if (lower.Contains('德') || lower.Contains("german")) return "German";
        if (lower.Contains('西') || lower.Contains("spanish")) return "Spanish";
        return "English";
    }
}
