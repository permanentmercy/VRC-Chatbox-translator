using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Interop.UIAutomationClient;

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
    /// 在后台静默切换 Windows 11 实时字幕的识别语言，无需手动呼出黑底窗口
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

        return await Task.Run(async () =>
        {
            try
            {
                if (_hWnd == IntPtr.Zero || !IsWindow(_hWnd))
                {
                    _hWnd = FindLiveCaptionsWindow();
                }

                if (_hWnd == IntPtr.Zero)
                {
                    StatusChanged?.Invoke("未找到 Windows 实时字幕窗口，请先开启实时字幕");
                    return false;
                }

                var uia = new CUIAutomation();
                var windowElement = uia.ElementFromHandle(_hWnd);
                if (windowElement == null) return false;

                // 1. 查找设置齿轮按钮 (AutomationId: "SettingsButton" 或包含设置字样)
                var condSettings = uia.CreateOrCondition(
                    uia.CreatePropertyCondition(UIA_PropertyIds.UIA_AutomationIdPropertyId, "SettingsButton"),
                    uia.CreatePropertyCondition(UIA_PropertyIds.UIA_NamePropertyId, "设置")
                );

                var settingsBtn = windowElement.FindFirst(TreeScope.TreeScope_Descendants, condSettings);
                if (settingsBtn == null)
                {
                    // 模糊查找 Button 类型
                    var btnCond = uia.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_ButtonControlTypeId);
                    var allBtns = windowElement.FindAll(TreeScope.TreeScope_Descendants, btnCond);
                    for (int i = 0; i < allBtns.Length; i++)
                    {
                        var b = allBtns.GetElement(i);
                        string bName = b.CurrentName ?? string.Empty;
                        string bId = b.CurrentAutomationId ?? string.Empty;
                        if (bId.Contains("Setting", StringComparison.OrdinalIgnoreCase) ||
                            bName.Contains("设置", StringComparison.OrdinalIgnoreCase) ||
                            bName.Contains("Setting", StringComparison.OrdinalIgnoreCase))
                        {
                            settingsBtn = b;
                            break;
                        }
                    }
                }

                if (settingsBtn == null)
                {
                    StatusChanged?.Invoke("未找到实时字幕设置按钮");
                    return false;
                }

                // 2. 触发点击设置按钮弹出菜单
                var invokePattern = settingsBtn.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) as IUIAutomationInvokePattern;
                if (invokePattern != null)
                {
                    invokePattern.Invoke();
                }
                else
                {
                    var expPattern = settingsBtn.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern;
                    expPattern?.Expand();
                }

                await Task.Delay(250);

                // 3. 在桌面根元素查找弹出的菜单
                var root = uia.GetRootElement();
                var menuCond = uia.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_MenuControlTypeId);
                var menus = root.FindAll(TreeScope.TreeScope_Children, menuCond);

                IUIAutomationElement? langMenuItem = null;

                for (int m = 0; m < menus.Length; m++)
                {
                    var menuEl = menus.GetElement(m);
                    var itemCond = uia.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_MenuItemControlTypeId);
                    var items = menuEl.FindAll(TreeScope.TreeScope_Descendants, itemCond);

                    for (int i = 0; i < items.Length; i++)
                    {
                        var it = items.GetElement(i);
                        string itName = it.CurrentName ?? string.Empty;
                        if (itName.Contains("标注语言", StringComparison.OrdinalIgnoreCase) ||
                            itName.Contains("Caption language", StringComparison.OrdinalIgnoreCase) ||
                            itName.Contains("语言", StringComparison.OrdinalIgnoreCase) ||
                            itName.Contains("Language", StringComparison.OrdinalIgnoreCase))
                        {
                            langMenuItem = it;
                            break;
                        }
                    }
                    if (langMenuItem != null) break;
                }

                if (langMenuItem != null)
                {
                    // 展开“标注语言”子菜单
                    var langExp = langMenuItem.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern;
                    langExp?.Expand();

                    var langInv = langMenuItem.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) as IUIAutomationInvokePattern;
                    langInv?.Invoke();

                    await Task.Delay(250);

                    // 4. 在展开的子菜单中查找目标语言
                    var allMenuItems = root.FindAll(TreeScope.TreeScope_Descendants,
                        uia.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_MenuItemControlTypeId));

                    IUIAutomationElement? targetLangItem = null;
                    for (int i = 0; i < allMenuItems.Length; i++)
                    {
                        var it = allMenuItems.GetElement(i);
                        string itName = it.CurrentName ?? string.Empty;
                        if (itName.Contains(targetLang.MatchKeyword, StringComparison.OrdinalIgnoreCase) ||
                            itName.Contains(targetLang.DisplayName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetLangItem = it;
                            break;
                        }
                    }

                    if (targetLangItem != null)
                    {
                        var targetInv = targetLangItem.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) as IUIAutomationInvokePattern;
                        targetInv?.Invoke();

                        var targetSel = targetLangItem.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId) as IUIAutomationSelectionItemPattern;
                        targetSel?.Select();

                        await Task.Delay(100);
                        StatusChanged?.Invoke($"Windows 实时字幕语言已切换为: {targetLang.DisplayName}");
                        return true;
                    }
                }

                // 关闭可能遗留的菜单（再次点击设置按钮或按 ESC）
                try { invokePattern?.Invoke(); } catch { }

                StatusChanged?.Invoke($"未在实时字幕菜单中匹配到语言: {targetLang.DisplayName}，可能系统尚未下载该离线语言包");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaptions] SwitchLanguage failed: {ex.Message}");
                StatusChanged?.Invoke($"切换实时字幕语言出错: {ex.Message}");
                return false;
            }
        });
    }
}
