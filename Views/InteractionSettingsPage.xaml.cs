using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views;

public sealed partial class InteractionSettingsPage : Page
{
    private bool _isInitializing = true;

    public InteractionSettingsPage()
    {
        InitializeComponent();
        Loaded += InteractionSettingsPage_Loaded;
    }

    private void InteractionSettingsPage_Loaded(object sender, RoutedEventArgs e)
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
        UpdatePersistentCharCount();

        _isInitializing = false;
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
    }

    private void PersistentTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdatePersistentCharCount();
        SettingsService.Instance.PersistentCustomText = PersistentTextBox.Text;
    }

    private void UpdatePersistentCharCount()
    {
        int len = PersistentTextBox.Text.Length;
        PersistentCharCountTextBlock.Text = $"{len} / 144 字符";
        if (len > 144)
        {
            PersistentCharCountTextBlock.Foreground = new SolidColorBrush(Colors.Red);
        }
        else
        {
            PersistentCharCountTextBlock.ClearValue(TextBlock.ForegroundProperty);
        }
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

        await s.PersistentTextService.SubmitAsync(text, s.OscService, s);
        PersistentStatusTextBlock.Text = "已提交至 VRChat";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (ts, te) =>
        {
            timer.Stop();
            PersistentStatusTextBlock.Text = string.Empty;
        };
        timer.Start();
    }

    private async void ClearPersistentTextButton_Click(object sender, RoutedEventArgs e)
    {
        PersistentTextBox.Text = string.Empty;
        var s = SettingsService.Instance;
        s.PersistentCustomText = string.Empty;
        await s.PersistentTextService.ClearAsync(s.OscService);
        PersistentStatusTextBlock.Text = "已清空";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (ts, te) =>
        {
            timer.Stop();
            PersistentStatusTextBlock.Text = string.Empty;
        };
        timer.Start();
    }
}
