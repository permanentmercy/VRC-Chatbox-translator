using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views.Sections;

public sealed partial class SpeechSection : UserControl
{
    private bool _isInitializing = true;
    private List<AudioDeviceInfo> _audioDevices = new();

    public SpeechSection()
    {
        InitializeComponent();
        Loaded += SpeechSection_Loaded;
        Unloaded += SpeechSection_Unloaded;
    }

    private void SpeechSection_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.SpeechStatusUpdated -= OnSpeechStatusUpdated;
        SettingsService.Instance.SpeechRecognitionStateChanged -= OnSpeechRecognitionStateChanged;
    }

    private async void SpeechSection_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        SpeechSwitch.IsOn = s.IsSpeechRecognitionEnabled;
        SpeechStatusTextBlock.Text = s.SpeechStatus;
        s.SpeechStatusUpdated += OnSpeechStatusUpdated;
        s.SpeechRecognitionStateChanged += OnSpeechRecognitionStateChanged;

        InitSpeechEngine(s.SpeechEngine);
        InitLiveCaptionsLanguages(s.LiveCaptionsLanguageCode);
        InitSpeechModel(s.SpeechModelType);
        InitSpeechLanguages();
        InitChineseVariant(s.ChineseVariant);
        InitGpuMode(s.GpuUsageMode);
        InitSpeechSliceDuration(s.SpeechSliceDurationSeconds);
        await InitAudioDevicesAsync();
        InitSubtitleQueueCapacity(s.SubtitleQueueCapacity);

        _isInitializing = false;
    }

    private void OnSpeechStatusUpdated(string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SpeechStatusTextBlock.Text = status;
        });
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

    private async void SpeechSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        if (SettingsService.Instance.IsSpeechRecognitionEnabled == SpeechSwitch.IsOn) return;
        SettingsService.Instance.IsSpeechRecognitionEnabled = SpeechSwitch.IsOn;
        await SettingsService.Instance.ApplySpeechStateAsync();
    }

    private void InitSpeechEngine(string? engine)
    {
        string target = string.IsNullOrWhiteSpace(engine) ? "Whisper" : engine;
        int selIdx = 0;
        for (int i = 0; i < SpeechEngineComboBox.Items.Count; i++)
        {
            if (SpeechEngineComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                selIdx = i;
                break;
            }
        }
        SpeechEngineComboBox.SelectedIndex = selIdx;
        UpdateEngineVisibility(target);
    }

    private void SpeechEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (SpeechEngineComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            SettingsService.Instance.SpeechEngine = tag;
            UpdateEngineVisibility(tag);
        }
    }

    private void UpdateEngineVisibility(string engine)
    {
        bool isLiveCaptions = string.Equals(engine, "LiveCaptions", StringComparison.OrdinalIgnoreCase);
        WhisperOptionsGrid.Visibility = isLiveCaptions ? Visibility.Collapsed : Visibility.Visible;
        AudioDeviceCard.Visibility = isLiveCaptions ? Visibility.Collapsed : Visibility.Visible;
        LiveCaptionsPanel.Visibility = isLiveCaptions ? Visibility.Visible : Visibility.Collapsed;
        LiveCaptionsHideNativeCheckBox.IsChecked = SettingsService.Instance.LiveCaptionsHideNativeWindow;

        if (!isLiveCaptions)
        {
            bool hasCuda = SpeechService.IsCudaRuntimeAvailable(out _);
            CudaWarningInfoBar.IsOpen = !hasCuda;
        }
        else
        {
            CudaWarningInfoBar.IsOpen = false;
        }
    }

    private void ToggleLiveCaptionsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.LiveCaptionsService.ToggleNativeWindow();
    }

    private void LiveCaptionsHideNativeCheckBox_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool isHide = LiveCaptionsHideNativeCheckBox.IsChecked == true;
        SettingsService.Instance.LiveCaptionsHideNativeWindow = isHide;
        if (isHide)
        {
            SettingsService.Instance.LiveCaptionsService.HideNativeWindow();
        }
        else
        {
            SettingsService.Instance.LiveCaptionsService.RestoreNativeWindow();
        }
    }

    private void InitLiveCaptionsLanguages(string? currentCode)
    {
        LiveCaptionsLanguageComboBox.Items.Clear();
        var supported = LiveCaptionsService.SupportedLanguages;
        int selectedIdx = 0;
        string target = string.IsNullOrWhiteSpace(currentCode) ? "zh-CN" : currentCode;

        for (int i = 0; i < supported.Count; i++)
        {
            var lang = supported[i];
            var item = new ComboBoxItem
            {
                Content = $"{lang.DisplayName} [{lang.Code}]",
                Tag = lang.Code
            };
            LiveCaptionsLanguageComboBox.Items.Add(item);

            if (string.Equals(lang.Code, target, StringComparison.OrdinalIgnoreCase))
            {
                selectedIdx = i;
            }
        }

        LiveCaptionsLanguageComboBox.SelectedIndex = selectedIdx;
    }

    private void LiveCaptionsLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (LiveCaptionsLanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
        {
            SettingsService.Instance.LiveCaptionsLanguageCode = code;
        }
    }

    private void InitSpeechModel(string? modelType)
    {
        string target = string.IsNullOrWhiteSpace(modelType) ? "tiny" : modelType.ToLowerInvariant();
        int selIdx = 0;
        for (int i = 0; i < SpeechModelComboBox.Items.Count; i++)
        {
            if (SpeechModelComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                selIdx = i;
                break;
            }
        }
        SpeechModelComboBox.SelectedIndex = selIdx;
    }

    private void SpeechModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (SpeechModelComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            SettingsService.Instance.SpeechModelType = tag;
        }
    }

    private void InitSpeechLanguages()
    {
        SpeechLanguageComboBox.Items.Clear();
        var supported = SpeechService.GetSupportedLanguages();
        if (supported.Count == 0)
        {
            SpeechLanguageComboBox.Items.Add(new ComboBoxItem { Content = "中文 (中华人民共和国) [zh-CN]", Tag = "zh" });
            SpeechLanguageComboBox.SelectedIndex = 0;
            return;
        }

        string currentTag = SettingsService.Instance.SpeechLanguageTag;
        int selectedIdx = 0;

        for (int i = 0; i < supported.Count; i++)
        {
            var lang = supported[i];
            var item = new ComboBoxItem
            {
                Content = $"{lang.DisplayName} [{lang.LanguageTag}]",
                Tag = lang.LanguageTag
            };
            SpeechLanguageComboBox.Items.Add(item);

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
            await SettingsService.Instance.SwitchSpeechLanguageAsync(supported[idx].LanguageTag);
        }
    }

    private void InitChineseVariant(string variant)
    {
        int selIdx = variant switch
        {
            "Traditional" => 1,
            "Original" => 2,
            _ => 0
        };
        ChineseVariantComboBox.SelectedIndex = selIdx;
    }

    private void ChineseVariantComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        string variant = ChineseVariantComboBox.SelectedIndex switch
        {
            1 => "Traditional",
            2 => "Original",
            _ => "Simplified"
        };
        SettingsService.Instance.ChineseVariant = variant;
    }

    private void InitGpuMode(string mode)
    {
        int selIdx = mode?.ToLowerInvariant() switch
        {
            "medium" => 1,
            "low" => 2,
            _ => 0
        };
        GpuModeComboBox.SelectedIndex = selIdx;
    }

    private void GpuModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        string mode = GpuModeComboBox.SelectedIndex switch
        {
            1 => "Medium",
            2 => "Low",
            _ => "High"
        };
        SettingsService.Instance.GpuUsageMode = mode;
    }

    private void InitSpeechSliceDuration(int durationSecs)
    {
        int selIdx = durationSecs switch
        {
            3 => 0,
            4 => 1,
            5 => 2,
            6 => 3,
            8 => 4,
            10 => 5,
            12 => 6,
            15 => 7,
            _ => 3
        };
        SpeechSliceDurationComboBox.SelectedIndex = selIdx;
    }

    private void SpeechSliceDurationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        int secs = SpeechSliceDurationComboBox.SelectedIndex switch
        {
            0 => 3,
            1 => 4,
            2 => 5,
            3 => 6,
            4 => 8,
            5 => 10,
            6 => 12,
            7 => 15,
            _ => 6
        };
        SettingsService.Instance.SpeechSliceDurationSeconds = secs;
    }

    private async Task InitAudioDevicesAsync()
    {
        AudioDeviceComboBox.Items.Clear();
        AudioDeviceComboBox.Items.Add(new ComboBoxItem { Content = "系统默认设备", Tag = string.Empty });

        _audioDevices = await AudioDeviceService.GetAudioRenderDevicesAsync();

        string currentEndpointId = SettingsService.Instance.AudioInputDeviceId;
        int selectedIndex = 0;

        for (int i = 0; i < _audioDevices.Count; i++)
        {
            var d = _audioDevices[i];
            string itemText = d.IsDefault ? $"{d.Name} (当前系统默认)" : d.Name;
            var item = new ComboBoxItem
            {
                Content = itemText,
                Tag = d.EndpointId
            };
            AudioDeviceComboBox.Items.Add(item);

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
}
