# UI 布局与导航优化结果

## 当前状态

已完成源码调整与编译检查；未修改 TTS 服务代码。

## 已完成改动

- 在 `App.xaml` 建立共享设置页样式：内容基准宽度 700 DIP、最大宽度 960 DIP；窄窗口或导航展开使内容区变窄时可横向滚动，避免控件不可达。
- 所有设置子页共用相同的滚动和内容列样式，页面切换时内容列保持一致。
- 缩小翻译、交互和 TTS 分区的固定列宽，确保主要卡片内容能放入统一内容列。
- 主聊天页与各设置页启用 `NavigationCacheMode="Enabled"`，Frame 缓存大小设为 7；页面导航使用 `SuppressNavigationTransitionInfo`，保留草稿/滚动状态并去除横向位移切换。
- 快捷键修改说明集中显示在热键页标题下；移除两个热键框的重复提示、沉浸模式按钮上可能过期的固定 `Ctrl + Shift + Z` 提示，并精简输入发送说明。
- “恢复默认排序”现在包含 TTS 导航项，顺序与初始导航一致。

## 验证

- `git diff --check`：通过。
- `dotnet build .\VrcChatboxDemo.csproj --no-restore --output .\artifacts\ui-build-check -p:UseAppHost=false`：成功，0 错误。
- 构建有 20 条重复输出的 `CS0618` 警告，均指向工作区原有 `Services/Tts/TtsService.cs` 中过时的 `WasapiOut` API；该文件未在本次改动中修改。
- 首次使用默认 `bin` 输出目录构建时，exe 被运行中的进程占用；改用已忽略的 `artifacts/` 输出目录后构建成功。
- 未运行自动化测试或手动视觉检查；本次验证为 XAML/C# 编译与静态布局核对。
