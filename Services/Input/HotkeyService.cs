using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VrcChatboxDemo.Services;

public partial class HotkeyService : IDisposable
{
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

    public void EnsureImeActive(IntPtr? specificHwnd = null)
    {
        IntPtr target = specificHwnd ?? _currentHwnd;
        if (target == IntPtr.Zero) target = _mainWindowHwnd;
        if (target != IntPtr.Zero)
        {
            EnsureImeActive(target);
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
