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
    private static readonly Regex VariableRegex = new(@"\{([a-zA-Z0-9_\u4e00-\u9fa5]+)\}", RegexOptions.Compiled);

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
        RegisterBuiltin("language", "当前字幕语言", "当前语音识别/字幕引擎生效的语言名称 (如 中文, 日本語, English)");
        RegisterBuiltin("language_code", "当前字幕语言代码", "当前字幕引擎生效的语言代码 (如 zh-CN, ja-JP, en-US)");
        RegisterBuiltin("time", "当前时间", "当前系统时钟 (HH:mm)");
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

        if (string.Equals(name, "lang", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "caption_lang", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "subtitle_lang", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "字幕语言", StringComparison.OrdinalIgnoreCase))
        {
            name = "language";
        }
        else if (string.Equals(name, "lang_code", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "caption_lang_code", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "字幕语言代码", StringComparison.OrdinalIgnoreCase))
        {
            name = "language_code";
        }

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
        name = name.Trim().Trim('{', '}').Trim();
        if (template.IndexOf($"{{{name}}}", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        if (string.Equals(name, "language", StringComparison.OrdinalIgnoreCase))
        {
            return template.IndexOf("{lang}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   template.IndexOf("{caption_lang}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   template.IndexOf("{subtitle_lang}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   template.IndexOf("{字幕语言}", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        if (string.Equals(name, "language_code", StringComparison.OrdinalIgnoreCase))
        {
            return template.IndexOf("{lang_code}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   template.IndexOf("{caption_lang_code}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   template.IndexOf("{字幕语言代码}", StringComparison.OrdinalIgnoreCase) >= 0;
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
    /// 使用当前变量池中的最新值渲染模板字符串，并进行 144 字符安全截断（VRChat 限制）
    /// </summary>
    public string Render(string template)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        string rendered = VariableRegex.Replace(template, match =>
        {
            string varName = match.Groups[1].Value;
            if (string.Equals(varName, "time", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.Now.ToString("HH:mm");
            }

            string lookupName = varName;
            if (string.Equals(varName, "lang", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(varName, "caption_lang", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(varName, "subtitle_lang", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(varName, "字幕语言", StringComparison.OrdinalIgnoreCase))
            {
                lookupName = "language";
            }
            else if (string.Equals(varName, "lang_code", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(varName, "caption_lang_code", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(varName, "字幕语言代码", StringComparison.OrdinalIgnoreCase))
            {
                lookupName = "language_code";
            }

            if (_variables.TryGetValue(lookupName, out var item))
            {
                return item.Value ?? string.Empty;
            }

            // 未知变量保持原样或保留
            return match.Value;
        });

        // 规范化多余空行，最多允许连续 1 个换行
        rendered = rendered.Replace("\r\n", "\n").Replace('\r', '\n');
        
        // VRChat 单条气泡最多 144 个字符限制
        if (rendered.Length > 144)
        {
            rendered = rendered.Substring(0, 144);
        }

        return rendered.Trim();
    }
}
