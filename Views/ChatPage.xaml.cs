using System;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VrcChatboxDemo.Services;
using Windows.System;
using Windows.UI.Core;

namespace VrcChatboxDemo.Views;

public sealed partial class ChatPage : Page
{
    private bool _hasManualNewlines = false;
    private bool _isClearingOnSend = false;
    private bool _isImeComposing = false;
    private DateTime _lastImeEndTime = DateTime.MinValue;
    private bool _isSending = false;
    private bool _isAutoFilling = false;

    public ChatPage()
    {
        InitializeComponent();
        Loaded += ChatPage_Loaded;
        Unloaded += ChatPage_Unloaded;
    }

    private void ChatPage_Loaded(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance;
        s.SpeechRecognizedUpdated += OnSpeechRecognizedUpdated;
        s.SpeechHypothesisUpdated += OnSpeechHypothesisUpdated;
        s.TranslationUpdated += OnTranslationUpdated;
        s.DisplaySettingsChanged += OnDisplaySettingsChanged;
        s.SpeechStatusUpdated += OnSpeechStatusUpdated;
        s.RequestAutoFillInput += OnRequestAutoFillInput;

        RecognizedTextBlock.Text = s.LastRecognizedText;
        TranslatedTextBlock.Text = s.LastTranslatedText;

        UpdateDisplayVisibility();
        FocusInput();
    }

    private void ChatPage_Unloaded(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance;
        s.SpeechRecognizedUpdated -= OnSpeechRecognizedUpdated;
        s.SpeechHypothesisUpdated -= OnSpeechHypothesisUpdated;
        s.TranslationUpdated -= OnTranslationUpdated;
        s.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        s.SpeechStatusUpdated -= OnSpeechStatusUpdated;
        s.RequestAutoFillInput -= OnRequestAutoFillInput;
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
        DispatcherQueue.TryEnqueue(UpdateDisplayVisibility);
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
        SpeechDisplayCard.Visibility = (showRecRow || showTransRow) ? Visibility.Visible : Visibility.Collapsed;

        UpdateLatencyDisplay(s.LastTranslationLatencyMs);
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
        if (SettingsService.Instance.IsImmersiveMode) return;
        if (!SettingsService.Instance.IsAutoFillEnabled) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // 仅当当前处于主聊天页面且可见时才处理填入，坚决不在后台或其他页面抢占或篡改
                if (!IsLoaded || Visibility != Visibility.Visible)
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
                }
                finally
                {
                    _isAutoFilling = false;
                }

                SettingsService.Instance.SuppressTypingAnimation();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatPage] AutoFill failed: {ex.Message}");
            }
        });
    }

    public void FocusInput()
    {
        MessageInputBox.Focus(FocusState.Programmatic);
        MessageInputBox.Select(MessageInputBox.Text.Length, 0);
    }

    private void MessageInputBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _isImeComposing = true;
    }

    private void MessageInputBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _isImeComposing = false;
        _lastImeEndTime = DateTime.UtcNow;

        // 输入法候选词上屏后，立即同步一次游戏内实时打字预览（汉字上屏，非拼音）
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

        // 核心防线：输入法拼音组字阶段或自动填入阶段绝对不向 VRChat 发送打字预览与打字动画
        if (!_isAutoFilling && !_isImeComposing)
        {
            SettingsService.Instance.UpdateLiveTyping(rawText);
        }
    }

    private async void MessageInputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // 1. 若当前正在进行输入法拼音选词组字，或者正是刚刚按下确认选词的瞬间（< 80ms）
            // 坚决不拦截回车，绝不提前发送或清空，将回车交给输入法以完成汉字落地
            if (_isImeComposing || (DateTime.UtcNow - _lastImeEndTime).TotalMilliseconds < 80)
            {
                return;
            }

            var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            bool isShiftDown = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

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
                    // 核心保底措施：必须等待消息切实通过 OSC 提交发送到 VRChat 并完成底层打字状态收尾
                    bool sent = await SettingsService.Instance.FinalizeSendAsync(text);

                    // 严密时序保底：只有在确保发送成功后，才执行输入框清空！
                    if (sent && SettingsService.Instance.IsClearOnSendEnabled)
                    {
                        _isClearingOnSend = true;
                        try
                        {
                            MessageInputBox.Text = string.Empty;
                            CharCountTextBlock.Text = "0 / 144 字符";
                            CharCountTextBlock.ClearValue(TextBlock.ForegroundProperty);
                            _hasManualNewlines = false;
                        }
                        finally
                        {
                            _isClearingOnSend = false;
                        }
                    }
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
}
