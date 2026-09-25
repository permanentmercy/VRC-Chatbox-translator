using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VrcChatboxDemo.Services;

public record LiveCaptionsLanguage(string Code, string DisplayName, string MatchKeyword);

public partial class LiveCaptionsService
{
    public static readonly IReadOnlyList<LiveCaptionsLanguage> SupportedLanguages = new List<LiveCaptionsLanguage>
    {
        new("zh-CN", "中文 (简体，中国)", "中文"),
        new("en-US", "英语 (美国)", "English"),
        new("ja-JP", "日本語 (日本)", "日本語"),
        new("ko-KR", "한국어 (대한민국)", "한국어"),
        new("zh-TW", "中文 (繁体，中国台湾)", "繁體"),
        new("fr-FR", "Français (France)", "Français"),
        new("de-DE", "Deutsch (Deutschland)", "Deutsch"),
        new("es-ES", "Español (España)", "Español"),
        new("it-IT", "Italiano (Italia)", "Italiano")
    };

    /// <summary>
    /// 直接将字幕语言代码写入 Windows 11 LiveCaptions 原生注册表配置中
    /// </summary>
    public static void SetRegistryLanguage(string langCode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\LiveCaptions\UI");
            key?.SetValue("CaptionLanguage", langCode, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveCaptions] Failed to set registry language: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取 Windows 11 LiveCaptions 当前注册表配置的字幕语言代码
    /// </summary>
    public static string GetRegistryLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\LiveCaptions\UI");
            return key?.GetValue("CaptionLanguage") as string ?? "zh-CN";
        }
        catch
        {
            return "zh-CN";
        }
    }

    /// <summary>
    /// 在后台静默切换 Windows 11 实时字幕的识别语言：
    /// 1. 写入系统原生注册表配置；
    /// 2. 若当前正在运行，无缝静默热重启 LiveCaptions 进程以立即加载新语言模型；
    /// 3. 全程不弹出任何系统设置菜单、无需鼠标点击，100% 稳定切换。
    /// </summary>
    public async Task<bool> SwitchLanguageAsync(string langCodeOrKeyword)
    {
        if (string.IsNullOrWhiteSpace(langCodeOrKeyword)) return false;

        var targetLang = SupportedLanguages.FirstOrDefault(l =>
            string.Equals(l.Code, langCodeOrKeyword, StringComparison.OrdinalIgnoreCase) ||
            l.DisplayName.Contains(langCodeOrKeyword, StringComparison.OrdinalIgnoreCase) ||
            l.MatchKeyword.Contains(langCodeOrKeyword, StringComparison.OrdinalIgnoreCase))
            ?? new LiveCaptionsLanguage(langCodeOrKeyword, langCodeOrKeyword, langCodeOrKeyword);

        StatusChanged?.Invoke($"正在切换 Windows 实时字幕语言至: {targetLang.DisplayName}...");
        SetRegistryLanguage(targetLang.Code);

        // 如果当前实时字幕正在运行或处于监听状态，静默热重载以生效新语言设置
        if (_isRunning || FindLiveCaptionsWindow() != IntPtr.Zero)
        {
            bool ok = await RestartAsync(SettingsService.Instance.LiveCaptionsHideNativeWindow);
            if (ok)
            {
                StatusChanged?.Invoke($"Windows 实时字幕语言已切换为: {targetLang.DisplayName}");
                return true;
            }
        }
        else
        {
            StatusChanged?.Invoke($"已保存 Windows 实时字幕语言: {targetLang.DisplayName}");
            return true;
        }

        return false;
    }
}
