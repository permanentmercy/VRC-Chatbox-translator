# VRC Chatbox Translator

> 为 VRChat 设计的桌面端实时字幕、本地 AI 翻译与语音合成辅助工具。基于 **WinUI 3 + Windows App SDK** 构建，通过 **OSC 协议**把语音识别结果、翻译结果和自定义文本直接推送到游戏内角色头顶气泡（Chatbox）。

---

## 目录

- [项目简介](#项目简介)
- [核心能力一览](#核心能力一览)
- [整体架构](#整体架构)
- [两种窗口模式](#两种窗口模式)
- [设置页说明](#设置页说明)
- [外部调用接口（HTTP / OSC Inbound）](#外部调用接口http--osc-inbound)
- [配置文件 `config.json`](#配置文件-configjson)
- [技术栈与依赖](#技术栈与依赖)
- [构建与运行](#构建与运行)
- [常见问题排查](#常见问题排查)
- [项目结构](#项目结构)
- [开源协议](#开源协议)

---

## 项目简介

VRC Chatbox Translator 是一款面向 VRChat 玩家的桌面辅助工具，主要解决三个痛点：

1. **跨语言社交** —— 把你说的话实时识别并翻译成对方语言，直接显示在头顶气泡里。
2. **字幕输出** —— 把语音转写结果作为同屏字幕展示，配合沉浸式透明窗可做"悬浮字幕"。
3. **语音播报** —— 通过 IndexTTS 本地语音合成 + 虚拟声卡，把发送的消息/翻译以语音念出来。

软件完全本地运行（可选连接本机 Ollama / IndexTTS 服务），不依赖任何第三方云端账号，隐私友好。所有配置通过 `config.json` 自动持久化，支持系统托盘常驻、单实例运行与全局快捷键。

---

## 核心能力一览

### 1. 语音识别（两种引擎可切换）

| 引擎 | 说明 |
| --- | --- |
| **Windows 11 实时字幕**（`LiveCaptions`） | 直接捕获系统内置 LiveCaptions 文本流，**不额外占用 GPU 显存**，对游戏帧率几乎无影响。支持隐藏原生字幕窗口、自动检测语言并按周期切换识别语言。 |
| **本地 Whisper**（`Whisper`） | 内置离线 Whisper 识别（`tiny` / `base` / `small` 模型），支持 CUDA 加速或纯 CPU 推理，可指定中文简繁体输出偏好。 |

- **自动断句**：识别出的长句按标点与停顿自动切分，避免一次性堆积过多文字。
- **音频源选择**：可指定麦克风设备，或使用"进程隔离"模式仅监听 VRChat 游戏进程音频（避免采集到游戏音效）。
- **中文输入保护**：在拼音输入选词阶段不发送未确定的拼音字母，避免在游戏内"穿帮"。

### 2. 本地 AI 翻译

- 连接本机运行的 **Ollama** 服务进行翻译，可自定义模型名称、接口地址、目标语言与翻译提示词（支持 `{targetLanguage}`、`{text}` 占位符）。
- 提供"识别/翻译直接发送"选项：说话或翻译结束时直接推送到头顶气泡，**不会篡改或覆盖当前输入框里的未发送草稿**，且不触发打字动画。
- 可选显示翻译请求延迟（毫秒）。

### 3. 游戏内聊天与打字交互（OSC）

- **实时打字预览**：开启逐字上屏后，输入时实时同步到头顶气泡，回车确认发送并播放提示音。
- **OSC 限流保底**：严格遵守 VRChat 单包间隔 ≥ 110ms 的限流要求，使用发送互斥锁 + 防抖，避免丢包。
- **头顶常驻文本**：可在头顶循环保持一段自定义文字（例如个人状态、挂机提示）。主动说话或发送新消息时优先展示新内容，气泡淡出后自动恢复常驻文本。
- 支持 `{变量名}` 动态变量（如 `{speech}`、`{translation}`、`{language}`）与 `[文本]` 格式的"瞬态文本"（带有效时长，过期自动恢复纯常驻）。

### 4. 透明沉浸悬浮窗

- 桌面沉浸式半透明窗口，未被卡片覆盖的空白区域支持**鼠标硬件级穿透**，可直接操作底层游戏或桌面。
- 支持 **Alt + 鼠标左键 / 中键拖拽**移动；全局快捷键（默认 `Ctrl+Shift+Z`）随时呼出/隐藏。

### 5. 语音合成（TTS，可选）

- 通过 **IndexTTS**（自回归语音模型 + 本地 HTTP 服务 + 声码器）输出高质量中文语音。
- **流式推流**：首块音频到达即唤醒声卡推流，后续音频边生成边播放，首包延迟更低。
- **虚拟麦克风推流**：把合成语音推送到 VB-Cable 之类的虚拟声卡，让游戏内队友也能听到你的"语音版消息"。
- 内置智能语义断句（句子级切分、超长句细分、过短片段粘合、VRChat 富文本标签过滤、句末标点补齐）。
- 可选"自动朗读已发送消息"或"自动朗读翻译结果"。

### 6. 外部调用接口

- 内置轻量 **HTTP REST API** + **UDP OSC Inbound** 端口，支持第三方脚本/程序直接调用本工具向 VRChat 发送消息、更新变量或触发避让。详见 [外部调用接口](#外部调用接口http--osc-inbound)。

---

## 整体架构

软件采用 **单实例 + 多服务聚合** 的设计。核心是一个全局单例 `SettingsService`，它聚合管理所有后台服务并作为 UI 与底层能力的桥梁。

```
App (单实例互斥 + 崩溃日志 + 双窗口)
 ├─ MainWindow          —— 主设置窗口（NavigationView 抽屉导航 + 7 个设置页）
 └─ ImmersiveWindow     —— 透明沉浸悬浮窗（穿透点击 + 拖拽 + 输入）
        │
SettingsService (聚合根 / 全局单例)
 ├─ OscService            —— UDP 发送 OSC 到 VRChat（SendChatboxMessage / SendTyping / 限流）
 ├─ SpeechService         —— 本地 Whisper 识别（模型管理 / CUDA / 转写 / 中文处理）
 ├─ LiveCaptionsService   —— Windows 11 实时字幕桥接（窗口隐藏 / 语言切换 / 自动检测）
 │    └─ LiveCaptionsLanguageAutoSwitcher —— 周期性自动识别语言并切换
 ├─ OllamaService         —— 本地 Ollama HTTP 客户端（拉取模型列表 / 翻译请求）
 ├─ TtsService            —— IndexTTS 流式推流 + 虚拟声卡（WasapiPlayer 输出）
 ├─ InboundService        —— 外部接入（HttpListener + UdpClient，接收第三方文本/变量）
 ├─ PersistentTextService —— 头顶常驻文本循环 + 变量插值 + 游戏内避让
 ├─ VariableService       —— 动态变量仓库（供模板 {{变量}} 引用）
 ├─ VrcInGameGuard        —— 检测游戏内输入/打字，触发常驻文本避让
 └─ HotkeyService         —— 全局快捷键（Win32 低层键盘钩子）

辅助模块：
 ├─ AudioDeviceService / AudioProcessService / ProcessLoopbackCapture —— 音频设备与游戏进程音频捕获
 ├─ EarLanguageDetector   —— "听声辨语言"辅助检测
 ├─ TrayIconService       —— 系统托盘图标与菜单
 └─ TransparentBackdrop   —— 沉浸窗透明背景
```

**关键设计点**：

- **UI 与后台解耦**：ViewModel 通过 `SettingsService` 暴露的属性（带 `INotifyPropertyChanged` 风格的通知）与事件（`SpeechRecognizedUpdated`、`TranslationUpdated`、`TtsActiveStateChanged` 等）订阅状态；所有后台线程回调都通过 `App.DispatcherQueueInstance` 回到 UI 线程。
- **限流与互斥**：所有 OSC 发送经过 `SettingsService` 的 `_sendSemaphore` 互斥 + ≥110ms 间隔保底，避免 VRChat 限流丢包。
- **崩溃兜底**：`App.xaml.cs` 在 `AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`、`UnhandledException` 三处把异常写入 `crash.log`，便于排查。

---

## 两种窗口模式

| 模式 | 窗口 | 用途 |
| --- | --- | --- |
| **主窗口（正常模式）** | `MainWindow` | 左侧 `NavigationView` 抽屉导航，右侧 `ContentFrame` 承载 7 个设置页。含自定义标题栏 + 置顶 / 沉浸切换按钮。 |
| **沉浸模式** | `ImmersiveWindow` | 半透明悬浮窗，空白区鼠标穿透。通过 `Ctrl+Shift+Z`（默认）或标题栏按钮切换；切换时主窗与沉浸窗互斥显示。 |

两种窗口共享同一套状态与发送逻辑（`SettingsService`），切换不影响后台服务运行。

### 设置页（NavigationView 7 项）

| 导航项 | 页面 | 主要功能 |
| --- | --- | --- |
| 消息发送 | `ChatPage` | 同屏预览识别原文 + 翻译结果，手动输入框，TTS 试听，连接状态。 |
| 语音识别 | `SpeechSettingsPage` | 选择识别引擎（LiveCaptions / Whisper）、模型规格、语言、中文输出偏好、GPU 负载策略、音频输入设备、切片时长、CUDA 状态告警。 |
| 翻译配置 | `TranslationSettingsPage` | Ollama 端点、翻译模型下拉（可刷新列表）、目标语言、自定义提示词、翻译开关与显示选项。 |
| TTS 语音 | `TtsSettingsPage` | IndexTTS 服务地址、自动启动、Python/脚本路径、模型名、虚拟麦克风设备、监听音量、自动朗读开关。 |
| 快捷热键 | `HotkeySettingsPage` | 绑定全局唤醒快捷键与沉浸切换快捷键。 |
| 交互发送 | `InteractionSettingsPage` | 直接上屏 / 提示音 / 打字动画 / Enter 发送 / 发后清空 / 逐字上屏、常驻模板变量、游戏内避让时长。 |
| 网络日志 | `NetworkLogSettingsPage` | OSC 收发日志、外部接口状态、HTTP/OSC Inbound 端口与自动发送开关。 |

> 导航项支持**长按拖拽重排**，顺序持久化在 `config.json` 的 `NavOrder` 中。

---

## 外部调用接口（HTTP / OSC Inbound）

当 `config.json` 中 `InboundEnabled = true` 时，软件会同时监听：

- **HTTP**：`http://127.0.0.1:{InboundHttpPort}/`（默认 `9002`）
- **UDP OSC**：`{InboundOscPort}`（默认 `9001`）

### HTTP REST API

所有响应均为 JSON，且默认放开 CORS（`Access-Control-Allow-Origin: *`），便于浏览器脚本调用。

#### 发送消息到 VRChat

```http
GET  http://127.0.0.1:9002/send?text=Hello%20VRChat
POST http://127.0.0.1:9002/send
Content-Type: application/json

{ "text": "Hello VRChat" }
```

- 响应：`{ "success": true, "text": "..." }`
- 若 `InboundAutoSendToVrc = true`，文本会**直接发送**到游戏；否则自动填充到主窗口输入框（不发送）。

#### 查询 / 设置动态变量

```http
GET  http://127.0.0.1:9002/api/variables
GET  http://127.0.0.1:9002/api/variables/set?name=status&value=挂机中&display=状态
POST http://127.0.0.1:9002/api/variables
Content-Type: application/json

{ "name": "status", "value": "挂机中", "displayName": "状态" }
```

变量可在"交互发送"页的常驻模板中以 `{status}` 形式引用，更新后模板实时重算。

#### 游戏内避让 / 恢复

```http
GET http://127.0.0.1:9002/api/chatbox/avoid?seconds=12   # 暂停常驻文本 12 秒（避让游戏内聊天）
GET http://127.0.0.1:9002/api/chatbox/pause?seconds=12    # 同上
GET http://127.0.0.1:9002/api/chatbox/resume              # 立即恢复常驻保活
```

### UDP OSC Inbound

向 `127.0.0.1:{InboundOscPort}` 发送标准 OSC 消息 `/chatbox/input`（`s` 类型字符串），软件会按与 HTTP 相同的逻辑处理。

> 安全提示：Inbound 仅绑定 `127.0.0.1`（本地回环），不会暴露到局域网；若要允许远程调用需自行在防火墙/代理层处理。

---

## 配置文件 `config.json`

配置文件位于程序根目录，启动时自动加载、改动后防抖写入。以下为常用字段（默认值见 `SettingsService.ResetToDefault()`）：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `WindowWidth` / `WindowHeight` / `WindowX` / `WindowY` | — | 主窗口尺寸与位置（自动持久化）。 |
| `IsAlwaysOnTop` / `IsMaximized` | `false` | 置顶 / 最大化。 |
| `NavOrder` | 7 项数组 | 导航项顺序。 |
| `Host` / `Port` | `127.0.0.1` / `9000` | OSC 发送到 VRChat 的地址与端口。 |
| `IsDirectSendEnabled` | `true` | 发送时是否直接上屏（否则仅填输入框）。 |
| `IsSoundEnabled` | `false` | 发送时是否播放提示音。 |
| `IsTypingEnabled` / `IsLiveTypingEnabled` / `IsEnterSendEnabled` / `IsClearOnSendEnabled` | 见默认值 | 打字动画 / 逐字上屏 / Enter 发送 / 发后清空。 |
| `IsPersistentTextEnabled` / `PersistentCustomText` | `false` / 模板 | 头顶常驻文本开关与模板。 |
| `InGameAvoidanceEnabled` / `InGameAvoidanceSeconds` | `true` / `12` | 游戏内输入自动避让。 |
| `TransientTextDurationSeconds` | `10` | 瞬态 `[文本]` 的有效时长。 |
| `IsHotkeyEnabled` / `CustomHotkeyString` | `true` / `Ctrl + Shift + C` | 全局唤醒快捷键。 |
| `IsImmersiveHotkeyEnabled` / `CustomImmersiveHotkeyString` | `true` / `Ctrl + Shift + Z` | 沉浸模式切换快捷键。 |
| `IsSpeechRecognitionEnabled` / `SpeechEngine` | `true` / `LiveCaptions` | 语音识别开关与引擎。 |
| `SpeechModelType` / `GpuUsageMode` | `base` / `Medium` | Whisper 模型规格与 GPU 负载策略。 |
| `SpeechLanguageTag` / `ChineseVariant` | `auto` / `Simplified` | 识别语言与中文输出偏好。 |
| `AudioInputDeviceId` | `""`（默认设备） | 麦克风设备；`process:VRChat` 表示仅监听游戏进程音频。 |
| `SpeechSliceDurationSeconds` | `10` | 语音切片最长时限。 |
| `IsTranslationEnabled` / `OllamaEndpoint` / `OllamaModel` | `false` / `http://127.0.0.1:11434` / `RogerBen/HY-MT2-1.8B:latest` | 翻译开关、Ollama 地址与模型。 |
| `TargetLanguage` / `OllamaPromptTemplate` | `中文 (Chinese)` / 模板 | 目标语言与翻译提示词。 |
| `ShowRecognizedText` / `ShowTranslatedText` / `ShowTranslationLatency` | `true` / `true` / `true` | 同屏字幕显示选项。 |
| `InboundEnabled` / `InboundHttpPort` / `InboundOscPort` / `InboundAutoSendToVrc` | `true` / `9002` / `9001` / `true` | 外部接口开关与端口。 |
| `IsTtsEnabled` / `TtsServerEndpoint` / `TtsServerPort` | `true` / `http://127.0.0.1:9880` | IndexTTS 服务开关与地址。 |
| `AutoStartTtsServer` / `TtsPythonExePath` / `TtsServerScriptPath` / `TtsModelName` | — | TTS 服务自动启动与脚本路径。 |
| `TtsVirtualMicDeviceId` / `TtsMonitorDeviceId` | — | 虚拟麦克风（推流）与监听设备 ID。 |
| `TtsOutputVolume` / `TtsMonitorVolume` | `1` / `0.8` | 推流 / 监听音量。 |
| `IsTtsAutoReadSentMessage` / `IsTtsAutoReadTranslation` | `true` / `false` | 自动朗读已发送 / 翻译。 |

> 提示：修改 `config.json` 后重启软件生效；也可在 UI 内调整，软件会自动回写该文件。

---

## 技术栈与依赖

| 类别 | 技术 |
| --- | --- |
| 界面框架 | **WinUI 3** + **Windows App SDK 1.8**（`Microsoft.WindowsAppSDK 1.8.260317003`） |
| 运行时 | **.NET 10**（`net10.0-windows10.0.26100.0`，最低目标 `10.0.17763.0`） |
| 部署形态 | Unpackaged Win32（`<WindowsPackageType>None</WindowsPackageType>`，自包含 WindowsAppSDK 运行时） |
| 语音识别 | `Whisper.net` 1.9.1（+ `Runtime.Cuda12.Windows` / `Runtime.Vulkan` 可选加速） |
| 音频采集 / 输出 | `NAudio` 3.1.0（`NAudio.Wasapi`，虚拟声卡推流） |
| 外部自动化 | `Interop.UIAutomationClient` 10.19041.0（游戏内输入检测） |

项目使用 `ImplicitUsings` + `Nullable` 启用，采用单文件 `.csproj`（无旧的 `*.csproj` + `App.xaml` 拆分）。

---

## 构建与运行

### 环境要求

- **Windows 10 / 11 64 位**（使用 Windows 11 实时字幕需 22H2+）。
- **.NET 10 SDK**（或对应运行时）。
- **Visual Studio 2022+** 安装"使用 Windows App SDK 的桌面开发"工作负载，或纯命令行 + `dotnet` CLI。
- 可选：**Ollama**（本地翻译）、**IndexTTS** 服务（TTS）、**VB-Cable** 等虚拟声卡（TTS 推流）。

### 从源码构建

```bash
git clone <仓库地址>
cd VrcChatboxDemo
dotnet build -c Release
```

### 运行（调试）

```bash
dotnet run -c Debug
```

或直接使用仓库内的 `run.bat`（优先启动已构建的 `bin\Debug\...\VrcChatboxDemo.exe`，否则 `dotnet run --no-build`）。

### 独立发布（Self-contained）

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

输出位于 `./publish`，包含完整运行时，可在**未安装 .NET** 的机器上直接运行 `VrcChatboxDemo.exe`。

> 发布目标框架为 `win-x64`；若需 `x86` / `ARM64`，可在 `.csproj` 的 `<Platforms>`（`x86;x64;ARM64`）与 `-r` 参数中对应调整。

### 首次使用前置配置

1. **VRChat OSC**：游戏内圆盘菜单 → Options → OSC → 开启。默认发送 `127.0.0.1:9000`。
2. **翻译**：本机安装并启动 Ollama，拉取一个翻译模型（如 `RogerBen/HY-MT2-1.8B`），在"翻译配置"页点"刷新模型"后选择。
3. **TTS**：部署 IndexTTS 的 `tts_server.py` 并填好 Python 路径 / 模型名；如需队友听到，安装 VB-Cable 并在"TTS 语音"页指定虚拟麦克风设备。

---

## 常见问题排查

1. **VRChat 收不到任何消息 / OSC 连接失败**
   - 确认 VRChat 游戏内 OSC 开关已开启。
   - 默认发送端口 `9000`、地址 `127.0.0.1`；若改过端口需保持两端一致（`config.json` 的 `Host` / `Port`）。
   - 查看"网络日志"页确认 OSC 收发记录。

2. **Windows 11 实时字幕无反应**
   - 首次使用需确保系统已下载对应离线语言包（设置 → 时间和语言 → 语音 / 语言包）。
   - 若要监听自己的麦克风，在系统实时字幕窗口设置中勾选"包含麦克风音频"。
   - 检查"语音识别"页引擎是否选为 `LiveCaptions`，且"隐藏原生字幕窗口"符合预期。

3. **Whisper 识别卡顿 / 无 GPU 加速**
   - 若未检测到 NVIDIA CUDA 或 Ollama 内置 CUDA 库，软件会在"语音识别"页弹出告警，建议改用 Windows 11 实时字幕，或在 GPU 高负载时把"GPU 负载策略"调为"低负载"。

4. **翻译模型下拉框 / 刷新按钮无法点击**
   - 这是已知的 XAML 布局 bug 修复项（详见代码提交历史）：外层 Grid 的 `RowDefinition` 数量与 `Grid.Row` 不匹配会导致透明 TextBlock 覆盖控件。已通过补全 `RowDefinition` + 给状态文本加 `IsHitTestVisible="False"` 修复。

5. **快捷键冲突 / 不生效**
   - 在"快捷热键"页重新绑定全局唤醒与沉浸切换快捷键；确认没有其他软件占用同一组合键。

6. **软件崩溃 / 无响应**
   - 崩溃信息会写入程序根目录 `crash.log`。可据此定位（如 TTS 脚本路径错误、设备 ID 失效等）。
   - 软件为单实例：重复启动会自动唤醒已运行实例并退出。

---

## 项目结构

```
VrcChatboxDemo/
├─ App.xaml(.cs)                     # 应用入口：单实例互斥、崩溃日志、双窗口生命周期
├─ MainWindow.xaml(.cs)              # 主窗口：NavigationView + 拖拽重排 + 置顶/沉浸按钮 + 系统托盘
├─ Views/
│  ├─ ChatPage.xaml(.cs)             # 消息发送页（同屏字幕 + 输入 + TTS 试听）
│  ├─ ImmersiveWindow.xaml(.cs)      # 透明沉浸悬浮窗（穿透点击 + 拖拽）
│  ├─ *SettingsPage.xaml(.cs)        # 7 个设置页（语音/翻译/TTS/热键/交互/网络日志）
│  └─ Sections/                      # 各设置页复用的卡片式 UserControl
│     ├─ SpeechSection / TranslationSection / TtsSection
│     ├─ HotkeySection / InteractionSection / NetworkLogSection
├─ Services/
│  ├─ Common/                        # AppConfig / TrayIconService / TransparentBackdrop
│  ├─ Input/                         # HotkeyService（Win32 低层钩子 + 封装）
│  ├─ Osc/                           # OscService（UDP 发送）+ Inbound（HTTP/UDP 接收）
│  ├─ Settings/                      # SettingsService（聚合根：属性/事件/OSC 发送/恢复默认）
│  ├─ Speech/                        # SpeechService(Whisper) / LiveCaptionsService / 音频设备与进程捕获
│  ├─ Translation/                   # OllamaService
│  ├─ Template/                      # PersistentTextService / VariableService / VrcInGameGuard
│  └─ Tts/                           # TtsService（IndexTTS 流式推流 + 虚拟声卡）
├─ Models/                           # 本地 Whisper 模型文件（*.bin）
├─ Assets/                          # 应用图标等资源
├─ config.json                      # 运行时配置（自动持久化）
├─ app.manifest                     # 应用清单（OS 兼容 + PerMonitorV2 DPI）
├─ VrcChatboxDemo.csproj            # 单文件项目配置
└─ run.bat                          # 一键运行脚本
```

---

## 开源协议

本项目采用 **MIT 许可证** 开源。详见仓库内 `LICENSE` 文件。
