using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VrcChatboxDemo.Services;
using VrcChatboxDemo.Services.Tts;

namespace VrcChatboxDemo.Views.Sections;

public sealed partial class TtsSection : UserControl
{
    private bool _isInitializing = true;
    private List<TtsDeviceInfo> _devices = new();

    public TtsSection()
    {
        InitializeComponent();
        Loaded += TtsSection_Loaded;
        Unloaded += TtsSection_Unloaded;
    }

    private async void TtsSection_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        TtsEnableSwitch.IsOn = s.IsTtsEnabled;
        ServerEndpointTextBox.Text = s.TtsServerEndpoint;
        ModelNameTextBox.Text = s.TtsModelName;
        AutoStartServerCheckBox.IsChecked = s.AutoStartTtsServer;

        MicVolumeSlider.Value = Math.Round(s.TtsOutputVolume * 100);
        MonitorToggleSwitch.IsOn = s.IsTtsMonitorEnabled;
        MonitorVolumeSlider.Value = Math.Round(s.TtsMonitorVolume * 100);

        AutoReadSentMessageSwitch.IsOn = s.IsTtsAutoReadSentMessage;
        AutoReadTranslationSwitch.IsOn = s.IsTtsAutoReadTranslation;

        TtsService.Instance.StateChanged += OnTtsServiceStateChanged;

        LoadAudioDevices();

        _isInitializing = false;

