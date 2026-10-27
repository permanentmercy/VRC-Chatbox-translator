# UI 缺陷排查结果

## 当前状态

静态检查已完成，共确认 2 项 UI 缺陷。未修改应用源码。

## 已确认缺陷

### UI-01：设置页面在导航展开或窗口变窄时裁切右侧内容

- 严重度：中
- 位置：`Views/SpeechSettingsPage.xaml:11-16`；同样的 `MinWidth="680"` 和禁用横向滚动设置也出现在 `TranslationSettingsPage.xaml`、`TtsSettingsPage.xaml`、`HotkeySettingsPage.xaml`、`InteractionSettingsPage.xaml`、`NetworkLogSettingsPage.xaml` 与 `SettingsPage.xaml`。
- 复现：打开任意设置页后展开左侧导航抽屉，或将主窗口缩窄，使内容区不足约 704 DIP（680 DIP 最小内容宽度加左右 12 DIP 边距）。
- 结果：内部内容仍至少 680 DIP 宽，但外层 `ScrollViewer` 禁用了横向滚动；较窄的视口无法访问右侧被裁掉的控件/字段。窗口恢复宽度下限只有 400 DIP，因此该布局状态可由应用支持的窗口尺寸触发。
- 证据：设置页外层 ScrollViewer 仅允许纵向滚动，内层 StackPanel 强制 `MinWidth="680"`。主窗口导航使用 `LeftCompact`，用户展开 Pane 后会进一步压缩内容视口。

### UI-02：“恢复默认排序”会把 TTS 导航项移动到末尾

- 严重度：低
- 位置：`MainWindow.xaml.cs:273-277`；补全缺失导航项的逻辑在 `MainWindow.xaml.cs:203-209`。
- 复现：在任意左侧导航项上打开右键菜单，选择“恢复默认排序”。
- 结果：TTS 不在 `defaultOrder` 列表内，随后由补全逻辑追加到列表末尾；实际顺序变为“消息发送、语音识别、翻译配置、快捷热键、交互发送、网络日志、TTS 语音”。初始界面顺序则是 TTS 位于翻译配置之后、快捷热键之前。
- 证据：`MainWindow.xaml` 中 TTS 导航项位于快捷热键之前；重置列表遗漏了 `"tts"`。

## 已检查范围

- `App.xaml`、`MainWindow.xaml` 与主窗口代码后置。
- 聊天页及 IME/输入、字幕显示相关 UI 事件。
- 沉浸式窗口 XAML、窗口拖动与命中区域代码。
- 设置页外层布局与语音、翻译、快捷键、交互、网络日志、TTS 分区的主要布局/状态处理。

## 范围限制

只调查 UI 缺陷；本次为静态检查，未运行应用或测试，因此不包含运行时截图与多显示器/不同 DPI 实测。工作区原有的 `Services/Tts/TtsService.cs` 修改保持不变。
