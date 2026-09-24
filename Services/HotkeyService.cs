using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VrcChatboxDemo.Services;

public class HotkeyService : IDisposable
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

    private readonly WndProcDelegate _wndProcDelegate;
    private readonly IntPtr _wndProcPtr;

    private IntPtr _currentHwnd;
    private IntPtr _mainWindowHwnd;
    private IntPtr _mainOldWndProc;
    private IntPtr _immersiveWindowHwnd;
    private IntPtr _immersiveOldWndProc;
    private IntPtr _lastExternalHwnd = IntPtr.Zero;

    private bool _isWakeRegistered;
    private bool _isImmersiveRegistered;

    private uint _lastWakeModifiers;
    private uint _lastWakeKey;
    private uint _lastImmersiveModifiers;
    private uint _lastImmersiveKey;

    public bool IsWakeRegistered => _isWakeRegistered;
    public int LastWakeError { get; private set; }

    public bool IsImmersiveRegistered => _isImmersiveRegistered;
    public int LastImmersiveError { get; private set; }

    public event Action? HotkeyPressed;
    public event Action? ImmersiveHotkeyPressed;
    public event Action? HotkeyRegistrationChanged;

    public HotkeyService()
    {
        _wndProcDelegate = HookWndProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
    }

    private static void Log(string message)
    {
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hotkey.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch { }
    }

    public void Initialize(IntPtr mainWindowHwnd)
    {
        _mainWindowHwnd = mainWindowHwnd;
        _currentHwnd = mainWindowHwnd;

        if (_mainWindowHwnd != IntPtr.Zero && _mainOldWndProc == IntPtr.Zero)
        {
            _mainOldWndProc = SetWindowLongPtr(_mainWindowHwnd, GWLP_WNDPROC, _wndProcPtr);
            Log($"Hooked MainWindow {_mainWindowHwnd}, oldWndProc: {_mainOldWndProc}");
        }
    }

    public void SetImmersiveWindow(IntPtr immersiveHwnd)
    {
        _immersiveWindowHwnd = immersiveHwnd;

        if (_immersiveWindowHwnd != IntPtr.Zero && _immersiveOldWndProc == IntPtr.Zero)
        {
            _immersiveOldWndProc = SetWindowLongPtr(_immersiveWindowHwnd, GWLP_WNDPROC, _wndProcPtr);
            Log($"Hooked ImmersiveWindow {_immersiveWindowHwnd}, oldWndProc: {_immersiveOldWndProc}");
        }
    }

    public void SwitchActiveWindow(bool isImmersive)
    {
        IntPtr target = isImmersive ? _immersiveWindowHwnd : _mainWindowHwnd;
        if (target == IntPtr.Zero || target == _currentHwnd) return;

        Log($"SwitchActiveWindow: isImmersive={isImmersive}, target={target}, prev={_currentHwnd}");

        bool hadWake = _isWakeRegistered;
        bool hadImmersive = _isImmersiveRegistered;

        UnregisterWake();
        UnregisterImmersive();

        _currentHwnd = target;

        if (hadWake && _lastWakeKey != 0)
        {
            RegisterWake(_lastWakeModifiers, _lastWakeKey);
        }

        if (hadImmersive && _lastImmersiveKey != 0)
        {
            RegisterImmersive(_lastImmersiveModifiers, _lastImmersiveKey);
        }
    }

    public bool Register(uint modifiers, uint vk)
    {
        return RegisterWake(modifiers, vk);
    }

    public void Unregister()
    {
        UnregisterWake();
    }

    public bool RegisterWake(uint modifiers, uint vk)
    {
        _lastWakeModifiers = modifiers;
        _lastWakeKey = vk;

        if (_currentHwnd == IntPtr.Zero)
        {
            Log("RegisterWake failed: _currentHwnd is Zero");
            return false;
        }

        if (_isWakeRegistered)
        {
            UnregisterWake();
        }

        _isWakeRegistered = RegisterHotKey(_currentHwnd, HOTKEY_WAKE_ID, modifiers | MOD_NOREPEAT, vk);
        int err = Marshal.GetLastWin32Error();
        if (!_isWakeRegistered)
        {
            // 某些系统环境不兼容 MOD_NOREPEAT，进行自动降级重试
            _isWakeRegistered = RegisterHotKey(_currentHwnd, HOTKEY_WAKE_ID, modifiers, vk);
            err = Marshal.GetLastWin32Error();
        }

        LastWakeError = _isWakeRegistered ? 0 : err;
        Log($"RegisterWake on {_currentHwnd}: res={_isWakeRegistered}, mod=0x{modifiers:X}, vk=0x{vk:X}, err={err}");
        HotkeyRegistrationChanged?.Invoke();
        return _isWakeRegistered;
    }

    public void UnregisterWake()
    {
        if (_currentHwnd != IntPtr.Zero && _isWakeRegistered)
        {
            UnregisterHotKey(_currentHwnd, HOTKEY_WAKE_ID);
            _isWakeRegistered = false;
            LastWakeError = 0;
            Log($"UnregisterWake on {_currentHwnd}");
            HotkeyRegistrationChanged?.Invoke();
        }
    }

    public bool RegisterImmersive(uint modifiers, uint vk)
    {
        _lastImmersiveModifiers = modifiers;
        _lastImmersiveKey = vk;

        if (_currentHwnd == IntPtr.Zero)
        {
            Log("RegisterImmersive failed: _currentHwnd is Zero");
            return false;
        }

        if (_isImmersiveRegistered)
        {
            UnregisterImmersive();
        }

        _isImmersiveRegistered = RegisterHotKey(_currentHwnd, HOTKEY_IMMERSIVE_ID, modifiers | MOD_NOREPEAT, vk);
        int err = Marshal.GetLastWin32Error();
        if (!_isImmersiveRegistered)
        {
            _isImmersiveRegistered = RegisterHotKey(_currentHwnd, HOTKEY_IMMERSIVE_ID, modifiers, vk);
            err = Marshal.GetLastWin32Error();
        }

        LastImmersiveError = _isImmersiveRegistered ? 0 : err;
        Log($"RegisterImmersive on {_currentHwnd}: res={_isImmersiveRegistered}, mod=0x{modifiers:X}, vk=0x{vk:X}, err={err}");
        HotkeyRegistrationChanged?.Invoke();
        return _isImmersiveRegistered;
    }

    public void UnregisterImmersive()
    {
        if (_currentHwnd != IntPtr.Zero && _isImmersiveRegistered)
        {
            UnregisterHotKey(_currentHwnd, HOTKEY_IMMERSIVE_ID);
            _isImmersiveRegistered = false;
            LastImmersiveError = 0;
            Log($"UnregisterImmersive on {_currentHwnd}");
            HotkeyRegistrationChanged?.Invoke();
        }
    }

    public void ActivateWindow()
    {
        ActivateWindow(_mainWindowHwnd);
    }

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
            uint foreThread = GetWindowThreadProcessId(fore, IntPtr.Zero);
            uint targetThread = GetWindowThreadProcessId(_lastExternalHwnd, IntPtr.Zero);

            if (foreThread != targetThread && foreThread != 0 && targetThread != 0)
            {
                AttachThreadInput(foreThread, targetThread, true);
                SetForegroundWindow(_lastExternalHwnd);
                BringWindowToTop(_lastExternalHwnd);
                AttachThreadInput(foreThread, targetThread, false);
            }
            else
            {
                SetForegroundWindow(_lastExternalHwnd);
                BringWindowToTop(_lastExternalHwnd);
            }
            return true;
        }

        if (_mainWindowHwnd != IntPtr.Zero)
        {
            ShowWindow(_mainWindowHwnd, SW_MINIMIZE);
        }
        return false;
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

        IntPtr foreWnd = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(foreWnd, IntPtr.Zero);
        uint appThread = GetCurrentThreadId();

        if (foreThread != appThread)
        {
            AttachThreadInput(foreThread, appThread, true);
            SetForegroundWindow(target);
            BringWindowToTop(target);
            AttachThreadInput(foreThread, appThread, false);
        }
        else
        {
            SetForegroundWindow(target);
            BringWindowToTop(target);
        }
    }

    private IntPtr HookWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_ACTIVATE)
        {
            int wa = (int)(wParam.ToInt64() & 0xFFFF);
            if (wa != 0) // WA_ACTIVE or WA_CLICKACTIVE
            {
                IntPtr deactivatedHwnd = lParam;
                if (deactivatedHwnd != IntPtr.Zero && deactivatedHwnd != _mainWindowHwnd && deactivatedHwnd != _immersiveWindowHwnd)
                {
                    if (IsWindow(deactivatedHwnd))
                    {
                        _lastExternalHwnd = deactivatedHwnd;
                        Log($"WM_ACTIVATE recorded external previous window: {_lastExternalHwnd}");
                    }
                }
            }
        }
        else if (msg == WM_HOTKEY)
        {
            int id = (int)wParam;
            Log($"HookWndProc WM_HOTKEY received on hWnd {hWnd}: id={id}");
            if (id == HOTKEY_WAKE_ID)
            {
                IntPtr currentFore = GetForegroundWindow();
                if (currentFore != IntPtr.Zero && currentFore != _mainWindowHwnd && currentFore != _immersiveWindowHwnd && GetAncestor(currentFore, GA_ROOT) != _mainWindowHwnd)
                {
                    _lastExternalHwnd = currentFore;
                    Log($"WM_HOTKEY recorded external previous window: {_lastExternalHwnd}");
                }
                HotkeyPressed?.Invoke();
                return IntPtr.Zero;
            }
            else if (id == HOTKEY_IMMERSIVE_ID)
            {
                ImmersiveHotkeyPressed?.Invoke();
                return IntPtr.Zero;
            }
        }
        else if (msg == App.WM_WAKE_UP_APP && App.WM_WAKE_UP_APP != 0)
        {
            Log($"HookWndProc WM_WAKE_UP_APP received on hWnd {hWnd}");
            App.ShowMainWindow();
            return IntPtr.Zero;
        }

        IntPtr prev = (hWnd == _immersiveWindowHwnd) ? _immersiveOldWndProc : _mainOldWndProc;
        if (prev != IntPtr.Zero)
        {
            return CallWindowProc(prev, hWnd, msg, wParam, lParam);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterWake();
        UnregisterImmersive();

        if (_mainWindowHwnd != IntPtr.Zero && _mainOldWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_mainWindowHwnd, GWLP_WNDPROC, _mainOldWndProc);
            _mainOldWndProc = IntPtr.Zero;
        }

        if (_immersiveWindowHwnd != IntPtr.Zero && _immersiveOldWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_immersiveWindowHwnd, GWLP_WNDPROC, _immersiveOldWndProc);
            _immersiveOldWndProc = IntPtr.Zero;
        }
    }
}
