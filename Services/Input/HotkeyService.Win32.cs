using System;
using System.Runtime.InteropServices;

namespace VrcChatboxDemo.Services;

public partial class HotkeyService
{
    private const int HOTKEY_WAKE_ID = 9001;
    private const int HOTKEY_IMMERSIVE_ID = 9002;
    private const uint WM_HOTKEY = 0x0312;
    private const int GWLP_WNDPROC = -4;
    private const int HTCAPTION = 2;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint GA_ROOT = 2;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        else
            return SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public void DragWindow(IntPtr? specificHwnd = null)
    {
        IntPtr target = specificHwnd ?? _mainWindowHwnd;
        if (target != IntPtr.Zero)
        {
            ReleaseCapture();
            SendMessage(target, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
    }

    public bool IsWindowForeground(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero) return false;
        IntPtr fore = GetForegroundWindow();
        if (fore == IntPtr.Zero) return false;
        if (fore == targetHwnd) return true;
        return GetAncestor(fore, GA_ROOT) == targetHwnd;
    }

    public bool RestorePreviousWindowFocus()
    {
        Log($"RestorePreviousWindowFocus called, _lastExternalHwnd={_lastExternalHwnd}");
        if (_lastExternalHwnd != IntPtr.Zero && IsWindow(_lastExternalHwnd))
        {
            if (IsIconic(_lastExternalHwnd))
            {
                ShowWindow(_lastExternalHwnd, SW_RESTORE);
            }

            IntPtr fore = GetForegroundWindow();
            keybd_event(VK_MENU, 0, 0, 0);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
            SetForegroundWindow(_lastExternalHwnd);
            BringWindowToTop(_lastExternalHwnd);
            return true;
        }

        if (_mainWindowHwnd != IntPtr.Zero)
        {
            ShowWindow(_mainWindowHwnd, SW_MINIMIZE);
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetConversionStatus(IntPtr hIMC, uint fdwConversion, uint fdwSentence);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint lpfdwConversion, out uint lpfdwSentence);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);

    [DllImport("user32.dll")]
    private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint Flags);

    private const uint IME_CMODE_NATIVE = 0x0001;
    private const uint KLF_ACTIVATE = 0x00000001;

    /// <summary>
    /// 确保当前窗口的中文输入法处于打开与中文候选模式。
    /// 彻底解决从游戏切回时线程输入法被锁在纯英文导致只能敲出英文字母、没有候选框的 Windows 底层输入法失联问题。
    /// </summary>
    public static void EnsureImeActive(IntPtr hWnd)
    {
        try
        {
            // 1. 若当前键盘布局为纯英文 (0x0409)，但系统安装了中文输入法 (0x0804)，则优先切至中文输入法布局
            IntPtr currentHkl = GetKeyboardLayout(0);
            ushort langId = (ushort)((long)currentHkl & 0xFFFF);
            if (langId != 0x0804)
            {
                int count = GetKeyboardLayoutList(0, Array.Empty<IntPtr>());
                if (count > 0)
                {
                    IntPtr[] list = new IntPtr[count];
                    GetKeyboardLayoutList(count, list);
                    foreach (var hkl in list)
                    {
                        ushort lId = (ushort)((long)hkl & 0xFFFF);
                        if (lId == 0x0804)
                        {
                            ActivateKeyboardLayout(hkl, KLF_ACTIVATE);
                            break;
                        }
                    }
                }
            }

            // 2. 通过 IMM32 确保输入法打开 (OpenStatus = true, ConversionMode = NATIVE 中文模式)
            if (hWnd != IntPtr.Zero)
            {
                IntPtr hIMC = ImmGetContext(hWnd);
                if (hIMC != IntPtr.Zero)
                {
                    ImmSetOpenStatus(hIMC, true);
                    if (ImmGetConversionStatus(hIMC, out uint conv, out uint sent))
                    {
                        conv |= IME_CMODE_NATIVE;
                        ImmSetConversionStatus(hIMC, conv, sent);
                    }
                    ImmReleaseContext(hWnd, hIMC);
                }
            }
        }
        catch { }
    }

    public void ActivateWindow(IntPtr? specificHwnd = null)
    {
        IntPtr target = specificHwnd ?? _mainWindowHwnd;
        if (target == IntPtr.Zero) return;

        IntPtr currentFore = GetForegroundWindow();
        if (currentFore != IntPtr.Zero && currentFore != target && GetAncestor(currentFore, GA_ROOT) != target)
        {
            _lastExternalHwnd = currentFore;
        }

        if (IsIconic(target))
        {
            ShowWindow(target, SW_RESTORE);
        }
        else
        {
            ShowWindow(target, SW_SHOW);
        }

        // 核心修复：坚决不调用 AttachThreadInput(foreThread, appThread, true)！
        // 理由：AttachThreadInput 会将本程序线程强行与游戏(VRChat)的前台线程输入队列绑定，
        // 从而同步复制游戏中的“纯英文键盘布局/关闭IME”状态，导致本程序唤出后输入法无法打出中文候选词。
        // 使用安全前台激活机制：
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        BringWindowToTop(target);
        SetForegroundWindow(target);

        // 激活目标窗口时同步确保输入法就绪
        EnsureImeActive(target);
    }
}
