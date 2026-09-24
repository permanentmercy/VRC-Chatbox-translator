using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VrcChatboxDemo.Services;
using Windows.System;
using Windows.UI.Core;

namespace VrcChatboxDemo.Views;

public sealed partial class SettingsPage : Page
{
    private bool _isInitializing = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.SpeechStatusUpdated -= OnSpeechStatusUpdated;
        SettingsService.Instance.SpeechRecognitionStateChanged -= OnSpeechRecognitionStateChanged;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;

        var s = SettingsService.Instance;

        // 语音识别与 AI 翻译
        SpeechSwitch.IsOn = s.IsSpeechRecognitionEnabled;
        SpeechStatusTextBlock.Text = s.SpeechStatus;
        s.SpeechStatusUpdated += OnSpeechStatusUpdated;
        s.SpeechRecognitionStateChanged += OnSpeechRecognitionStateChanged;
        InitSpeechLanguages();
        await InitAudioDevicesAsync();
        InitSubtitleQueueCapacity(s.SubtitleQueueCapacity);

        ShowRecognizedTextSwitch.IsOn = s.ShowRecognizedText;
        TranslationSwitch.IsOn = s.IsTranslationEnabled;
        ShowTranslatedTextSwitch.IsOn = s.ShowTranslatedText;
        ShowLatencySwitch.IsOn = s.ShowTranslationLatency;
        AutoFillInputSwitch.IsOn = s.IsAutoFillEnabled;
        OllamaEndpointTextBox.Text = s.OllamaEndpoint;

        InitTargetLanguage(s.TargetLanguage);

        // 热键
        HotkeySwitch.IsOn = s.IsHotkeyEnabled;
        HotkeyTextBox.Text = s.CustomHotkeyString;

        ImmersiveHotkeySwitch.IsOn = s.IsImmersiveHotkeyEnabled;
        ImmersiveHotkeyTextBox.Text = s.CustomImmersiveHotkeyString;

        // 交互与发送
        DirectSendSwitch.IsOn = s.IsDirectSendEnabled;
        SoundSwitch.IsOn = s.IsSoundEnabled;
        TypingSwitch.IsOn = s.IsTypingEnabled;
        EnterSendSwitch.IsOn = s.IsEnterSendEnabled;
        ClearOnSendSwitch.IsOn = s.IsClearOnSendEnabled;
        LiveTypingSwitch.IsOn = s.IsLiveTypingEnabled;

        // OSC 网络
        HostTextBox.Text = s.Host;
        PortTextBox.Text = s.Port.ToString();

        // 日志
        LogsListView.ItemsSource = s.Logs;

        _isInitializing = false;

