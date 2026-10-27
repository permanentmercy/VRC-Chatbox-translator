using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VrcChatboxDemo.Services;

namespace VrcChatboxDemo.Views.Sections;

public sealed partial class TranslationSection : UserControl
{
    private bool _isInitializing = true;
    private bool _isLoadingModels = false;

    public TranslationSection()
    {
        InitializeComponent();
        Loaded += TranslationSection_Loaded;
    }

    private async void TranslationSection_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        var s = SettingsService.Instance;

        TranslationSwitch.IsOn = s.IsTranslationEnabled;
        OllamaEndpointTextBox.Text = s.OllamaEndpoint;
        InitTargetLanguage(s.TargetLanguage);

        OllamaPromptTextBox.Text = s.OllamaPromptTemplate;
        InitRequestFrequency(s.OllamaRequestFrequency);

        ShowRecognizedTextSwitch.IsOn = s.ShowRecognizedText;
        ShowTranslatedTextSwitch.IsOn = s.ShowTranslatedText;
        ShowLatencySwitch.IsOn = s.ShowTranslationLatency;
        SentMessageTranslationSwitch.IsOn = s.IsSentMessageTranslationEnabled;

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

    /// <summary>
    /// 填充「Ollama 请求频率」下拉框。选项表来自 SettingsService，避免 UI 与
    /// 节流逻辑各写一份档位定义而对不上。
    /// </summary>
    private void InitRequestFrequency(string currentMode)
    {
        RequestFrequencyComboBox.Items.Clear();
        foreach (var opt in SettingsService.RequestFrequencyOptions)
        {
            RequestFrequencyComboBox.Items.Add(new ComboBoxItem
            {
                Content = opt.Label,
                Tag = opt.Mode
            });
        }

        var resolved = SettingsService.ResolveRequestFrequency(currentMode);
        for (int i = 0; i < RequestFrequencyComboBox.Items.Count; i++)
        {
            if (RequestFrequencyComboBox.Items[i] is ComboBoxItem item &&
                item.Tag is string mode &&
                string.Equals(mode, resolved.Mode, StringComparison.OrdinalIgnoreCase))
            {
                RequestFrequencyComboBox.SelectedIndex = i;
                break;
            }
        }
        if (RequestFrequencyComboBox.SelectedIndex < 0)
        {
            RequestFrequencyComboBox.SelectedIndex = 0;
        }

        UpdateRequestFrequencyHint();
    }

    private void UpdateRequestFrequencyHint()
    {
        if (RequestFrequencyHintTextBlock is null) return;

        var opt = SettingsService.ResolveRequestFrequency(
            (RequestFrequencyComboBox.SelectedItem as ComboBoxItem)?.Tag as string);

        if (opt.IntervalMs <= 0)
        {
            RequestFrequencyHintTextBlock.Text =
                "不做假说预翻译：只有在语音识别定稿后才会请求 Ollama，最省资源，但说话过程中字幕不会提前显示译文。";
        }
        else
        {
            RequestFrequencyHintTextBlock.Text =
                $"最快每 {opt.IntervalMs / 1000.0:0.0#} 秒发一次请求，且内容至少比上次多 {opt.MinNewChars} 个字才触发。" +
                "频率越高字幕越跟手，对本地模型压力也越大（定稿句的翻译不受影响，永远只发一次）。";
        }
    }

    private async Task LoadOllamaModelsAsync()
    {
        if (_isLoadingModels) return;
        _isLoadingModels = true;
        try
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
        finally
        {
            _isLoadingModels = false;
        }
    }

    private async void TranslationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsTranslationEnabled = TranslationSwitch.IsOn;
        SettingsService.Instance.NotifyDisplaySettingsChanged();
        SettingsService.Instance.AddLog("Translation", $"Ollama 翻译开关已{(TranslationSwitch.IsOn ? "开启" : "关闭")} (模型: {SettingsService.Instance.OllamaModel}, 目标语言: {SettingsService.Instance.TargetLanguage})", true);

