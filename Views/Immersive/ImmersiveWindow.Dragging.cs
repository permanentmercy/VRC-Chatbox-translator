using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace VrcChatboxDemo.Views;

public sealed partial class ImmersiveWindow : Window
{
    private bool _isDragging = false;
    private POINT _dragStartCursor;
    private int _dragStartWindowX;
    private int _dragStartWindowY;
    private UIElement? _capturedElement;
    private bool _hasCustomPosition = false;

    private void StartDragging(UIElement sender, PointerRoutedEventArgs e)
    {
        _isDragging = true;
        _hasCustomPosition = true;
        _capturedElement = sender;
        GetCursorPos(out _dragStartCursor);
        GetWindowRect(_hWnd, out RECT rect);
        _dragStartWindowX = rect.Left;
        _dragStartWindowY = rect.Top;
        try
        {
            sender.CapturePointer(e.Pointer);
        }
        catch { }
        e.Handled = true;
    }

    private void UpdateDragging(PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        GetCursorPos(out POINT cur);
        int dx = cur.X - _dragStartCursor.X;
        int dy = cur.Y - _dragStartCursor.Y;
        SetWindowPos(_hWnd, IntPtr.Zero, _dragStartWindowX + dx, _dragStartWindowY + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void StopDragging(PointerRoutedEventArgs? e = null)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (_capturedElement != null && e != null)
        {
            try
            {
                _capturedElement.ReleasePointerCapture(e.Pointer);
            }
            catch { }
        }
        _capturedElement = null;

        UpdateHitBounds();

        if (e != null)
        {
            e.Handled = true;
        }
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint((UIElement)sender);
        if (pt.Properties.IsLeftButtonPressed)
        {
            StartDragging((UIElement)sender, e);
        }
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateDragging(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopDragging(e);
    }

    private void DragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        StopDragging(e);
    }

    private void Global_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var altState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        bool isAltDown = (altState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        var pt = e.GetCurrentPoint(OverlayRootGrid);
        if (isAltDown || pt.Properties.IsMiddleButtonPressed)
        {
            StartDragging(OverlayRootGrid, e);
        }
    }

    private void Global_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _capturedElement == OverlayRootGrid)
        {
            UpdateDragging(e);
        }
    }

    private void Global_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _capturedElement == OverlayRootGrid)
        {
            StopDragging(e);
        }
    }

    private void Global_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _capturedElement == OverlayRootGrid)
        {
            StopDragging(e);
        }
    }
}
