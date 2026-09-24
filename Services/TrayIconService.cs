using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VrcChatboxDemo.Services;

public class TrayIconService : IDisposable
{
    private IntPtr _hWnd;
    private bool _isInitialized;
    private Action? _onOpenMainWindow;
    private Action? _onToggleImmersive;
    private Action? _onQuitApp;
    private SubclassProc? _subclassDelegate;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 200;
    private const int WM_CLOSE = 0x0010;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;

    private const int CMD_OPEN = 1001;
    private const int CMD_IMMERSIVE = 1002;
    private const int CMD_QUIT = 1003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    public void Initialize(IntPtr hWnd, Action onOpenMainWindow, Action onToggleImmersive, Action onQuitApp)
    {
        if (_isInitialized) return;
        _hWnd = hWnd;
        _onOpenMainWindow = onOpenMainWindow;
        _onToggleImmersive = onToggleImmersive;
        _onQuitApp = onQuitApp;

        IntPtr hIcon = IntPtr.Zero;
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = Path.Combine(@"H:\program\chatbox\VrcChatboxDemo\Assets", "AppIcon.ico");
            }
            if (File.Exists(iconPath))
            {
                hIcon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, 16, 16, 0x00000010 /* LR_LOADFROMFILE */);
            }
        }
        catch { }

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "Vrc Chatbox By Mercy's BUG"
        };

        Shell_NotifyIcon(NIM_ADD, ref nid);

        _subclassDelegate = WindowSubclass;
        SetWindowSubclass(_hWnd, _subclassDelegate, (UIntPtr)999, IntPtr.Zero);
        _isInitialized = true;
    }

    private IntPtr WindowSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            int msg = (int)lParam;
            if (msg == WM_LBUTTONUP || msg == WM_LBUTTONDBLCLK)
            {
                _onOpenMainWindow?.Invoke();
                return IntPtr.Zero;
            }
            else if (msg == WM_RBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        else if (uMsg == WM_CLOSE)
        {
            // 关闭按钮截获：隐藏主窗口至托盘，保持后台运行
            ShowWindow(hWnd, 0 /* SW_HIDE */);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        AppendMenu(hMenu, MF_STRING, CMD_OPEN, "打开主界面");
        AppendMenu(hMenu, MF_STRING, CMD_IMMERSIVE, "切换沉浸极简模式");
        AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(hMenu, MF_STRING, CMD_QUIT, "退出程序");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(_hWnd);

        int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hWnd, IntPtr.Zero);
        DestroyMenu(hMenu);

        if (cmd == CMD_OPEN)
        {
            _onOpenMainWindow?.Invoke();
        }
        else if (cmd == CMD_IMMERSIVE)
        {
            _onToggleImmersive?.Invoke();
        }
        else if (cmd == CMD_QUIT)
        {
            _onQuitApp?.Invoke();
        }
    }

    public void RemoveTrayIcon()
    {
        if (_isInitialized)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hWnd,
                uID = 1
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);

            if (_subclassDelegate != null)
            {
                RemoveWindowSubclass(_hWnd, _subclassDelegate, (UIntPtr)999);
                _subclassDelegate = null;
            }
            _isInitialized = false;
        }
    }

    public void Dispose()
    {
        RemoveTrayIcon();
        GC.SuppressFinalize(this);
    }
}
