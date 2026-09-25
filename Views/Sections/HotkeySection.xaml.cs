using System;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VrcChatboxDemo.Services;
using Windows.System;
using Windows.UI.Core;

namespace VrcChatboxDemo.Views.Sections;

public sealed partial class HotkeySection : UserControl
{
    private bool _isInitializing = true;
    private readonly Action _onHotkeyRegistrationChanged;

    public HotkeySection()
    {
        InitializeComponent();
        _onHotkeyRegistrationChanged = () => DispatcherQueue.TryEnqueue(UpdateHotkeyStatusVisuals);
        Loaded += HotkeySection_Loaded;
        Unloaded += HotkeySection_Unloaded;
    }

    private void HotkeySection_Unloaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.HotkeyService.HotkeyRegistrationChanged -= _onHotkeyRegistrationChanged;
    }

    private void HotkeySection_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        HotkeySwitch.IsOn = s.IsHotkeyEnabled;
        HotkeyTextBox.Text = s.CustomHotkeyString;

        ImmersiveHotkeySwitch.IsOn = s.IsImmersiveHotkeyEnabled;
        ImmersiveHotkeyTextBox.Text = s.CustomImmersiveHotkeyString;

        SettingsService.Instance.HotkeyService.HotkeyRegistrationChanged -= _onHotkeyRegistrationChanged;
        SettingsService.Instance.HotkeyService.HotkeyRegistrationChanged += _onHotkeyRegistrationChanged;

        _isInitializing = false;
        UpdateHotkeyStatusVisuals();
    }

    private void UpdateHotkeyStatusVisuals()
    {
        var s = SettingsService.Instance;
        var hk = s.HotkeyService;

        if (!s.IsHotkeyEnabled || hk.IsWakeRegistered)
        {
            HotkeyStatusInfoBar.IsOpen = false;
        }
        else
        {
            HotkeyStatusInfoBar.Severity = InfoBarSeverity.Error;
            HotkeyStatusInfoBar.Title = "热键冲突或被占用";
            HotkeyStatusInfoBar.Message = $"热键 {s.CustomHotkeyString} 已被其他运行中的软件或后台程序占用 (系统错误码: {hk.LastWakeError})，请更换按键。";
            HotkeyStatusInfoBar.IsOpen = true;
        }

        if (!s.IsImmersiveHotkeyEnabled || hk.IsImmersiveRegistered)
        {
            ImmersiveHotkeyStatusInfoBar.IsOpen = false;
        }
        else
        {
            ImmersiveHotkeyStatusInfoBar.Severity = InfoBarSeverity.Error;
            ImmersiveHotkeyStatusInfoBar.Title = "热键冲突或被占用";
            ImmersiveHotkeyStatusInfoBar.Message = $"热键 {s.CustomImmersiveHotkeyString} 已被其他运行中的软件或后台程序占用 (系统错误码: {hk.LastImmersiveError})，请更换按键。";
            ImmersiveHotkeyStatusInfoBar.IsOpen = true;
        }
    }

    private void HotkeySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsHotkeyEnabled = HotkeySwitch.IsOn;
        SettingsService.Instance.ApplyHotkey();
        UpdateHotkeyStatusVisuals();
    }

    private void ImmersiveHotkeySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsImmersiveHotkeyEnabled = ImmersiveHotkeySwitch.IsOn;
        SettingsService.Instance.ApplyHotkey();
        UpdateHotkeyStatusVisuals();
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            e.Handled = true;
            HotkeyTextBox.Text = SettingsService.Instance.CustomHotkeyString;
            ResetHotkeyButton.Focus(FocusState.Programmatic);
            return;
        }

        if (IsModifierOnly(key)) return;

        e.Handled = true;
        ParseAndApplyKey(key, isImmersive: false);
    }

    private void ImmersiveHotkeyTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            e.Handled = true;
            ImmersiveHotkeyTextBox.Text = SettingsService.Instance.CustomImmersiveHotkeyString;
            ResetImmersiveHotkeyButton.Focus(FocusState.Programmatic);
            return;
        }

        if (IsModifierOnly(key)) return;

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
        UpdateHotkeyStatusVisuals();
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
        UpdateHotkeyStatusVisuals();
    }

    private void ResetImmersiveHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Instance;
        s.CustomImmersiveHotkeyModifiers = HotkeyService.MOD_CONTROL | HotkeyService.MOD_SHIFT;
        s.CustomImmersiveHotkeyKey = 0x5A;
        s.CustomImmersiveHotkeyString = "Ctrl + Shift + Z";
        s.ApplyHotkey();
        ImmersiveHotkeyTextBox.Text = s.CustomImmersiveHotkeyString;
        UpdateHotkeyStatusVisuals();
    }
}
