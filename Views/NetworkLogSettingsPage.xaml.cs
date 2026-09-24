using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views;

public sealed partial class NetworkLogSettingsPage : Page
{
    private bool _isInitializing = true;

    public NetworkLogSettingsPage()
    {
        InitializeComponent();
        Loaded += NetworkLogSettingsPage_Loaded;
        Unloaded += NetworkLogSettingsPage_Unloaded;
    }

    private void NetworkLogSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.InboundService.StatusChanged -= OnInboundStatusChanged;
    }

    private void NetworkLogSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        HostTextBox.Text = s.Host;
        PortTextBox.Text = s.Port.ToString();
        LogsListView.ItemsSource = s.Logs;

        InboundSwitch.IsOn = s.InboundService.IsRunning;
        HttpPortTextBox.Text = s.InboundService.HttpPort.ToString();
        OscInPortTextBox.Text = s.InboundService.OscPort.ToString();
        AutoSendExternalSwitch.IsOn = s.InboundService.AutoSendToVrc;

        s.InboundService.StatusChanged += OnInboundStatusChanged;
        InboundStatusTextBlock.Text = s.InboundService.IsRunning
            ? $"服务运行中 (HTTP: {s.InboundService.HttpPort}, OSC: {s.InboundService.OscPort})"
            : "外部接口已关闭";

        ConfigPathTextBlock.Text = SettingsService.ConfigPath;

        _isInitializing = false;
    }

    private void SaveConfigNowButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.SaveConfigImmediately();
    }

    private void OpenConfigFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = SettingsService.ConfigPath;
            if (System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                string? dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch { }
    }

    private void OnInboundStatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            InboundStatusTextBlock.Text = status;
        });
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

    private void ResetConfigButton_Click(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        SettingsService.Instance.Host = "127.0.0.1";
        SettingsService.Instance.Port = 9000;
        HostTextBox.Text = "127.0.0.1";
        PortTextBox.Text = "9000";
        _isInitializing = false;
    }

    private void InboundSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var inService = SettingsService.Instance.InboundService;
        if (InboundSwitch.IsOn)
        {
            inService.Start(inService.HttpPort, inService.OscPort);
        }
        else
        {
            inService.Stop();
        }
        SettingsService.Instance.SaveConfigDebounced();
    }

    private void HttpPortTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (int.TryParse(HttpPortTextBox.Text.Trim(), out int port) && port > 1024 && port <= 65535)
        {
            SettingsService.Instance.InboundService.HttpPort = port;
            SettingsService.Instance.SaveConfigDebounced();
        }
    }

    private void OscInPortTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (int.TryParse(OscInPortTextBox.Text.Trim(), out int port) && port > 1024 && port <= 65535)
        {
            SettingsService.Instance.InboundService.OscPort = port;
            SettingsService.Instance.SaveConfigDebounced();
        }
    }

    private void RestartInboundButton_Click(object sender, RoutedEventArgs e)
    {
        var inService = SettingsService.Instance.InboundService;
        inService.Start(inService.HttpPort, inService.OscPort);
        InboundSwitch.IsOn = inService.IsRunning;
        SettingsService.Instance.SaveConfigDebounced();
    }

    private void AutoSendExternalSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.InboundService.AutoSendToVrc = AutoSendExternalSwitch.IsOn;
        SettingsService.Instance.SaveConfigDebounced();
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Logs.Clear();
    }
}
