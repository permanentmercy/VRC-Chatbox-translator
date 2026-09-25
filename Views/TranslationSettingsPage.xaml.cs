using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views;

public sealed partial class TranslationSettingsPage : Page
{
    private bool _isInitializing = true;

    public TranslationSettingsPage()
    {
        InitializeComponent();
        Loaded += TranslationSettingsPage_Loaded;
    }

    private async void TranslationSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        TranslationSwitch.IsOn = s.IsTranslationEnabled;
        OllamaEndpointTextBox.Text = s.OllamaEndpoint;
        InitTargetLanguage(s.TargetLanguage);

        OllamaPromptTextBox.Text = s.OllamaPromptTemplate;

        ShowRecognizedTextSwitch.IsOn = s.ShowRecognizedText;
        ShowTranslatedTextSwitch.IsOn = s.ShowTranslatedText;
        ShowLatencySwitch.IsOn = s.ShowTranslationLatency;

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

    private void TranslationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsTranslationEnabled = TranslationSwitch.IsOn;
        SettingsService.Instance.NotifyDisplaySettingsChanged();
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

    private void OllamaPromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        string prompt = OllamaPromptTextBox.Text;
        SettingsService.Instance.OllamaPromptTemplate = prompt;
        PromptStatusTextBlock.Text = "提示词已保存";
    }

    private void ResetPromptButton_Click(object sender, RoutedEventArgs e)
    {
        OllamaPromptTextBox.Text = SettingsService.DefaultOllamaPromptTemplate;
        SettingsService.Instance.OllamaPromptTemplate = SettingsService.DefaultOllamaPromptTemplate;
        PromptStatusTextBlock.Text = "已恢复默认提示词";
    }

    private void ShowRecognizedTextSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.ShowRecognizedText = ShowRecognizedTextSwitch.IsOn;
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
}