        await LoadOllamaModelsAsync();
    }

    private void InitTargetLanguage(string targetLang)
    {
        for (int i = 0; i < TargetLanguageComboBox.Items.Count; i++)
        {
            if (TargetLanguageComboBox.Items[i] is ComboBoxItem item &&
                item.Content?.ToString() == targetLang)
            {
                TargetLanguageComboBox.SelectedIndex = i;
                return;
            }
        }
        TargetLanguageComboBox.SelectedIndex = 0;
    }

    private async Task LoadOllamaModelsAsync()
    {
        string endpoint = SettingsService.Instance.OllamaEndpoint;
        var models = await SettingsService.Instance.OllamaService.GetInstalledModelsAsync(endpoint);

        OllamaModelComboBox.Items.Clear();
        if (models.Count > 0)
        {
            foreach (var m in models)
            {
                OllamaModelComboBox.Items.Add(m);
            }

            string current = SettingsService.Instance.OllamaModel;
            int foundIdx = models.FindIndex(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
            if (foundIdx >= 0)
            {
                OllamaModelComboBox.SelectedIndex = foundIdx;
            }
            else
            {
                int hyIdx = models.FindIndex(m => m.Contains("HY-MT", StringComparison.OrdinalIgnoreCase));
                OllamaModelComboBox.SelectedIndex = hyIdx >= 0 ? hyIdx : 0;
            }
        }
        else
        {
            OllamaModelComboBox.Items.Add(SettingsService.Instance.OllamaModel);
            OllamaModelComboBox.SelectedIndex = 0;
        }
    }

    private async void SpeechSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (SettingsService.Instance.IsSpeechRecognitionEnabled == SpeechSwitch.IsOn) return;
        SettingsService.Instance.IsSpeechRecognitionEnabled = SpeechSwitch.IsOn;
        await SettingsService.Instance.ApplySpeechStateAsync();
    }

    private void OnSpeechRecognitionStateChanged(bool isListening)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (SpeechSwitch.IsOn != isListening)
            {
                SpeechSwitch.IsOn = isListening;
            }
        });
    }

    private void OnSpeechStatusUpdated(string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SpeechStatusTextBlock.Text = status;
        });
    }

    private void InitSpeechLanguages()
    {
        SpeechLanguageComboBox.Items.Clear();
        var supported = SpeechService.GetSupportedLanguages();
        if (supported.Count == 0)
        {
            SpeechLanguageComboBox.Items.Add("中文 (中华人民共和国) [zh-CN]");
            SpeechLanguageComboBox.SelectedIndex = 0;
            return;
        }

        string currentTag = SettingsService.Instance.SpeechLanguageTag;
        int selectedIdx = 0;

        for (int i = 0; i < supported.Count; i++)
        {
            var lang = supported[i];
            SpeechLanguageComboBox.Items.Add($"{lang.DisplayName} [{lang.LanguageTag}]");
            if (!string.IsNullOrEmpty(currentTag) && string.Equals(lang.LanguageTag, currentTag, StringComparison.OrdinalIgnoreCase))
            {
                selectedIdx = i;
            }
        }

        SpeechLanguageComboBox.SelectedIndex = selectedIdx;
    }

    private async void SpeechLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        var supported = SpeechService.GetSupportedLanguages();
        int idx = SpeechLanguageComboBox.SelectedIndex;
        if (idx >= 0 && idx < supported.Count)
        {
            SettingsService.Instance.SpeechLanguageTag = supported[idx].LanguageTag;
            if (SettingsService.Instance.IsSpeechRecognitionEnabled)
            {
                await SettingsService.Instance.ApplySpeechStateAsync();
            }
        }
    }

    private async void TestAudioFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TestAudioFileButton.IsEnabled = false;
            SpeechStatusTextBlock.Text = "正在识别测试音频文件 (me.wav...)...";
            string text = await SettingsService.Instance.TestTranscribeFileAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                SpeechStatusTextBlock.Text = $"测试文件识别成功: {text}";
            }
            else
            {
                SpeechStatusTextBlock.Text = "测试文件识别完成 (未检测到有效内容)";
            }
        }
        catch (Exception ex)
        {
            SpeechStatusTextBlock.Text = $"测试识别异常: {ex.Message}";
        }
        finally
        {
            TestAudioFileButton.IsEnabled = true;
        }
    }

    private List<AudioDeviceInfo> _audioDevices = new();

    private async Task InitAudioDevicesAsync()
    {
        AudioDeviceComboBox.Items.Clear();
        AudioDeviceComboBox.Items.Add("系统默认设备 (自动跟随系统当前扬声器)");

        _audioDevices = await AudioDeviceService.GetAudioRenderDevicesAsync();

        string currentEndpointId = SettingsService.Instance.AudioInputDeviceId;
        int selectedIndex = 0;

        for (int i = 0; i < _audioDevices.Count; i++)
        {
            var d = _audioDevices[i];
            string itemText = d.IsDefault ? $"{d.Name} (当前系统默认)" : d.Name;
            AudioDeviceComboBox.Items.Add(itemText);

            if (!string.IsNullOrEmpty(currentEndpointId) &&
                (string.Equals(d.EndpointId, currentEndpointId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(d.Id, currentEndpointId, StringComparison.OrdinalIgnoreCase)))
            {
                selectedIndex = i + 1;
            }
        }

        AudioDeviceComboBox.SelectedIndex = selectedIndex;
    }

    private async void AudioDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        int idx = AudioDeviceComboBox.SelectedIndex;
        if (idx <= 0)
        {
            await SettingsService.Instance.SwitchAudioDeviceAsync(null, "系统默认设备");
        }
        else
        {
            int devIdx = idx - 1;
            if (devIdx >= 0 && devIdx < _audioDevices.Count)
            {
                var dev = _audioDevices[devIdx];
                await SettingsService.Instance.SwitchAudioDeviceAsync(dev.EndpointId, dev.Name);
            }
        }
    }

    private async void RefreshAudioDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        await InitAudioDevicesAsync();
        _isInitializing = false;
    }

    private void ShowRecognizedTextSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.ShowRecognizedText = ShowRecognizedTextSwitch.IsOn;
        SettingsService.Instance.NotifyDisplaySettingsChanged();
    }

    private void TranslationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsTranslationEnabled = TranslationSwitch.IsOn;
        SettingsService.Instance.NotifyDisplaySettingsChanged();
    }

    private void ShowTranslatedTextSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.ShowTranslatedText = ShowTranslatedTextSwitch.IsOn;
        SettingsService.Instance.NotifyDisplaySettingsChanged();
    }

    private void ShowLatencySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.ShowTranslationLatency = ShowLatencySwitch.IsOn;
        SettingsService.Instance.NotifyDisplaySettingsChanged();
    }

    private void AutoFillInputSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsAutoFillEnabled = AutoFillInputSwitch.IsOn;
    }

    private void InitSubtitleQueueCapacity(int capacity)
    {
        int selIdx = capacity switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            4 => 3,
            5 => 4,
            6 => 5,
            _ => 2
        };
        SubtitleQueueComboBox.SelectedIndex = selIdx;
    }

    private void SubtitleQueueComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        int capacity = SubtitleQueueComboBox.SelectedIndex switch
        {
            0 => 1,
            1 => 2,
            2 => 3,
            3 => 4,
            4 => 5,
            5 => 6,
            _ => 3
        };
        SettingsService.Instance.SubtitleQueueCapacity = capacity;
    }

    private void ClearSubtitleQueueButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.ClearSubtitleQueue();
    }

    private void OllamaEndpointTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        string endpoint = OllamaEndpointTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(endpoint))
        {
            SettingsService.Instance.OllamaEndpoint = endpoint;
        }
    }

    private void OllamaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (OllamaModelComboBox.SelectedItem is string modelStr)
        {
            SettingsService.Instance.OllamaModel = modelStr;
        }
        else if (!string.IsNullOrWhiteSpace(OllamaModelComboBox.Text))
        {
            SettingsService.Instance.OllamaModel = OllamaModelComboBox.Text.Trim();
        }
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadOllamaModelsAsync();
    }

    private void TargetLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (TargetLanguageComboBox.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            SettingsService.Instance.TargetLanguage = item.Content.ToString() ?? "英语 (English)";
        }
    }

    private void HotkeySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsHotkeyEnabled = HotkeySwitch.IsOn;
        SettingsService.Instance.ApplyHotkey();
    }

    private void ImmersiveHotkeySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsImmersiveHotkeyEnabled = ImmersiveHotkeySwitch.IsOn;
        SettingsService.Instance.ApplyHotkey();
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;

        // 如果按下 ESC，取消本次热键自定义，恢复原热键并失去焦点
        if (key == VirtualKey.Escape)
        {
            e.Handled = true;
            HotkeyTextBox.Text = SettingsService.Instance.CustomHotkeyString;
            ResetHotkeyButton.Focus(FocusState.Programmatic);
            return;
        }

        if (IsModifierOnly(key))
        {
            return;
        }

        e.Handled = true;
        ParseAndApplyKey(key, isImmersive: false);
    }

    private void ImmersiveHotkeyTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;

        // 如果按下 ESC，取消本次热键自定义，恢复原热键并失去焦点
        if (key == VirtualKey.Escape)
        {
            e.Handled = true;
            ImmersiveHotkeyTextBox.Text = SettingsService.Instance.CustomImmersiveHotkeyString;
            ResetImmersiveHotkeyButton.Focus(FocusState.Programmatic);
            return;
        }

        if (IsModifierOnly(key))
        {
            return;
        }

        e.Handled = true;
        ParseAndApplyKey(key, isImmersive: true);
    }

    private bool IsModifierOnly(VirtualKey key)
    {
        return key == VirtualKey.Control || key == VirtualKey.LeftControl || key == VirtualKey.RightControl ||
               key == VirtualKey.Shift || key == VirtualKey.LeftShift || key == VirtualKey.RightShift ||
               key == VirtualKey.Menu || key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu ||
               key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows ||
               key == VirtualKey.CapitalLock;
    }

    private void ParseAndApplyKey(VirtualKey key, bool isImmersive)
    {
        bool isCtrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        bool isShift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        bool isAlt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        bool isWin = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down ||
                     (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        uint mods = 0;
        var parts = new List<string>();

        if (isCtrl) { mods |= HotkeyService.MOD_CONTROL; parts.Add("Ctrl"); }
        if (isAlt) { mods |= HotkeyService.MOD_ALT; parts.Add("Alt"); }
        if (isShift) { mods |= HotkeyService.MOD_SHIFT; parts.Add("Shift"); }
        if (isWin) { mods |= HotkeyService.MOD_WIN; parts.Add("Win"); }

        string keyName = FormatKeyName(key);
        parts.Add(keyName);

        string fullHotkeyString = string.Join(" + ", parts);
        uint vk = (uint)key;

        var s = SettingsService.Instance;
        if (isImmersive)
        {
            s.CustomImmersiveHotkeyModifiers = mods;
            s.CustomImmersiveHotkeyKey = vk;
            s.CustomImmersiveHotkeyString = fullHotkeyString;
            s.ApplyHotkey();
            ImmersiveHotkeyTextBox.Text = fullHotkeyString;
        }
        else
        {
            s.CustomHotkeyModifiers = mods;
            s.CustomHotkeyKey = vk;
            s.CustomHotkeyString = fullHotkeyString;
            s.ApplyHotkey();
            HotkeyTextBox.Text = fullHotkeyString;
        }
    }

    private static string FormatKeyName(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.None => "None",
            VirtualKey.Back => "Backspace",
            VirtualKey.Enter => "Enter",
            VirtualKey.Space => "Space",
            (VirtualKey)192 => "~",
            _ => key.ToString()
        };
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance;
        s.CustomHotkeyModifiers = HotkeyService.MOD_CONTROL | HotkeyService.MOD_SHIFT;
        s.CustomHotkeyKey = 0x43;
        s.CustomHotkeyString = "Ctrl + Shift + C";
        s.ApplyHotkey();
        HotkeyTextBox.Text = s.CustomHotkeyString;
    }

    private void ResetImmersiveHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance;
        s.CustomImmersiveHotkeyModifiers = HotkeyService.MOD_CONTROL | HotkeyService.MOD_SHIFT;
        s.CustomImmersiveHotkeyKey = 0x5A;
        s.CustomImmersiveHotkeyString = "Ctrl + Shift + Z";
        s.ApplyHotkey();
        ImmersiveHotkeyTextBox.Text = s.CustomImmersiveHotkeyString;
    }

    private void DirectSendSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsDirectSendEnabled = DirectSendSwitch.IsOn;
    }

    private void SoundSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsSoundEnabled = SoundSwitch.IsOn;
    }

    private async void TypingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool isTyping = TypingSwitch.IsOn;
        await SettingsService.Instance.SetTypingAsync(isTyping);
    }

    private void EnterSendSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsEnterSendEnabled = EnterSendSwitch.IsOn;
    }

    private void ClearOnSendSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsClearOnSendEnabled = ClearOnSendSwitch.IsOn;
    }

    private void LiveTypingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsLiveTypingEnabled = LiveTypingSwitch.IsOn;
    }

    private void HostTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        string host = HostTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(host))
        {
            SettingsService.Instance.Host = host;
        }
    }

    private void PortTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (int.TryParse(PortTextBox.Text.Trim(), out int port) && port > 0 && port <= 65535)
        {
            SettingsService.Instance.Port = port;
        }
    }

    private async void ResetConfigButton_Click(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        SettingsService.Instance.ResetToDefault();

        var s = SettingsService.Instance;
        SpeechSwitch.IsOn = s.IsSpeechRecognitionEnabled;
        SpeechStatusTextBlock.Text = s.SpeechStatus;
        InitSpeechLanguages();
        await InitAudioDevicesAsync();
        ShowRecognizedTextSwitch.IsOn = s.ShowRecognizedText;
        TranslationSwitch.IsOn = s.IsTranslationEnabled;
        ShowTranslatedTextSwitch.IsOn = s.ShowTranslatedText;
        ShowLatencySwitch.IsOn = s.ShowTranslationLatency;
        AutoFillInputSwitch.IsOn = s.IsAutoFillEnabled;
        InitSubtitleQueueCapacity(s.SubtitleQueueCapacity);
        OllamaEndpointTextBox.Text = s.OllamaEndpoint;
        InitTargetLanguage(s.TargetLanguage);

        HotkeySwitch.IsOn = s.IsHotkeyEnabled;
        HotkeyTextBox.Text = s.CustomHotkeyString;
        ImmersiveHotkeySwitch.IsOn = s.IsImmersiveHotkeyEnabled;
        ImmersiveHotkeyTextBox.Text = s.CustomImmersiveHotkeyString;

        HostTextBox.Text = s.Host;
        PortTextBox.Text = s.Port.ToString();
        DirectSendSwitch.IsOn = s.IsDirectSendEnabled;
        SoundSwitch.IsOn = s.IsSoundEnabled;
        TypingSwitch.IsOn = s.IsTypingEnabled;
        EnterSendSwitch.IsOn = s.IsEnterSendEnabled;
        ClearOnSendSwitch.IsOn = s.IsClearOnSendEnabled;
        LiveTypingSwitch.IsOn = s.IsLiveTypingEnabled;
        _isInitializing = false;
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Logs.Clear();
    }
}
