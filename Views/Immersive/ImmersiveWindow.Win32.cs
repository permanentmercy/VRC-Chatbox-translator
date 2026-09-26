using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VrcChatboxDemo.Views;

public sealed partial class ImmersiveWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private const int IDC_SIZEALL = 32646;
    private static readonly IntPtr _sizeAllCursor = LoadCursor(IntPtr.Zero, IDC_SIZEALL);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fTransitionOnMaximized;
    }

    private const uint DWM_BB_ENABLE = 0x00000001;
    private const uint DWM_BB_BLURREGION = 0x00000002;

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    private const int RGN_OR = 2;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOREDRAW = 0x0008;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOCOPYBITS = 0x0100;

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    private static void ConfigureWindowTransparency(IntPtr hWnd)
    {
        MARGINS margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hWnd, ref margins);

        IntPtr hrgn = CreateRectRgn(-2, -2, -1, -1);
        var bb = new DWM_BLURBEHIND
        {
            dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = hrgn,
            fTransitionOnMaximized = false
        };
        DwmEnableBlurBehindWindow(hWnd, ref bb);
        DeleteObject(hrgn);

        uint cornerPref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(uint));
        uint borderColor = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        ClearWindowBackground(hWnd);
    }

    private static void ClearWindowBackground(IntPtr hWnd)
    {
        IntPtr hdc = GetDC(hWnd);
        if (hdc != IntPtr.Zero)
        {
            if (GetClientRect(hWnd, out RECT rect))
            {
                IntPtr blackBrush = CreateSolidBrush(0);
                FillRect(hdc, ref rect, blackBrush);
                DeleteObject(blackBrush);
            }
            ReleaseDC(hWnd, hdc);
        }
    }

    private IntPtr ImmersiveSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == 0x0083 /* WM_NCCALCSIZE */)
        {
            if (wParam != IntPtr.Zero) return IntPtr.Zero;
        }
        else if (uMsg == 0x0085 /* WM_NCPAINT */)
        {
            return IntPtr.Zero;
        }
        else if (uMsg == 0x0014 /* WM_ERASEBKGND */)
        {
            if (GetClientRect(hWnd, out RECT rect))
            {
                IntPtr blackBrush = CreateSolidBrush(0);
                FillRect(wParam, ref rect, blackBrush);
                DeleteObject(blackBrush);
            }
            return (IntPtr)1;
        }
        else if (uMsg == 0x031E /* WM_DWMCOMPOSITIONCHANGED */)
        {
            ConfigureWindowTransparency(hWnd);
            return IntPtr.Zero;
        }
        else if (uMsg == 0x0024 /* WM_GETMINMAXINFO */)
        {
            DefSubclassProc(hWnd, uMsg, wParam, lParam);
            Marshal.WriteInt32(lParam, 28, 20);
            return IntPtr.Zero;
        }
        else if (uMsg == 0x0020 /* WM_SETCURSOR */)
        {
            if (_isDragging)
            {
                SetCursor(_sizeAllCursor);
                return (IntPtr)1;
            }
        }
        else if (uMsg == 0x0084 /* WM_NCHITTEST */)
        {
            if (_isDragging)
            {
                return DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }

            int screenX = unchecked((short)(long)lParam);
            int screenY = unchecked((short)((long)lParam >> 16));

            if (!IsScreenPointInInteractiveCards(screenX, screenY))
            {
                // 当鼠标落在任何非卡片区域（包括卡片上方、卡片下方、窗口多余区域等）时，
                // 立即返回 HTTRANSPARENT (-1)，操作系统直接将所有鼠标点击、悬停、滚轮完全透传给下层游戏或应用
                return (IntPtr)(-1 /* HTTRANSPARENT */);
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
