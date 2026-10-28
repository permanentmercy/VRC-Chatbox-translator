using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views.Sections;

public sealed partial class NetworkLogSection : UserControl
{
    private bool _isInitializing = true;
    private DispatcherTimer? _inboundRestartTimer;

    /// <summary>端口输入停手多久后才真正重启监听，用来把逐字符输入合并成一次。</summary>
    private const int InboundRestartDebounceMs = 350;

    public NetworkLogSection()
    {
        InitializeComponent();

        _inboundRestartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InboundRestartDebounceMs) };
        _inboundRestartTimer.Tick += (s, e) =>
        {
            _inboundRestartTimer.Stop();
            ApplyInboundPorts();
        };

        Loaded += NetworkLogSection_Loaded;
        Unloaded += NetworkLogSection_Unloaded;
    }

    private void NetworkLogSection_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.InboundService.StatusChanged -= OnInboundStatusChanged;

        // 还有待执行的重启就立刻补上：否则会出现“界面/配置已经是新端口、实际监听还停在旧端口”。
        if (_inboundRestartTimer?.IsEnabled == true)
        {
            _inboundRestartTimer.Stop();
            ApplyInboundPorts();
        }
    }

    private void NetworkLogSection_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        HostTextBox.Text = s.Host;
        PortTextBox.Text = s.Port.ToString();
        LogsListView.ItemsSource = s.Logs;

        InboundSwitch.IsOn = s.InboundEnabled;
        HttpPortTextBox.Text = s.InboundService.HttpPort.ToString();
        OscInPortTextBox.Text = s.InboundService.OscPort.ToString();
        AutoSendExternalSwitch.IsOn = s.InboundService.AutoSendToVrc;

        s.InboundService.StatusChanged -= OnInboundStatusChanged;
        s.InboundService.StatusChanged += OnInboundStatusChanged;
        InboundStatusTextBlock.Text = s.InboundService.IsRunning
            ? $"服务运行中 (HTTP: {s.InboundService.HttpPort}, OSC: {s.InboundService.OscPort})"
            : s.InboundEnabled
                ? "外部接口未运行（启动失败，可点击“重启接口”重试）"
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

        var settings = SettingsService.Instance;
        var inService = settings.InboundService;

        // 记录用户意图；即使本次端口启动失败，下次启动仍会按用户意愿重试，
        // 不会被运行时 IsRunning=false 永久覆盖。
        settings.InboundEnabled = InboundSwitch.IsOn;

        if (InboundSwitch.IsOn)
        {
            inService.Start(inService.HttpPort, inService.OscPort);
        }
        else
        {
            inService.Stop();
        }
        settings.SaveConfigDebounced();
    }

    private void HttpPortTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (int.TryParse(HttpPortTextBox.Text.Trim(), out int port) && port > 1024 && port <= 65535)
        {
            var inService = SettingsService.Instance.InboundService;
            if (inService.HttpPort == port) return;

            inService.HttpPort = port;
            SettingsService.Instance.SaveConfigDebounced();

            // 监听器是在 Start 时按旧端口创建的；只改属性不会迁移监听，
            // 会出现“界面显示新端口、实际仍在旧端口监听”的假象。
            ScheduleInboundRestart();
        }
    }

    private void OscInPortTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (int.TryParse(OscInPortTextBox.Text.Trim(), out int port) && port > 1024 && port <= 65535)
        {
            var inService = SettingsService.Instance.InboundService;
            if (inService.OscPort == port) return;

            inService.OscPort = port;
            SettingsService.Instance.SaveConfigDebounced();

            ScheduleInboundRestart();
        }
    }

    /// <summary>
    /// 端口输入框是逐字符触发的：每敲一个字符就 Stop+Start 会让正在监听的接口反复拆除重建，
    /// 中途的半成品端口（如输入 18080 过程中的 1808）也会被真实绑定并广播“已就绪”，
    /// 外部程序在这期间发出的请求会全部失败。这里只按“参数已落定”触发一次重启。
    /// </summary>
    private void ScheduleInboundRestart()
    {
        if (!SettingsService.Instance.InboundEnabled || !InboundSwitch.IsOn) return;
        _inboundRestartTimer?.Stop();
        _inboundRestartTimer?.Start();
    }

    private void ApplyInboundPorts()
    {
        // 端口改动只有在“外部接口应当运行”时才需要迁移监听。
        // 防抖期间用户可能已经把开关关掉，这里不能再把它拉起来。
        if (!SettingsService.Instance.InboundEnabled || !InboundSwitch.IsOn) return;

        var inService = SettingsService.Instance.InboundService;
        inService.Start(inService.HttpPort, inService.OscPort);
    }

    private void RestartInboundButton_Click(object sender, RoutedEventArgs e)
    {
        var inService = SettingsService.Instance.InboundService;

        // 重启接口视为用户希望保持开启；若开关原本是关的，先拨到开启，
        // 由 Toggled 统一启动，避免“服务在跑但开关显示关闭”的状态分裂。
        if (!InboundSwitch.IsOn)
        {
            InboundSwitch.IsOn = true;
        }
        else
        {
            inService.Start(inService.HttpPort, inService.OscPort);
        }

        SettingsService.Instance.SaveConfigDebounced();
    }

    private void AutoSendExternalSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.InboundService.AutoSendToVrc = AutoSendExternalSwitch.IsOn;

        // AutoSendToVrc 是运行时字段，SaveConfigImmediately 会从它同步回 Config；
        // 这里必须显式写一次配置，否则防抖保存前若发生其它保存，值会被旧状态覆盖。
        SettingsService.Instance.Config.InboundAutoSendToVrc = AutoSendExternalSwitch.IsOn;
        SettingsService.Instance.SaveConfigDebounced();
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.Logs.Clear();
    }
}