        await RefreshEngineHealthAsync();
    }

    private void TtsSection_Unloaded(object sender, RoutedEventArgs e)
    {
        TtsService.Instance.StateChanged -= OnTtsServiceStateChanged;
    }

    private void OnTtsServiceStateChanged(TtsServerState state, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStatusIndicator(state, message);
        });
    }

    private async Task RefreshEngineHealthAsync()
    {
        var health = await TtsService.Instance.CheckHealthAsync();
        if (health != null && health.Ready)
        {
            UpdateStatusIndicator(TtsServerState.Ready, $"服务就绪！(模型: {health.Model}, 显存模式: Low VRAM)");
        }
        else if (health != null && !health.Ready)
        {
            UpdateStatusIndicator(TtsServerState.Error, $"服务启动失败: {health.Error ?? "未知错误"}");
        }
        else
        {
            UpdateStatusIndicator(TtsServerState.Stopped, "TTS 引擎已停止 (点击右侧启动引擎)");
        }
    }

    private void UpdateStatusIndicator(TtsServerState state, string message)
    {
        ServerStatusTextBlock.Text = message;

        switch (state)
        {
            case TtsServerState.Ready:
                StatusDot.Background = new SolidColorBrush(Colors.LimeGreen);
                StartServerButton.IsEnabled = false;
                StopServerButton.IsEnabled = true;
                SpeakToMicButton.IsEnabled = true;
                ListenLocalButton.IsEnabled = true;
                break;
            case TtsServerState.Starting:
                StatusDot.Background = new SolidColorBrush(Colors.Orange);
                StartServerButton.IsEnabled = false;
                StopServerButton.IsEnabled = true;
                SpeakToMicButton.IsEnabled = false;
                ListenLocalButton.IsEnabled = false;
                break;
            case TtsServerState.Synthesizing:
                StatusDot.Background = new SolidColorBrush(Colors.DeepSkyBlue);
                break;
            case TtsServerState.Error:
                StatusDot.Background = new SolidColorBrush(Colors.Red);
                StartServerButton.IsEnabled = true;
                StopServerButton.IsEnabled = false;
                break;
            case TtsServerState.Stopped:
            default:
                StatusDot.Background = new SolidColorBrush(Colors.Gray);
                StartServerButton.IsEnabled = true;
                StopServerButton.IsEnabled = false;
                break;
        }
    }

    private void LoadAudioDevices()
    {
        _devices = TtsService.Instance.GetPlaybackDevices();

        VirtualMicComboBox.Items.Clear();
        MonitorDeviceComboBox.Items.Clear();

        if (_devices.Count == 0)
        {
            VirtualMicComboBox.Items.Add(new ComboBoxItem { Content = "未检测到播放设备", Tag = "" });
            MonitorDeviceComboBox.Items.Add(new ComboBoxItem { Content = "未检测到播放设备", Tag = "" });
            VirtualMicComboBox.SelectedIndex = 0;
            MonitorDeviceComboBox.SelectedIndex = 0;
            return;
        }

        string savedMicId = SettingsService.Instance.TtsVirtualMicDeviceId;
        string savedMonitorId = SettingsService.Instance.TtsMonitorDeviceId;

        int selectedMicIdx = -1;
        int selectedMonitorIdx = -1;

        for (int i = 0; i < _devices.Count; i++)
        {
            var d = _devices[i];
            var item1 = new ComboBoxItem { Content = d.Name, Tag = d.Id };
            var item2 = new ComboBoxItem { Content = d.Name, Tag = d.Id };

            VirtualMicComboBox.Items.Add(item1);
            MonitorDeviceComboBox.Items.Add(item2);

            if (selectedMicIdx < 0 && !string.IsNullOrEmpty(savedMicId) && string.Equals(d.Id, savedMicId, StringComparison.OrdinalIgnoreCase))
            {
                selectedMicIdx = i;
            }
            if (selectedMonitorIdx < 0 && !string.IsNullOrEmpty(savedMonitorId) && string.Equals(d.Id, savedMonitorId, StringComparison.OrdinalIgnoreCase))
            {
                selectedMonitorIdx = i;
            }
        }

        // 虚拟麦克风必须优先绑定虚拟声卡 (如 CABLE Input)，坚决避免将推流目标误指向物理扬声器
        int virtualRecIdx = _devices.FindIndex(d => d.IsVirtualRecommendation);
        if (virtualRecIdx >= 0)
        {
            // 如果此前未保存或此前保存的不是虚拟声卡，纠正为虚拟声卡
            if (selectedMicIdx < 0 || !_devices[selectedMicIdx].IsVirtualRecommendation)
            {
                selectedMicIdx = virtualRecIdx;
            }
        }
        else if (selectedMicIdx < 0)
        {
            selectedMicIdx = 0;
        }

        // 耳返设备默认选中系统默认播放设备（通常为物理耳机/扬声器）
        if (selectedMonitorIdx < 0)
        {
            selectedMonitorIdx = _devices.FindIndex(d => d.IsDefault);
            if (selectedMonitorIdx < 0) selectedMonitorIdx = 0;
        }

        VirtualMicComboBox.SelectedIndex = Math.Clamp(selectedMicIdx, 0, _devices.Count - 1);
        MonitorDeviceComboBox.SelectedIndex = Math.Clamp(selectedMonitorIdx, 0, _devices.Count - 1);

        // 自动提交当前选中的设备 ID
        if (VirtualMicComboBox.SelectedItem is ComboBoxItem micItem && micItem.Tag is string mId)
        {
            SettingsService.Instance.TtsVirtualMicDeviceId = mId;
        }
        if (MonitorDeviceComboBox.SelectedItem is ComboBoxItem monItem && monItem.Tag is string monId)
        {
            SettingsService.Instance.TtsMonitorDeviceId = monId;
        }

        // 动态展示声卡状态引导
        bool hasVirtual = _devices.Any(d => d.IsVirtualRecommendation);
        if (hasVirtual)
        {
            VirtualCardInfoBar.Severity = InfoBarSeverity.Success;
            VirtualCardInfoBar.Title = "✅ 虚拟麦克风跳线声卡已匹配就绪！";
            VirtualCardInfoBar.Message = "推流目标已自动锁定为虚拟声卡 (CABLE Input)，耳返监听为您自己的物理耳机。请在 VRChat 游戏音频设置中将麦克风选择为 CABLE Output，游戏内的朋友即可听到您的 TTS 实时语音！";
            DownloadVbCableButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            VirtualCardInfoBar.Severity = InfoBarSeverity.Warning;
            VirtualCardInfoBar.Title = "⚠️ 未检测到虚拟麦克风声卡 (当前推流目标暂回退至扬声器/耳机)";
            VirtualCardInfoBar.Message = "Windows 系统不允许软件直接把声音写入物理硬件麦克风。要让 VRChat 里的朋友从麦克风听到声音，需安装免费虚拟声卡跳线 (此处推流目标选 CABLE Input，VRChat 麦克风选 CABLE Output；耳返耳机则选您自己的物理耳机)。请点击右侧按钮下载并安装驱动！";
            DownloadVbCableButton.Visibility = Visibility.Visible;
        }
    }

    private void DownloadVbCableButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://vb-audio.com/Cable/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SettingsService.Instance.AddLog("Browser Error", $"打开官网失败: {ex.Message}", false);
        }
    }

    private void TtsEnableSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsTtsEnabled = TtsEnableSwitch.IsOn;
        SettingsService.Instance.AddLog("TTS Switch", $"TTS 功能已{(TtsEnableSwitch.IsOn ? "开启" : "关闭")}", true);
    }

    private async void StartServerButton_Click(object sender, RoutedEventArgs e)
    {
        StartServerButton.IsEnabled = false;
        await TtsService.Instance.StartManagedServerAsync();
    }

    private void StopServerButton_Click(object sender, RoutedEventArgs e)
    {
        TtsService.Instance.StopManagedServer();
    }

    private void ServerEndpointTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        string ep = ServerEndpointTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(ep))
        {
            SettingsService.Instance.TtsServerEndpoint = ep;
        }
    }

    private void AutoStartServerCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.AutoStartTtsServer = AutoStartServerCheckBox.IsChecked == true;
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadAudioDevices();
    }

    private void VirtualMicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (VirtualMicComboBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            SettingsService.Instance.TtsVirtualMicDeviceId = id;
        }
    }

    private void MicVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.TtsOutputVolume = (float)(MicVolumeSlider.Value / 100.0);
    }

    private void MonitorToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsTtsMonitorEnabled = MonitorToggleSwitch.IsOn;
    }

    private void MonitorDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (MonitorDeviceComboBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            SettingsService.Instance.TtsMonitorDeviceId = id;
        }
    }

    private void MonitorVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.TtsMonitorVolume = (float)(MonitorVolumeSlider.Value / 100.0);
    }

    private async void SpeakToMicButton_Click(object sender, RoutedEventArgs e)
    {
        string text = TestTextInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        SpeakToMicButton.IsEnabled = false;
        ListenLocalButton.IsEnabled = false;
        TestResultTextBlock.Text = "正在流式合成并推流至虚拟麦克风...";

        try
        {
            bool ok = await SettingsService.Instance.SpeakTextStreamAsync(text, force: true);
            if (ok)
            {
                TestResultTextBlock.Text = "流式推流完成！已首字极速出声并推流至虚拟声卡。";
            }
            else
            {
                TestResultTextBlock.Text = "推流未完成或已取消。";
            }
        }
        catch (Exception ex)
        {
            TestResultTextBlock.Text = $"执行异常: {ex.Message}";
        }
        finally
        {
            SpeakToMicButton.IsEnabled = true;
            ListenLocalButton.IsEnabled = true;
        }
    }

    private async void ListenLocalButton_Click(object sender, RoutedEventArgs e)
    {
        string text = TestTextInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        ListenLocalButton.IsEnabled = false;
        SpeakToMicButton.IsEnabled = false;
        TestResultTextBlock.Text = "正在本地耳机流式试听...";

        try
        {
            bool ok = await SettingsService.Instance.SpeakLocalPreviewStreamAsync(text);
            if (ok)
            {
                TestResultTextBlock.Text = "本地试听播放完成！首句已毫秒级响应出声。";
            }
            else
            {
                TestResultTextBlock.Text = "试听未完成或已取消。";
            }
        }
        catch (Exception ex)
        {
            TestResultTextBlock.Text = $"试听异常: {ex.Message}";
        }
        finally
        {
            ListenLocalButton.IsEnabled = true;
            SpeakToMicButton.IsEnabled = true;
        }
    }

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        TtsService.Instance.StopPlayback();
        TestResultTextBlock.Text = "已停止播放";
    }

    private void AutoReadSentMessageSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsTtsAutoReadSentMessage = AutoReadSentMessageSwitch.IsOn;
    }

    private void AutoReadTranslationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsTtsAutoReadTranslation = AutoReadTranslationSwitch.IsOn;
    }
}
