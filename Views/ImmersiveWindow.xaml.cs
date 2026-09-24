using System;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VrcChatboxDemo.Services;
using Windows.System;
using Windows.UI.Core;

namespace VrcChatboxDemo.Views;

public sealed partial class ImmersiveWindow : Window
{
    private const int WindowWidthDip = 620;
    private const int WindowHeightDip = 640;

    private readonly IntPtr _hWnd;
    private readonly SubclassProc _subclassProc;
    private bool _hasManualNewlines = false;
    private bool _isClearingOnSend = false;
    private bool _isImeComposing = false;
    private DateTime _lastImeEndTime = DateTime.MinValue;
    private bool _isSending = false;
    private bool _isAutoFilling = false;

    public ImmersiveWindow()
    {
        InitializeComponent();

        _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new TransparentBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        _subclassProc = ImmersiveSubclassProc;
        SetWindowSubclass(_hWnd, _subclassProc, (UIntPtr)101, IntPtr.Zero);

        ConfigureWindowTransparency(_hWnd);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 设置可拖动元素的移动光标
        RightDragHandle.PointerEntered += (s, e) => SetCursor(_sizeAllCursor);
        SpeechDisplayCard.PointerEntered += (s, e) => SetCursor(_sizeAllCursor);

        // 绑定拖拽事件监听
        RightDragHandle.PointerPressed += DragHandle_PointerPressed;
        RightDragHandle.PointerMoved += DragHandle_PointerMoved;
        RightDragHandle.PointerReleased += DragHandle_PointerReleased;
        RightDragHandle.PointerCaptureLost += DragHandle_PointerCaptureLost;

        SpeechDisplayCard.PointerPressed += DragHandle_PointerPressed;
        SpeechDisplayCard.PointerMoved += DragHandle_PointerMoved;
        SpeechDisplayCard.PointerReleased += DragHandle_PointerReleased;
        SpeechDisplayCard.PointerCaptureLost += DragHandle_PointerCaptureLost;

        // 全局备选拖拽支持：支持按住 Alt 键左键拖拽，或中键直接拖拽
        OverlayRootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Global_PointerPressed), handledEventsToo: true);
        OverlayRootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Global_PointerMoved), handledEventsToo: true);
        OverlayRootGrid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Global_PointerReleased), handledEventsToo: true);
        OverlayRootGrid.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Global_PointerCaptureLost), handledEventsToo: true);

        // 布局变动时实时重新计算实际可见组件的命中边界
        OverlayRootGrid.LayoutUpdated += (s, e) => UpdateHitBounds();

        var s = SettingsService.Instance;
        s.SpeechRecognizedUpdated += OnSpeechRecognizedUpdated;
        s.SpeechHypothesisUpdated += OnSpeechHypothesisUpdated;
        s.TranslationUpdated += OnTranslationUpdated;
        s.DisplaySettingsChanged += OnDisplaySettingsChanged;
        s.SpeechStatusUpdated += OnSpeechStatusUpdated;
        s.RequestAutoFillInput += OnRequestAutoFillInput;

        EnsureWindowPosition();
        UpdateDisplayVisibility();
        UpdateImmersiveLayout();

        Closed += ImmersiveWindow_Closed;
    }

    private void ImmersiveWindow_Closed(object sender, WindowEventArgs args)
    {
        var s = SettingsService.Instance;
        s.SpeechRecognizedUpdated -= OnSpeechRecognizedUpdated;
        s.SpeechHypothesisUpdated -= OnSpeechHypothesisUpdated;
        s.TranslationUpdated -= OnTranslationUpdated;
        s.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        s.SpeechStatusUpdated -= OnSpeechStatusUpdated;
        s.RequestAutoFillInput -= OnRequestAutoFillInput;

        RemoveWindowSubclass(_hWnd, _subclassProc, (UIntPtr)101);
    }

    private void OnSpeechStatusUpdated(string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var s = SettingsService.Instance;
            if (s.IsSpeechRecognitionEnabled && string.IsNullOrWhiteSpace(s.LastRecognizedText))
            {
                UpdateDisplayVisibility();
            }
        });
    }

    private void OnDisplaySettingsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateDisplayVisibility();
        });
    }

    private void UpdateDisplayVisibility()
    {
        var s = SettingsService.Instance;
        bool speechOn = s.IsSpeechRecognitionEnabled;
        bool showRec = s.ShowRecognizedText;
        bool transOn = s.IsTranslationEnabled;
        bool showTrans = s.ShowTranslatedText;

        bool hasRealRecText = !string.IsNullOrWhiteSpace(s.LastRecognizedText);
        bool hasRealTransText = !string.IsNullOrWhiteSpace(s.LastTranslatedText);

        if (hasRealRecText)
        {
            RecognizedTextBlock.Text = s.LastRecognizedText;
            RecognizedTextBlock.Opacity = 1.0;
        }
        else
        {
            RecognizedTextBlock.Text = string.Empty;
            RecognizedTextBlock.Opacity = 1.0;
        }

        if (hasRealTransText && transOn && showTrans)
        {
            TranslatedTextBlock.Text = s.LastTranslatedText;
            TranslatedRow.Visibility = Visibility.Visible;
        }
        else
        {
            TranslatedTextBlock.Text = string.Empty;
            TranslatedRow.Visibility = Visibility.Collapsed;
        }

        bool showRecRow = showRec && speechOn && hasRealRecText;
        bool showTransRow = transOn && showTrans && hasRealTransText;

        RecognizedRow.Visibility = showRecRow ? Visibility.Visible : Visibility.Collapsed;
        TranslatedRow.Visibility = showTransRow ? Visibility.Visible : Visibility.Collapsed;
        SpeechDisplayCard.Visibility = (showRecRow || showTransRow) ? Visibility.Visible : Visibility.Collapsed;
        SpeechDisplayCard.Height = double.NaN;

        UpdateLatencyDisplay(s.LastTranslationLatencyMs);
        UpdateHitBounds();
    }

    private void UpdateLatencyDisplay(long latencyMs)
    {
        var s = SettingsService.Instance;
        if (s.ShowTranslationLatency && latencyMs > 0 && !string.IsNullOrWhiteSpace(TranslatedTextBlock.Text))
        {
            TranslationLatencyTextBlock.Text = $"({latencyMs}ms)";
            TranslationLatencyTextBlock.Visibility = Visibility.Visible;
        }
        else
        {
            TranslationLatencyTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSpeechHypothesisUpdated(string interimText)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var s = SettingsService.Instance;
            if (s.ShowRecognizedText)
            {
                RecognizedTextBlock.Text = interimText;
                UpdateDisplayVisibility();
            }
        });
    }

    private void OnSpeechRecognizedUpdated(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RecognizedTextBlock.Text = text;
            UpdateDisplayVisibility();
        });
    }

    private void OnTranslationUpdated(string translatedText, long latencyMs)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TranslatedTextBlock.Text = translatedText;
            UpdateDisplayVisibility();
            UpdateLatencyDisplay(latencyMs);
        });
    }

    private void OnRequestAutoFillInput(string text)
    {
        if (!SettingsService.Instance.IsImmersiveMode)
        {
            return;
        }
        if (!SettingsService.Instance.IsAutoFillEnabled)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!AppWindow.IsVisible)
                {
                    return;
                }

                // 若用户已在输入框内打字且拥有焦点，绝不强制覆盖用户尚未发送的草稿
                if (!string.IsNullOrWhiteSpace(MessageInputBox.Text) && MessageInputBox.FocusState != FocusState.Unfocused)
                {
                    return;
                }

                _isAutoFilling = true;
                try
                {
                    MessageInputBox.Text = text;
                    MessageInputBox.Select(MessageInputBox.Text.Length, 0);
                    UpdateHitBounds();
                }
                finally
                {
                    _isAutoFilling = false;
                }

                SettingsService.Instance.SuppressTypingAnimation();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ImmersiveWindow] AutoFill failed: {ex.Message}");
            }
        });
    }

    public void FocusInput()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!SettingsService.Instance.IsImmersiveMode || !AppWindow.IsVisible)
            {
                return;
            }

            SettingsService.Instance.HotkeyService.ActivateWindow(_hWnd);
            MessageInputBox.Focus(FocusState.Programmatic);
            MessageInputBox.Select(MessageInputBox.Text.Length, 0);
        });
    }

    private void MessageInputBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _isImeComposing = true;
    }

    private void MessageInputBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _isImeComposing = false;
        _lastImeEndTime = DateTime.UtcNow;

        if (!_isClearingOnSend && !_isAutoFilling)
        {
            SettingsService.Instance.UpdateLiveTyping(sender.Text);
        }
    }

    private void MessageInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isClearingOnSend) return;

        string rawText = MessageInputBox.Text;
        int len = rawText.Length;
        CharCountTextBlock.Text = $"{len} / 144 字符";

        if (len == 0)
        {
            _hasManualNewlines = false;
        }

        if (len > 144)
        {
            CharCountTextBlock.Foreground = new SolidColorBrush(Colors.Red);
        }
        else
        {
            CharCountTextBlock.ClearValue(TextBlock.ForegroundProperty);
        }

        if (!_isAutoFilling && !_isImeComposing)
        {
            SettingsService.Instance.UpdateLiveTyping(rawText);
        }

        UpdateHitBounds();
    }

    private async void MessageInputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SettingsService.Instance.SetImmersiveMode(false);
            return;
        }

        var s = SettingsService.Instance;
        if (s.IsImmersiveHotkeyEnabled && e.Key == (VirtualKey)s.CustomImmersiveHotkeyKey)
        {
            bool isCtrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isShift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isAlt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isWin = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down ||
                         (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            uint currentMods = 0;
            if (isCtrl) currentMods |= HotkeyService.MOD_CONTROL;
            if (isAlt) currentMods |= HotkeyService.MOD_ALT;
            if (isShift) currentMods |= HotkeyService.MOD_SHIFT;
            if (isWin) currentMods |= HotkeyService.MOD_WIN;

            if (currentMods == s.CustomImmersiveHotkeyModifiers)
            {
                e.Handled = true;
                s.SetImmersiveMode(false);
                return;
            }
        }

        if (e.Key == VirtualKey.Enter)
        {
            if (_isImeComposing || (DateTime.UtcNow - _lastImeEndTime).TotalMilliseconds < 80)
            {
                return;
            }

            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            var menuState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
            bool isShiftDown = ((shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down) ||
                               ((menuState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down);

            if (isShiftDown)
            {
                _hasManualNewlines = true;
            }
            else if (SettingsService.Instance.IsEnterSendEnabled)
            {
                e.Handled = true;

                if (_isSending) return;

                string text = MessageInputBox.Text.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                if (!_hasManualNewlines)
                {
                    text = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
                }

                _isSending = true;
                try
                {
                    bool sent = await SettingsService.Instance.FinalizeSendAsync(text);

                    if (sent && SettingsService.Instance.IsClearOnSendEnabled)
                    {
                        _isClearingOnSend = true;
                        try
                        {
                            MessageInputBox.Text = string.Empty;
                            CharCountTextBlock.Text = "0 / 144 字符";
                            CharCountTextBlock.ClearValue(TextBlock.ForegroundProperty);
                            MeasureTextBlock.Text = "A";
                            _hasManualNewlines = false;
                        }
                        finally
                        {
                            _isClearingOnSend = false;
                        }
                    }

                    UpdateHitBounds();
                }
                finally
                {
                    _isSending = false;
                }
            }
            else
            {
                _hasManualNewlines = true;
            }
        }
    }

    private void OverlayRootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SettingsService.Instance.SetImmersiveMode(false);
            return;
        }

        var s = SettingsService.Instance;
        if (s.IsImmersiveHotkeyEnabled && e.Key == (VirtualKey)s.CustomImmersiveHotkeyKey)
        {
            bool isCtrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isShift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isAlt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            bool isWin = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down ||
                         (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            uint currentMods = 0;
            if (isCtrl) currentMods |= HotkeyService.MOD_CONTROL;
            if (isAlt) currentMods |= HotkeyService.MOD_ALT;
            if (isShift) currentMods |= HotkeyService.MOD_SHIFT;
            if (isWin) currentMods |= HotkeyService.MOD_WIN;

            if (currentMods == s.CustomImmersiveHotkeyModifiers)
            {
                e.Handled = true;
                s.SetImmersiveMode(false);
            }
        }
    }

    private void ExitImmersiveButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.SetImmersiveMode(false);
    }
}
