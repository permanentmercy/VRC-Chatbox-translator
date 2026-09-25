using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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

                GetWindowThreadProcessId(_hWnd, out uint livePid);
                if (livePid == 0) return false;

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
                    if (expPattern != null) expPattern.Expand();
                    else SimulateClick(settingsBtn.CurrentBoundingRectangle);
                }

                await Task.Delay(300);

                // 3. 在系统全局范围内按 LiveCaptions PID 查找弹出的菜单或浮层元素
                var root = uia.GetRootElement();
                var pidCond = uia.CreatePropertyCondition(UIA_PropertyIds.UIA_ProcessIdPropertyId, (int)livePid);
                var liveElements = root.FindAll(TreeScope.TreeScope_Descendants, pidCond);

                IUIAutomationElement? langMenuItem = null;
                for (int i = 0; i < liveElements.Length; i++)
                {
                    var el = liveElements.GetElement(i);
                    string name = el.CurrentName ?? string.Empty;
                    if (name.Contains("标注语言", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Caption language", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("语言", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Language", StringComparison.OrdinalIgnoreCase))
                    {
                        langMenuItem = el;
                        break;
                    }
                }

                if (langMenuItem == null)
                {
                    StatusChanged?.Invoke("未在弹出菜单中定位到【标注语言】项");
                    SendEscKey();
                    return false;
                }

                // 4. 展开“标注语言”子菜单
                bool subMenuTriggered = false;
                var langExp = langMenuItem.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId) as IUIAutomationExpandCollapsePattern;
                if (langExp != null)
                {
                    langExp.Expand();
                    subMenuTriggered = true;
                }

                var langInv = langMenuItem.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) as IUIAutomationInvokePattern;
                if (langInv != null)
                {
                    langInv.Invoke();
                    subMenuTriggered = true;
                }

                if (!subMenuTriggered)
                {
                    SimulateClick(langMenuItem.CurrentBoundingRectangle);
                }

                await Task.Delay(300);

                // 5. 在展开的子菜单中重新扫描 PID 对应的元素以定位目标语言
                liveElements = root.FindAll(TreeScope.TreeScope_Descendants, pidCond);
                IUIAutomationElement? targetLangItem = null;

                for (int i = 0; i < liveElements.Length; i++)
                {
                    var el = liveElements.GetElement(i);
                    string name = el.CurrentName ?? string.Empty;
                    if (name.Contains(targetLang.MatchKeyword, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(targetLang.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(targetLang.Code) && name.Contains(targetLang.Code, StringComparison.OrdinalIgnoreCase)))
                    {
                        targetLangItem = el;
                        break;
                    }
                }

                if (targetLangItem != null)
                {
                    var targetInv = targetLangItem.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) as IUIAutomationInvokePattern;
                    if (targetInv != null)
                    {
                        targetInv.Invoke();
                    }
                    else
                    {
                        var targetSel = targetLangItem.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId) as IUIAutomationSelectionItemPattern;
                        if (targetSel != null) targetSel.Select();
                        else SimulateClick(targetLangItem.CurrentBoundingRectangle);
                    }

                    await Task.Delay(150);
                    SendEscKey(); // 确保菜单关闭

                    StatusChanged?.Invoke($"Windows 实时字幕语言已切换为: {targetLang.DisplayName}");
                    return true;
                }

                // 退出并关闭遗留的弹出菜单
                SendEscKey();
                SendEscKey();

                StatusChanged?.Invoke($"未在实时字幕菜单中匹配到语言: {targetLang.DisplayName}，可能系统尚未下载该语言包");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaptions] SwitchLanguage failed: {ex.Message}");
                StatusChanged?.Invoke($"切换实时字幕语言出错: {ex.Message}");
                try { SendEscKey(); } catch { }
                return false;
            }
        });
    }

    private static void SimulateClick(tagRECT rect)
    {
        if (rect.right > rect.left && rect.bottom > rect.top)
        {
            int cx = (rect.left + rect.right) / 2;
            int cy = (rect.top + rect.bottom) / 2;
            SetCursorPos(cx, cy);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTDOWN, cx, cy, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTUP, cx, cy, 0, UIntPtr.Zero);
        }
    }

    private static void SendEscKey()
    {
        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