        // 如果关闭翻译服务，立即向 Ollama 发送请求将模型从显存中卸载丢弃
        if (!TranslationSwitch.IsOn)
        {
            try
            {
                int unloaded = await SettingsService.Instance.OllamaService.UnloadAllModelsAsync(SettingsService.Instance.OllamaEndpoint);
                if (unloaded > 0)
                {
                    SettingsService.Instance.AddLog("Translation", $"已自动将 {unloaded} 个 Ollama 模型从 GPU 显存中卸载丢弃", true);
                }
            }
            catch { }
        }
    }

    private async void TestTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        TestTranslationButton.IsEnabled = false;
        OllamaStatusTextBlock.Text = "正在向 Ollama 发送测试翻译请求...";
        try
        {
            var result = await SettingsService.Instance.TestOllamaTranslationAsync("你好，欢迎来到 VRChat！");
            OllamaStatusTextBlock.Text = $"测试成功 ({result.LatencyMs}ms): \"你好，欢迎来到 VRChat！\" -> \"{result.Text}\"";
        }
        catch (Exception ex)
        {
            OllamaStatusTextBlock.Text = $"测试失败: {ex.Message}";
        }
        finally
        {
            TestTranslationButton.IsEnabled = true;
        }
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

    private async void OllamaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isLoadingModels) return;
        string oldModel = SettingsService.Instance.OllamaModel;
        string? newModel = null;
        if (OllamaModelComboBox.SelectedItem is string modelStr)
        {
            newModel = modelStr;
        }
        else if (!string.IsNullOrWhiteSpace(OllamaModelComboBox.Text))
        {
            newModel = OllamaModelComboBox.Text.Trim();
        }

        if (!string.IsNullOrEmpty(newModel) && !string.Equals(oldModel, newModel, StringComparison.OrdinalIgnoreCase))
        {
            SettingsService.Instance.OllamaModel = newModel;
            // 切换模型时，把上一个模型的权重从显存中丢弃
            if (!string.IsNullOrEmpty(oldModel))
            {
                try
                {
                    bool ok = await SettingsService.Instance.OllamaService.UnloadModelAsync(oldModel, SettingsService.Instance.OllamaEndpoint);
                    if (ok)
                    {
                        SettingsService.Instance.AddLog("Translation", $"已将旧模型 [{oldModel}] 从显存中丢弃释放", true);
                    }
                }
                catch { }
            }
        }
    }

    private void OllamaModelComboBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isLoadingModels) return;

        // 选中列表项的情况已由 SelectionChanged 处理；这里只处理“用户手输了
        // 列表外模型名”的情况。否则加载时回退选中项会在失焦时把配置改掉。
        if (OllamaModelComboBox.SelectedItem is string) return;

        string? typed = OllamaModelComboBox.Text?.Trim();
        if (!string.IsNullOrEmpty(typed) &&
            !string.Equals(typed, SettingsService.Instance.OllamaModel, StringComparison.OrdinalIgnoreCase))
        {
            SettingsService.Instance.OllamaModel = typed;
            SettingsService.Instance.AddLog("Translation", $"翻译模型已设为手输模型 [{typed}]", true);
        }
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadOllamaModelsAsync();
    }

    private void RequestFrequencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (RequestFrequencyComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;

        SettingsService.Instance.OllamaRequestFrequency = mode;
        UpdateRequestFrequencyHint();

        var opt = SettingsService.ResolveRequestFrequency(mode);
        string detail = opt.IntervalMs <= 0
            ? "关闭假说预翻译"
            : $"每 {opt.IntervalMs}ms 最多一次，最少新增 {opt.MinNewChars} 字";
        SettingsService.Instance.AddLog("Translation", $"Ollama 请求频率已设为「{opt.Label}」（{detail}）", true);
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

    private void SentMessageTranslationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SettingsService.Instance.IsSentMessageTranslationEnabled = SentMessageTranslationSwitch.IsOn;
        SettingsService.Instance.AddLog("Translation",
            $"发送消息自动翻译已{(SentMessageTranslationSwitch.IsOn ? "开启" : "关闭")}", true);
    }
}
