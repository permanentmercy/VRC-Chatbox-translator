# 项目长期笔记 — VrcChatboxDemo

## 技术栈
- WinUI 3 + Windows App SDK 1.8 + .NET 10
- Unpackaged Win32 应用（`<WindowsPackageType>None</WindowsPackageType>`）
- 入口：`MainWindow.xaml`（含 MicaBackdrop）→ NavigationView 抽屉 → ContentFrame 导航到 7 个 Page（Chat / Speech / Translation / Tts / Hotkey / Interaction / NetworkLog）
- 设置面板采用卡片式布局：`Views/Pages/*SettingsPage.xaml` + `Views/Sections/*Section.xaml`（UserControl 复用）

## WinUI 3 布局经验（重要）
- **`Grid.Row` 索引越界**：超出 `RowDefinitions.Count - 1` 时**不会**自动插入新行，而是被强制塞回最后一行；`Column` 同理。若同时设置 `ColumnSpan="2"`，会横跨覆盖整行。表现为"元素能看见但点不到"（TextBlock 即便 `Text=""` 也会吞命中测试）。来源：Microsoft Learn `Microsoft.UI.Xaml.Controls.Grid`。
- **`RowSpan/ColumnSpan` 大于总数**：被当成总数并跨越所有行 / 列；行为与越界索引不同。
- 任何状态 / 提示类 `TextBlock` 都应显式加 `IsHitTestVisible="False"`，避免未来 Grid 声明错误时再次引发"透明拦截"。

## 诊断经验
- "看得见但点不到"是 WinUI / WPF 典型症状。优先排查：
  1. Z 序覆盖（顶层透明 Canvas / Grid）
  2. 命中测试（`IsHitTestVisible="False"` 或继承自父）
  3. Grid 越界 / 错位（特别是 `Grid.Row` 索引超出 `RowDefinitions.Count`）
  4. 宽度溢出（固定列宽超出容器可用宽度，元素落在可视区外）
- 不要先入为主去查事件订阅 —— 通常不是事件订阅问题。

## 构建 / 警告
- `Services/Tts/TtsService.cs` 中 `WasapiOut` 已过时，会产生 20 个 `CS0618` 警告，不影响功能。新代码应使用 `WasapiPlayerBuilder`。