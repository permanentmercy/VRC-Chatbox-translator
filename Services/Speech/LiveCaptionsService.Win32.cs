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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_HIDE = 0;
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

    private async Task<IntPtr> EnsureLiveCaptionsProcessAsync(CancellationToken ct)
    {
        IntPtr hWnd = FindLiveCaptionsWindow();
        if (hWnd != IntPtr.Zero) return hWnd;

        StatusChanged?.Invoke("正在启动 Windows 11 原生实时字幕...");
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
            await Task.Delay(150, ct);
            hWnd = FindLiveCaptionsWindow();
            if (hWnd != IntPtr.Zero) return hWnd;
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
            int exStyle = GetWindowLong(_hWnd, GWL_EXSTYLE);
            ShowWindow(_hWnd, SW_MINIMIZE);
            SetWindowLong(_hWnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
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
                StatusChanged?.Invoke("已显示 Windows 实时字幕窗口 (您可在其界面调整麦克风或语言设置)");
            }
            else
            {
                HideNativeWindow();
                StatusChanged?.Invoke("已隐藏 Windows 实时字幕窗口 (后台静默工作)");
            }
        }
        catch { }
    }
}
