using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views.Sections;

public sealed partial class InteractionSection : UserControl
{
    private bool _isInitializing = true;
    private DispatcherTimer? _statusUpdateTimer;

    public InteractionSection()
    {
        InitializeComponent();
        Loaded += InteractionSection_Loaded;
        Unloaded += InteractionSection_Unloaded;
    }

    private void InteractionSection_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        DirectSendSwitch.IsOn = s.IsDirectSendEnabled;
        SoundSwitch.IsOn = s.IsSoundEnabled;
        TypingSwitch.IsOn = s.IsTypingEnabled;
        EnterSendSwitch.IsOn = s.IsEnterSendEnabled;
        ClearOnSendSwitch.IsOn = s.IsClearOnSendEnabled;
        LiveTypingSwitch.IsOn = s.IsLiveTypingEnabled;

        PersistentTextSwitch.IsOn = s.IsPersistentTextEnabled;
        PersistentTextBox.Text = s.PersistentCustomText ?? string.Empty;
        PersistentTextBox.IsEnabled = s.IsPersistentTextEnabled;

        InGameAvoidanceCheckBox.IsChecked = s.InGameAvoidanceEnabled;
        AvoidanceSecondsNumberBox.Value = s.InGameAvoidanceSeconds;
        InitTransientDuration(s.TransientTextDurationSeconds);

        LoadVariablesToComboBox();
        UpdateCharCount();

        // 注册变量事件监听（先退订：页面缓存下 Loaded 可能重复触发）
        s.VariableService.VariablesListChanged -= OnVariablesListChanged;
        s.VariableService.VariablesListChanged += OnVariablesListChanged;

        // 启动状态刷新定时器 (每 500ms 刷新一次当前保活/避让状态)
        _statusUpdateTimer?.Stop();
        _statusUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusUpdateTimer.Tick += StatusUpdateTimer_Tick;
        _statusUpdateTimer.Start();

