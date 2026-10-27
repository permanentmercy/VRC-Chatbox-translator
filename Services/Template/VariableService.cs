using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VrcChatboxDemo.Services;

public class VariableItem
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsBuiltin { get; set; } = false;
    public string Description { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 统一变量管理中心与文本模板渲染引擎
/// 管理固定内置变量（语音识别、AI翻译、时间等）及外部程序通过 HTTP API 注册的动态变量
/// </summary>
public class VariableService
{
    private readonly ConcurrentDictionary<string, VariableItem> _variables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 匹配 {变量名} 的正则表达式
    /// </summary>
    private static readonly Regex VariableRegex = new(@"\{([a-zA-Z0-9_\-\u4e00-\u9fa5]+)\}", RegexOptions.Compiled);

    // Rendering, reference detection and transient lifetimes must resolve aliases identically.
    internal static string GetCanonicalVariableName(string name)
    {
        name = name.Trim().Trim('{', '}').Trim();
        return name.ToLowerInvariant() switch
        {
            "lang" or "caption_lang" or "subtitle_lang" or "字幕语言" => "language",
            "lang_code" or "caption_lang_code" or "字幕语言代码" => "language_code",
            @"\n" or "newline" or "换行" => @"\n",
            _ => name
        };
    }

    /// <summary>
    /// 当某个变量的值发生变更时触发 (变量名, 新值)
    /// </summary>
    public event Action<string, string>? VariableUpdated;

    /// <summary>
    /// 当变量列表新增或删除变量时触发（用于通知 UI 下拉列表刷新）
    /// </summary>
    public event Action? VariablesListChanged;

    public VariableService()
    {
        InitializeBuiltinVariables();
    }

    private void InitializeBuiltinVariables()
    {
        RegisterBuiltin("speech", "语音识别文本", "从麦克风或系统扬声器读取的最新语音识别文本");
        RegisterBuiltin("translation", "AI 翻译结果", "本地 Ollama AI 翻译返回的最新目标语言文本");
        RegisterBuiltin("sent_text", "我发送的消息原文", "本程序最近一次发送到 VRChat 的聊天文本（原文）");
        RegisterBuiltin("sent_translation", "我发送消息的翻译", "本程序最近一次发送的聊天文本翻译成目标语言后的结果（需开启“发送消息时自动翻译”）");
        RegisterBuiltin("language", "当前字幕语言", "当前语音识别/字幕引擎生效的语言名称 (如 中文, 日本語, English)");
        RegisterBuiltin("language_code", "当前字幕语言代码", "当前字幕引擎生效的语言代码 (如 zh-CN, ja-JP, en-US)");
        RegisterBuiltin("time", "当前时间", "当前系统时钟 (HH:mm)");
        RegisterBuiltin(@"\n", "换行符", "插入显式换行 (仅使用 {\\n} 时才在游戏内换行)");
    }

    private void RegisterBuiltin(string name, string displayName, string description)
    {
        _variables[name] = new VariableItem
        {
            Name = name,
            DisplayName = displayName,
            Value = string.Empty,
            IsBuiltin = true,
            Description = description,
            LastUpdated = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 注册或更新变量的值。若变量不存在则自动注册。
    /// </summary>
    public VariableItem SetVariable(string name, string value, string? displayName = null, string? description = null)
    {
        name = name?.Trim().Trim('{', '}').Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Variable name cannot be empty.", nameof(name));
        }

        value ??= string.Empty;
        bool isNew = !_variables.ContainsKey(name);

        var item = _variables.AddOrUpdate(name,
            key => new VariableItem
            {
                Name = key,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
                Value = value,
                IsBuiltin = false,
                Description = description ?? "外部程序注册变量",
                LastUpdated = DateTime.UtcNow
            },
            (key, existing) =>
            {
                existing.Value = value;
                existing.LastUpdated = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(displayName) && !existing.IsBuiltin)
                {
                    existing.DisplayName = displayName;
                }
                if (!string.IsNullOrWhiteSpace(description) && !existing.IsBuiltin)
                {
                    existing.Description = description;
                }
                return existing;
            });

        if (isNew)
        {
            VariablesListChanged?.Invoke();
        }

        VariableUpdated?.Invoke(name, value);
        return item;
    }

    /// <summary>
    /// 获取指定变量的值
    /// </summary>
    public string GetVariableValue(string name)
    {
        name = name?.Trim().Trim('{', '}').Trim() ?? string.Empty;
        if (string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
        {
            return DateTime.Now.ToString("HH:mm");
        }

        if (string.Equals(name, @"\n", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "newline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "换行", StringComparison.OrdinalIgnoreCase))
        {
            return "\n";
        }

        name = GetCanonicalVariableName(name);

        if (_variables.TryGetValue(name, out var item))
        {
            return item.Value ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// 检查指定模板是否引用了某个变量
    /// </summary>
    public bool UsesVariable(string template, string name)
    {
        if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(name)) return false;
        name = GetCanonicalVariableName(name);
        // The explicit backslash-newline token is outside VariableRegex's name alphabet.
        if (string.Equals(name, @"\n", StringComparison.OrdinalIgnoreCase) &&
            template.IndexOf(@"{\n}", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        foreach (Match match in VariableRegex.Matches(template))
        {
            if (string.Equals(GetCanonicalVariableName(match.Groups[1].Value), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 获取所有变量列表（内置置顶，其余按更新时间排序）
    /// </summary>
    public IReadOnlyList<VariableItem> GetAllVariables()
    {
        return _variables.Values
            .OrderByDescending(v => v.IsBuiltin)
            .ThenBy(v => v.Name)
            .ToList();
    }

    /// <summary>
    /// 使用当前变量池中的最新值渲染模板字符串，并进行 144 字符安全截断（VRChat 限制）。
    /// 注意：仅在显式书写 {\n}、{newline} 或 {换行} 时才在输出中插入真实换行；
    /// 模板中的常规回车物理换行将被视为空格清洗，不会直接发送到游戏内。
    /// </summary>
    public string Render(string template)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        // 1. 将显式换行标识（{\n}, \n, {\N}, {newline}, {换行}）转换为受控安全占位符
        string processed = Regex.Replace(template, @"\{(\\n|newline|换行)\}|\\n", "\uE000", RegexOptions.IgnoreCase);

        // 2. 将模板内所有的普通物理换行（回车）转为空格，避免直接发送物理换行到游戏内
        processed = processed.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        // 3. 正常解析并替换所有变量
        string rendered = VariableRegex.Replace(processed, match =>
        {
            string varName = match.Groups[1].Value;
            if (string.Equals(varName, "time", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.Now.ToString("HH:mm");
            }

            string lookupName = GetCanonicalVariableName(varName);

            if (_variables.TryGetValue(lookupName, out var item))
            {
                return item.Value ?? string.Empty;
            }

            // 未知变量保持原样或保留
            return match.Value;
        });

        // 4. 将变量替换后可能混入的物理换行也统一消除（只有显式占位符才允许换行）
        rendered = rendered.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        // 5. 将受控占位符还原为真实换行符
        rendered = rendered.Replace("\uE000", "\n");

        // 6. 规整换行符前后的空白字符与多余空格
        rendered = Regex.Replace(rendered, @"[ \t]*\n[ \t]*", "\n");
        rendered = Regex.Replace(rendered, @"[ ]{2,}", " ");

        // 7. VRChat 单条气泡最多 144 个字符限制。
        // 直接 Substring 可能把 emoji 的代理对切成半个字符，渲染出乱码，
        // 这里在需要时回退一个字符保证不切断代理对。
        const int maxChatboxChars = 144;
        if (rendered.Length > maxChatboxChars)
        {
            int cut = maxChatboxChars;
            if (char.IsHighSurrogate(rendered[cut - 1]))
            {
                cut--;
            }
            rendered = rendered.Substring(0, cut);
        }

        return rendered.Trim();
    }
}
