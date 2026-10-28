using System;
using System.Text.RegularExpressions;

namespace VrcChatboxDemo.Services.Text;

/// <summary>
/// 聊天/TTS 文本的纯函数规范化：换行展开、144 字符截断、朗读前清洗。
///
/// 这些规则原先散落在 SettingsService 的三个方法里，其中「显式换行标记」的正则
/// 被复制了三次、截断逻辑被复制了两次。集中到一处后：
/// · 修改 VRChat 字符上限只需改一个常量；
/// · 全是纯函数，测试不需要任何服务或 UI。
/// </summary>
public static class ChatTextNormalizer
{
    /// <summary>VRChat chatbox 单条上限。</summary>
    public const int MaxChatboxChars = 144;

    private static readonly Regex ExplicitNewlinePattern =
        new(@"\{(\\n|newline|换行)\}|\\n", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VrChatRichTextPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ControlCharsPattern = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);
    private static readonly Regex HorizontalWhitespacePattern = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex AnyWhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 把用户写的显式换行标记（{¥n}、{newline}、{换行}、字面 \n）换成真实换行符。
    /// 注意：模板渲染链用的是占位符版本（见 PersistentTextService），这里用于「直接发送」路径。
    /// </summary>
    public static string ExpandExplicitNewlines(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : ExplicitNewlinePattern.Replace(text, "\n");

    /// <summary>
    /// 截断到 VRChat 单条上限。直接 Substring 会把 emoji 的代理对切成半个字符，
    /// 显示成乱码，因此必要时回退一个 char。
    /// </summary>
    public static string TruncateForChatbox(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxChatboxChars) return text ?? string.Empty;

        int cut = MaxChatboxChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text.Substring(0, cut);
    }

    /// <summary>直接发送前的完整处理：展开换行 + 截断。</summary>
    public static string PrepareForChatbox(string? text) => TruncateForChatbox(ExpandExplicitNewlines(text));

    /// <summary>
    /// 朗读前的声学清洗：
    /// 1. 去掉 VRChat 富文本标签（&lt;color=...&gt; 等）；
    /// 2. 换行转逗号，形成自然停顿而不是断音；
    /// 3. 去掉控制字符、压缩空白；
    /// 4. 末尾缺少终止标点时补齐（自回归 TTS 缺结束标记会在尾部产生电音/拖长）。
    /// </summary>
    public static string PrepareForTts(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string text = VrChatRichTextPattern.Replace(input, string.Empty);
        text = text.Replace("\r\n", "，").Replace("\r", "，").Replace("\n", "，");
        text = ControlCharsPattern.Replace(text, string.Empty);
        text = AnyWhitespacePattern.Replace(text, " ").Trim();

        if (string.IsNullOrEmpty(text)) return string.Empty;

        char last = text[^1];
        if (last is '。' or '！' or '？' or '.' or '!' or '?' or '…' or '~') return text;

        bool asciiWordEnd = last is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
        return text + (asciiWordEnd ? "." : "。");
    }

    /// <summary>压缩连续空格（发送前预览用）。</summary>
    public static string CollapseSpaces(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : HorizontalWhitespacePattern.Replace(text, " ");
}
