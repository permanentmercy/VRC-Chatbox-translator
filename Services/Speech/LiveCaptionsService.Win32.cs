using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public partial class LiveCaptionsService
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static RECT _savedNativeRect;
    private static bool _hasSavedNativeRect = false;

    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    /// <summary>
    /// 检测当前操作系统是否支持 Windows 11 实时字幕
    /// </summary>
    public static bool IsSupported()
    {
        try
        {
            string sys32Path = Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe");
            return File.Exists(sys32Path);
        }
        catch
        {
            return false;
        }
    }

    private IntPtr FindLiveCaptionsWindow()
    {
        IntPtr hWnd = FindWindow("LiveCaptionsDesktopWindow", null);
        if (hWnd != IntPtr.Zero && IsWindow(hWnd))
        {
            return hWnd;
        }

        var procs = Process.GetProcessesByName("LiveCaptions");
        foreach (var proc in procs)
        {
            if (proc.MainWindowHandle != IntPtr.Zero && IsWindow(proc.MainWindowHandle))
            {
                return proc.MainWindowHandle;
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private async Task<IntPtr> EnsureLiveCaptionsProcessAsync(CancellationToken ct, bool hideImmediately = true)
    {
        IntPtr hWnd = FindLiveCaptionsWindow();
        if (hWnd != IntPtr.Zero)
        {
            if (hideImmediately) HideNativeWindow();
            return hWnd;
        }

        StatusChanged?.Invoke("正在启动 Windows 11 实时字幕...");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "LiveCaptions.exe"),
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("LiveCaptions") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"启动 Windows 实时字幕失败: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000 && !ct.IsCancellationRequested)
        {
            await Task.Delay(20, ct);
            hWnd = FindLiveCaptionsWindow();
            if (hWnd != IntPtr.Zero)
            {
                if (hideImmediately)
                {
                    HideNativeWindow();
                }
                return hWnd;
            }
        }

        return hWnd;
    }

    public void HideNativeWindow()
    {
        if (_hWnd == IntPtr.Zero || !IsWindow(_hWnd))
        {
            _hWnd = FindLiveCaptionsWindow();
        }
        if (_hWnd == IntPtr.Zero) return;

        try
        {
            if (GetWindowRect(_hWnd, out var r))
            {
                if (r.Left > -1000 && r.Top > -1000 && (r.Right - r.Left) > 100 && (r.Bottom - r.Top) > 30)
                {
                    _savedNativeRect = r;
                    _hasSavedNativeRect = true;
                }
            }

            int exStyle = GetWindowLong(_hWnd, GWL_EXSTYLE);
            // 1. 设置工具窗口样式，防止任务栏驻留图标
            SetWindowLong(_hWnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            // 2. 最小化窗口：LiveCaptions 保持后台文字监听，且绝不会在识别文字时触发任何置顶弹窗
            ShowWindow(_hWnd, SW_MINIMIZE);
            // 3. 将最小化产生的桌面占位底座移出屏幕可视范围 (-32000, -32000)，彻底消除屏幕边缘的纯灰色小方块！
            SetWindowPos(_hWnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch { }
    }

    public void RestoreNativeWindow()
    {
        if (_hWnd == IntPtr.Zero || !IsWindow(_hWnd))
        {
            _hWnd = FindLiveCaptionsWindow();
        }
        if (_hWnd == IntPtr.Zero) return;

        try
        {
            int exStyle = GetWindowLong(_hWnd, GWL_EXSTYLE);
            SetWindowLong(_hWnd, GWL_EXSTYLE, exStyle & ~WS_EX_TOOLWINDOW);

            int x = _hasSavedNativeRect ? _savedNativeRect.Left : 200;
            int y = _hasSavedNativeRect ? _savedNativeRect.Top : 100;
            int w = _hasSavedNativeRect ? (_savedNativeRect.Right - _savedNativeRect.Left) : 800;
            int h = _hasSavedNativeRect ? (_savedNativeRect.Bottom - _savedNativeRect.Top) : 100;

            if (x < 0 || y < 0 || w < 100 || h < 40)
            {
                x = 200; y = 100; w = 800; h = 100;
            }

            SetWindowPos(_hWnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            ShowWindow(_hWnd, SW_RESTORE);
            SetForegroundWindow(_hWnd);
        }
        catch { }
    }

    public void ToggleNativeWindow()
    {
        if (_hWnd == IntPtr.Zero || !IsWindow(_hWnd))
        {
            _hWnd = FindLiveCaptionsWindow();
        }
        if (_hWnd == IntPtr.Zero) return;

        try
        {
            int exStyle = GetWindowLong(_hWnd, GWL_EXSTYLE);
            bool isTool = (exStyle & WS_EX_TOOLWINDOW) != 0;
            if (isTool)
            {
                RestoreNativeWindow();
                StatusChanged?.Invoke("已显示 Windows 实时字幕窗口 ");
            }
            else
            {
                HideNativeWindow();
                StatusChanged?.Invoke("已隐藏 Windows 实时字幕窗口");
            }
        }
        catch { }
    }
}