        _isInitializing = false;
        RefreshStatusText();
    }

    private void InteractionSection_Unloaded(object sender, RoutedEventArgs e)
    {
        _statusUpdateTimer?.Stop();
        _statusUpdateTimer = null;

        var s = SettingsService.Instance;
        s.VariableService.VariablesListChanged -= OnVariablesListChanged;
    }

    private void OnVariablesListChanged()
    {
        DispatcherQueue.TryEnqueue(LoadVariablesToComboBox);
    }

    private void StatusUpdateTimer_Tick(object? sender, object e)
    {
        RefreshStatusText();
    }

    private void RefreshStatusText()
    {
        var s = SettingsService.Instance;
        if (!s.PersistentTextService.IsEnabled)
        {
            PersistentStatusTextBlock.Text = "常驻未开启";
            PersistentStatusTextBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            return;
        }

        if (s.PersistentTextService.IsInAvoidance(out string reason, out _))
        {
            PersistentStatusTextBlock.Text = reason;
            PersistentStatusTextBlock.Foreground = new SolidColorBrush(Colors.Orange);
        }
        else
        {
            PersistentStatusTextBlock.Text = "常驻保活中";
            PersistentStatusTextBlock.Foreground = new SolidColorBrush(Colors.SeaGreen);
        }
    }

    private void LoadVariablesToComboBox()
    {
        var s = SettingsService.Instance;
        var vars = s.VariableService.GetAllVariables();

        VariableComboBox.Items.Clear();
        foreach (var v in vars)
        {
            // 以 __ 开头的是内部显示用变量（如 __sent_display），不暴露给用户插入
            if (v.Name.StartsWith("__", StringComparison.Ordinal)) continue;

            string typeTag = v.IsBuiltin ? "(内置)" : "(外部)";
            string label = $"{{{v.Name}}}  -  {v.DisplayName} {typeTag}";
            var item = new ComboBoxItem
            {
                Content = label,
                Tag = $"{{{v.Name}}}"
            };
            VariableComboBox.Items.Add(item);
        }
    }

    private void VariableComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (VariableComboBox.SelectedItem is ComboBoxItem item && item.Tag is string varTag)
        {
            InsertTextAtCursor(varTag);
            // 重置选中状态，以便下次重复点击选择
            VariableComboBox.SelectedIndex = -1;
        }
    }

    private void RefreshVariablesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadVariablesToComboBox();
    }

    private void InsertTextAtCursor(string textToInsert)
    {
        int selStart = PersistentTextBox.SelectionStart;
        string current = PersistentTextBox.Text ?? string.Empty;

        if (selStart < 0 || selStart > current.Length)
        {
            selStart = current.Length;
        }

        // 模板本身不限长度（实际发送时会按 VRChat 144 字符截断），
        // 这里不再按 144 截断插入内容。
        string updated = current.Insert(selStart, textToInsert);

        PersistentTextBox.Text = updated;
        int newPos = Math.Min(selStart + textToInsert.Length, updated.Length);
        PersistentTextBox.Focus(FocusState.Programmatic);
        PersistentTextBox.Select(newPos, 0);
        UpdateCharCount();
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

    private void PersistentTextSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool isOn = PersistentTextSwitch.IsOn;
        SettingsService.Instance.IsPersistentTextEnabled = isOn;
        PersistentTextBox.IsEnabled = isOn;

        if (isOn)
        {
            SettingsService.Instance.PersistentTextService.RestartLoop(
                SettingsService.Instance.VariableService,
                SettingsService.Instance.OscService);
        }
        else
        {
            SettingsService.Instance.PersistentTextService.StopLoop();
        }

        RefreshStatusText();
    }

    private void PersistentTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateCharCount();
        SettingsService.Instance.PersistentCustomText = PersistentTextBox.Text;
    }

    private void InGameAvoidanceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.InGameAvoidanceEnabled = InGameAvoidanceCheckBox.IsChecked ?? true;
    }

    private void AvoidanceSecondsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing) return;
        if (!double.IsNaN(sender.Value))
        {
            SettingsService.Instance.InGameAvoidanceSeconds = (int)Math.Clamp(sender.Value, 3, 60);
        }
    }

    private void UpdateCharCount()
    {
        int len = PersistentTextBox.Text?.Length ?? 0;

        // 模板本身不再限制 144 字符；只有真正发送到 VRChat 时才会截断。
        // 超过 144 时给出提示，但不标红（模板长度不是错误）。
        PersistentCharCountTextBlock.Text = len > 144
            ? $"{len} 字符（发送时截断至 144）"
            : $"{len} 字符";
        PersistentCharCountTextBlock.ClearValue(TextBlock.ForegroundProperty);
    }

    private async void ApplyPersistentTextButton_Click(object sender, RoutedEventArgs e)
    {
        string text = PersistentTextBox.Text.Trim();
        var s = SettingsService.Instance;
        s.PersistentCustomText = text;
        if (!string.IsNullOrWhiteSpace(text) && !PersistentTextSwitch.IsOn)
        {
            PersistentTextSwitch.IsOn = true;
            s.IsPersistentTextEnabled = true;
        }

        await s.PersistentTextService.SubmitAsync(text, s.VariableService, s.OscService, s);
        RefreshStatusText();
    }

    private async void ClearPersistentTextButton_Click(object sender, RoutedEventArgs e)
    {
        PersistentTextBox.Text = string.Empty;
        var s = SettingsService.Instance;
        s.PersistentCustomText = string.Empty;
        await s.PersistentTextService.ClearAsync(s.OscService);

        // ClearAsync 已把服务置为未启用；同步开关与持久化配置，避免界面仍显示“开启/保活中”
        if (PersistentTextSwitch.IsOn)
        {
            PersistentTextSwitch.IsOn = false;
        }
        s.IsPersistentTextEnabled = false;

        UpdateCharCount();
        RefreshStatusText();
    }

    private void InitTransientDuration(int duration)
    {
        int selIdx = 3; // 默认 10 秒
        for (int i = 0; i < TransientDurationComboBox.Items.Count; i++)
        {
            if (TransientDurationComboBox.Items[i] is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out int val) && val == duration)
            {
                selIdx = i;
                break;
            }
        }
        TransientDurationComboBox.SelectedIndex = selIdx;
    }

    private void TransientDurationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (TransientDurationComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int val))
        {
            SettingsService.Instance.TransientTextDurationSeconds = val;
        }
    }
}
