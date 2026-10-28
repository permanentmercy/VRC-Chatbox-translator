# 架构说明

> 本文描述**目标架构**与**当前落地进度**。新功能请按本文的分层规则放置，避免继续把逻辑堆进 `SettingsService`。

## 1. 分层与依赖方向

```
┌──────────────────────────────────────────────┐
│  App/            应用外壳（WinUI 宿主）        │  App.xaml.cs、MainWindow.xaml.cs
│  Ui/             视图（页面 / 片段 / 沉浸窗）   │  只依赖 Core 的公开 API
├──────────────────────────────────────────────┤
│  Core/           业务能力（不依赖 WinUI 控件）  │  可单元测试
│    Configuration 配置模型与持久化              │
│    Variables     变量仓库                      │
│    Templates     常驻文本渲染与保活            │
│    Messaging     OSC 出站 / Inbound 入站       │
│    Speech        识别引擎、字幕管线、音频捕获    │
│    Translation   Ollama 客户端                 │
│    Synthesis     TTS 服务端、流式合成、播放     │
│    Platform      Win32 互操作（托盘/热键/窗口） │
└──────────────────────────────────────────────┘
```

**硬性规则**

1. 依赖只能自上而下：`App → Ui → Core`。`Core` 不允许引用 `Views`、`Microsoft.UI.Xaml.Controls`、`Window`。
2. `Core` 之间按需单向引用，禁止环形依赖。跨模块协作通过**事件**或**注入的接口**，不要互相 `new`。
3. 任何 `static` 单例（如 `SettingsService.Instance`）只能出现在**组合根**（`App`/`MainWindow`）与**兼容层**，新代码请用构造函数注入。
4. 平台相关代码必须收敛到 `Platform/` 或 `*.Win32.cs`，且不承载业务判断。
5. 配置读写只走 `ConfigStore`；业务代码不要直接 `File.WriteAllText(config.json)`。

## 2. 组合根与「上帝对象」的拆解

当前 `SettingsService` 承担了配置持久化、OSC 发送、语音编排、字幕管线、退出清理五类职责，共约 2,660 行。拆解目标：

| 现在 | 目标 | 状态 |
|---|---|---|
| `SettingsService.Config.cs`（204 行） | `Core/Configuration/ConfigStore`（定位/读写/防抖/迁移） | ✅ 已完成 |
| `SettingsService.Osc.cs`（326 行） | `Core/Messaging/ChatSender` + `TypingIndicator` | ⏳ 待做 |
| `SettingsService.Speech.cs`（681 行） | `Core/Speech/SubtitlePipeline` + `SpeechCoordinator` | ⏳ 待做 |
| `SettingsService.Lifecycle.cs`（184 行） | `App/ShutdownCoordinator` | ⏳ 待做 |
| `SettingsService.cs`（1,269 行，属性与日志） | 保留为**门面**（供 UI 绑定），内部委托给上面各模块 | ⏳ 待做 |
| `TtsService.cs`（1,803 行） | `Core/Synthesis/TtsServerProcess` + `TtsStreamClient` + `TtsPlaybackEngine`，`TtsService` 变门面 | ⏳ 待做 |

**拆解手法（保持兼容，逐步推进）**

1. 把实现搬到新类，`SettingsService` 保留同名成员做**一行委托**；
2. UI 代码不动，先让编译与 16 套回归全绿；
3. 全部搬完后，再逐个把 UI 改为直接依赖新模块（此时才允许删门面）。

## 3. 目录约定

```
Services/
  Common/        跨模块的纯数据模型与通用工具（AppConfig、TransparentBackdrop）
  Configuration/ 配置持久化（ConfigStore）
  Input/         Win32 热键（HotkeyService*.cs）
  Osc/           OSC 出站与 Inbound 入站（含 Http/Osc 分部）
  Settings/      SettingsService 门面（过渡期保留）
  Speech/        语音识别、字幕、音频捕获
  Template/      常驻文本与变量
  Translation/   Ollama
  Tts/           TTS
Views/
  *.xaml        设置页外壳
  Sections/     各设置页的片段（一个功能一个文件）
  Immersive/    沉浸窗分部（拖拽 / 命中测试 / Win32）
tests/
  <模块>Regression/  每个套件只测一个模块
```

**分部类（`*.Partial.cs`）使用规范**：只允许按「平台差异」或「机械拆分」拆，不允许按「顺手」拆。一个分部类超过 3 个文件或单一职责跨越两件事时，应改为独立类。

## 4. 测试策略

- 回归测试**优先直接引用生产类**（把源文件 `Compile Include` 进测试工程），只有无法引用 WinUI 的部分才用 Roslyn 抽取源码。
- 新增能力时，先在 `Core` 里写成可实例化的类，再接入 UI —— 这样测试不需要解析源码文本。
- 现有 16 套回归的覆盖：模板渲染、字幕消费、音频解码、TTS 状态机与播放、配置持久化、退出清理、窗口尺寸、Inbound 解析、各设置页控件同步。

## 5. 已知待办（按收益排序）

1. `SettingsService.Osc.cs` → `ChatSender`/`TypingIndicator`（含 110ms 节流与打字心跳）。
2. `SettingsService.Speech.cs` → `SubtitlePipeline`（假说/定稿/翻译触发/队列容量）。
3. `TtsService` 三段拆分（服务端进程 / 流式 HTTP / WASAPI 播放）。
4. `Services/Common` 拆分：`TrayIconService`、`TransparentBackdrop` 属平台/UI，应移入 `Platform/`、`Ui/Shared/`。
5. 测试工程抽取公共 Harness，消除 16 份重复的 Roslyn 抽取代码。
6. `Views/Sections/*.xaml.cs`（合计约 2,200 行）逐步改为通过构造函数接收依赖，减少对 `SettingsService.Instance` 的直接访问。
