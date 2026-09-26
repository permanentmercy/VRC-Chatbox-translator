using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VrcChatboxDemo.Services;

/// <summary>
/// VRChat 游戏内按键守卫：
/// 监听 Windows 全局底层键盘事件，检测玩家在 VRChat 游戏窗口前台时的打字与回车发送行为，
/// 从而通知常驻文本服务智能让位与避让，避免常驻文本覆盖游戏内打字。
/// </summary>
public class VrcInGameGuard : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_RETURN = 0x0D;
    private const int VK_BACK = 0x08;
    private const int VK_SPACE = 0x20;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_Y = 0x59; // VRChat 默认聊天框呼出键
    private const int VK_PROCESSKEY = 0xE5; // Windows IME 输入法按键转换事件

    private bool _isChatboxOpen = false;
    private DateTime _lastChatboxActivity = DateTime.MinValue;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDisposed = false;

    // 缓存上一次检测到的前台 PID 与是否为 VRChat，降低频繁进程查询开销
    private uint _lastForegroundPid = 0;
    private bool _lastIsVrc = false;
    private DateTime _lastCheckTime = DateTime.MinValue;
    private DateTime _lastVrcForegroundTime = DateTime.MinValue;

    /// <summary>
    /// 当玩家在 VRChat 游戏窗口内按回车发送消息时触发
    /// </summary>
    public event Action? InGameMessageSent;

    /// <summary>
    /// 当玩家在 VRChat 游戏窗口内打字输入时触发
    /// </summary>
    public event Action? InGameTyping;

    public bool IsEnabled { get; set; } = true;

    public VrcInGameGuard()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr moduleHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VrcInGameGuard] Failed to install keyboard hook: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsEnabled && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            try
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (IsVrcForeground())
                {
                    HandleVrcKeyEvent(vkCode);
                }
                else
                {
                    _isChatboxOpen = false;
                }
            }
            catch { }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool IsVrcForeground()
    {
        var now = DateTime.UtcNow;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        if (pid == _lastForegroundPid && (now - _lastCheckTime).TotalMilliseconds < 400)
        {
            return _lastIsVrc;
        }

        _lastForegroundPid = pid;
        _lastCheckTime = now;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string procName = proc.ProcessName;
            bool isDirectVrc = procName.Contains("VRChat", StringComparison.OrdinalIgnoreCase);
            if (isDirectVrc)
            {
                _lastVrcForegroundTime = now;
                _lastIsVrc = true;
                return true;
            }

            // 核心兼容：如果前台窗口是 Windows 输入法/候选框宿主 (如 TextInputHost, ctfmon 等)，
            // 且最近 3 秒内前台是 VRChat，则仍视作在 VRChat 中使用输入法打字，绝不提前中断打字避让状态
            if ((now - _lastVrcForegroundTime).TotalSeconds < 3.0 &&
                (procName.Contains("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                 procName.Contains("ctfmon", StringComparison.OrdinalIgnoreCase)))
            {
                _lastIsVrc = true;
                return true;
            }

            _lastIsVrc = false;
        }
        catch
        {
            _lastIsVrc = false;
        }

        return _lastIsVrc;
    }

    private void HandleVrcKeyEvent(int vkCode)
    {
        var now = DateTime.UtcNow;

        // 1. Esc 键: 取消并关闭聊天框，立即退出打字状态
        if (vkCode == VK_ESCAPE)
        {
            _isChatboxOpen = false;
            return;
        }

        // 2. 回车键 (VK_RETURN):
        // 只要在 VRChat 前台按回车，均视为发送聊天框消息或确认输入，必须立即触发消息发送避让！
        // 彻底解决通过鼠标点击呼出聊天框、或输入法候选转换期间状态被重置导致回车避让失效的硬伤
        if (vkCode == VK_RETURN)
        {
            _isChatboxOpen = false;
            InGameMessageSent?.Invoke();
            return;
        }

        // 3. Y 键 (VRChat 默认聊天框呼出键)、IME 输入法按键 (拼音/输入法模式) 或 退格键 (VK_BACK)
        // 在普通移动中玩家绝不会按退格键，一旦按退格必定是在编辑聊天框文字
        if (vkCode == VK_Y || vkCode == VK_PROCESSKEY || vkCode == VK_BACK)
        {
            _isChatboxOpen = true;
            _lastChatboxActivity = now;
            InGameTyping?.Invoke();
            return;
        }

        // 4. 处于聊天打字状态时，检测是否超时 (超过 15 秒无击键自动重置)
        if (_isChatboxOpen)
        {
            if ((now - _lastChatboxActivity).TotalSeconds > 15.0)
            {
                _isChatboxOpen = false;
                return;
            }

            // 在聊天框开启状态下，输入字符、退格、空格均算打字输入
            if (IsInputKey(vkCode))
            {
                _lastChatboxActivity = now;
                InGameTyping?.Invoke();
            }
        }
        // 5. 聊天框未开启时，移动键 (WASD)、空格跳跃、普通游戏操作键一律完全忽略，杜绝误避让
    }

    private static bool IsInputKey(int vkCode)
    {
        // 退格、空格
        if (vkCode == VK_BACK || vkCode == VK_SPACE) return true;

        // A-Z 字母键 (0x41 ~ 0x5A)
        if (vkCode >= 0x41 && vkCode <= 0x5A) return true;

        // 0-9 数字键 (0x30 ~ 0x39)
        if (vkCode >= 0x30 && vkCode <= 0x39) return true;

        // 小键盘数字键 (0x60 ~ 0x69)
        if (vkCode >= 0x60 && vkCode <= 0x69) return true;

        // 标点符号与输入法常用按键 (0xBA ~ 0xE2)
        if (vkCode >= 0xBA && vkCode <= 0xE2) return true;

        return false;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            Stop();
        }
        GC.SuppressFinalize(this);
    }
}
