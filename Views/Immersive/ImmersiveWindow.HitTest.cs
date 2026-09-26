using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views;

public sealed partial class ImmersiveWindow : Window
{
    private Windows.Foundation.Rect _inputCardBounds;
    private Windows.Foundation.Rect _speechCardBounds;

    private void UpdateHitBounds()
    {
        try
        {
            if (_hWnd == IntPtr.Zero) return;

            uint dpi = GetDpiForWindow(_hWnd);
            float scale = (dpi > 0) ? dpi / 96f : 1f;

            IntPtr inputRgn = IntPtr.Zero;
            IntPtr speechRgn = IntPtr.Zero;

            if (InputCard != null && InputCard.ActualHeight > 0)
            {
                var t = InputCard.TransformToVisual(this.Content);
                var pt = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                _inputCardBounds = new Windows.Foundation.Rect(pt.X, pt.Y, InputCard.ActualWidth, InputCard.ActualHeight);

                int x1 = (int)Math.Floor(_inputCardBounds.X * scale) - 2;
                int y1 = (int)Math.Floor(_inputCardBounds.Y * scale) - 2;
                int x2 = (int)Math.Ceiling((_inputCardBounds.X + _inputCardBounds.Width) * scale) + 2;
                int y2 = (int)Math.Ceiling((_inputCardBounds.Y + _inputCardBounds.Height) * scale) + 2;
                int radius = (int)Math.Round(10 * scale);

                // 预留卡片下方输入法候选窗口 (IME Candidate Window) 显示空间，防止中文候选框被 SetWindowRgn 强制裁切屏蔽
                int imeCandidateHeight = (int)Math.Ceiling(140 * scale);
                IntPtr cardRgn = CreateRoundRectRgn(x1, y1, x2, y2, radius * 2, radius * 2);
                IntPtr imeRgn = CreateRectRgn(x1, y2 - 2, x2, y2 + imeCandidateHeight);
                inputRgn = CreateRectRgn(0, 0, 0, 0);
                CombineRgn(inputRgn, cardRgn, imeRgn, RGN_OR);
                DeleteObject(cardRgn);
                DeleteObject(imeRgn);
            }
            else
            {
                _inputCardBounds = Windows.Foundation.Rect.Empty;
            }

            if (SpeechDisplayCard != null && SpeechDisplayCard.Visibility == Visibility.Visible && SpeechDisplayCard.ActualHeight > 0)
            {
                var t = SpeechDisplayCard.TransformToVisual(this.Content);
                var pt = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                _speechCardBounds = new Windows.Foundation.Rect(pt.X, pt.Y, SpeechDisplayCard.ActualWidth, SpeechDisplayCard.ActualHeight);

                int sx1 = (int)Math.Floor(_speechCardBounds.X * scale) - 2;
                int sy1 = (int)Math.Floor(_speechCardBounds.Y * scale) - 2;
                int sx2 = (int)Math.Ceiling((_speechCardBounds.X + _speechCardBounds.Width) * scale) + 2;
                int sy2 = (int)Math.Ceiling((_speechCardBounds.Y + _speechCardBounds.Height) * scale) + 2;
                int sradius = (int)Math.Round(10 * scale);
                speechRgn = CreateRoundRectRgn(sx1, sy1, sx2, sy2, sradius * 2, sradius * 2);
            }
            else
            {
                _speechCardBounds = Windows.Foundation.Rect.Empty;
            }

            IntPtr finalRgn = IntPtr.Zero;
            if (inputRgn != IntPtr.Zero && speechRgn != IntPtr.Zero)
            {
                finalRgn = CreateRectRgn(0, 0, 0, 0);
                CombineRgn(finalRgn, inputRgn, speechRgn, RGN_OR);
                DeleteObject(inputRgn);
                DeleteObject(speechRgn);
            }
            else if (inputRgn != IntPtr.Zero)
            {
                finalRgn = inputRgn;
            }
            else if (speechRgn != IntPtr.Zero)
            {
                finalRgn = speechRgn;
            }

            if (finalRgn != IntPtr.Zero)
            {
                SetWindowRgn(_hWnd, finalRgn, true);
            }
        }
        catch { }
    }

    private void EnsureWindowPosition()
    {
        if (_hasCustomPosition) return;

        uint dpi = GetDpiForWindow(_hWnd);
        float scale = (dpi > 0) ? dpi / 96f : 1f;
        int pixelW = (int)Math.Ceiling(WindowWidthDip * scale);
        int pixelH = (int)Math.Ceiling(WindowHeightDip * scale);

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            int screenW = displayArea.WorkArea.Width;
            int screenH = displayArea.WorkArea.Height;
            int startX = displayArea.WorkArea.X + (screenW - pixelW) / 2;
            int targetScreenY = displayArea.WorkArea.Y + (int)(screenH * 0.72) - (int)Math.Ceiling((WindowHeightDip - 180) * scale);
            SetWindowPos(_hWnd, IntPtr.Zero, startX, targetScreenY, pixelW, pixelH, SWP_NOZORDER | SWP_NOACTIVATE);
            _hasCustomPosition = true;
        }
    }

    public void UpdateImmersiveLayout()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!AppWindow.IsVisible && !SettingsService.Instance.IsImmersiveMode)
            {
                return;
            }
            EnsureWindowPosition();
            UpdateHitBounds();
        });
    }
}
