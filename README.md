# VRChat 智能双语字幕与输入助手 (VRC-Chatbox-translator)

<p align="center">
  <img src="Assets/AppIcon.ico" width="96" height="96" alt="App Icon" />
</p>

<p align="center">
  <strong>专为 VRChat 玩家打造的现代 Fluent 风格双语字幕、本地 AI 翻译与 OSC 聊天助手</strong>
  <br />
  <em>零云端依赖 · 本地离线隐私 · 极速实时流式 · 硬件级透明穿透悬浮窗</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-purple.svg" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Windows%20App%20SDK-WinUI%203-blue.svg" alt="WinUI 3" />
  <img src="https://img.shields.io/badge/Platform-Windows%2011%20x64-0078D6.svg" alt="Platform" />
  <img src="https://img.shields.io/badge/AI-Whisper%20%7C%20Ollama-orange.svg" alt="AI" />
  <img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License" />
</p>

---

## 🌟 核心特性

### 1. 🎙️ 双引擎本地语音识别 (Speech Recognition)
- **Windows 11 原生实时字幕联动 (LiveCaptions.exe)**：
  - 基于 Windows UIAutomation 无缝捕获系统字幕流。
  - **0 显存占用**：绝不与 VRChat 或 Unity 争抢 GPU 算力，游戏保持高帧率丝滑运行。
  - 智能断句提交与流式假说输出，支持双逗号拆句与标点分段。
- **本地 OpenAI Whisper AI 离线引擎**：
  - 支持 `tiny`、`base`、`small` 等多种规格离线神经网络模型。
  - 自动检测并启用 NVIDIA CUDA 驱动级硬件加速，自动回退 Vulkan / CPU 推理。
  - 类似 Windows 字幕的实时假说流式出字与后文自动纠偏。
  - 内置简繁中文转换（Simplified / Traditional）。

### 2. 🤖 本地大语言模型实时翻译 (Ollama AI Translation)
- 支持接入本地运行的 [Ollama](https://ollama.ai/) 翻译大模型（如 `HY-MT`、`Qwen2.5`、`DeepSeek` 等）。
- 实时多语言互译：中、英、日、韩、俄、法、德、西。
- **识别/翻译直接发送至 VRChat**：语音识别或 AI 翻译定稿后直接提交至头顶气泡（无打字动画），**完全不污染或抢占软件主输入框**，用户草拟中的文字不受丝毫影响。
- 支持高度自定义提示词模版（Prompt Template）与目标语言占位符。

### 3. 💬 VRChat OSC 深度联动与打字体验
- **打字预览与输入法防穿帮**：
  - 实时打字预览（按键打字过程实时在头顶气泡更新，回车最终确认）。
  - **输入法状态隔离**：严密保护中文输入法拼音组字阶段，汉字候选词未上屏前绝不向游戏发送拼音字母。
- **头顶常驻自定义文本**：
  - 支持在角色头顶常驻指定文本内容（如名片、状态提示、交友签名）。
  - 主动说话或打字时优先显示新消息，气泡消失后自动恢复常驻文本。
- **速率防抖与限流保护**：
  - 严格限制 OSC 消息发送间隔（>= 110ms），完美符合 VRChat 9包/2秒的内部限流规则，杜绝丢包。

### 4. 🪟 沉浸式透明悬浮窗 (Immersive Floating UI)
- **硬件级透明穿透**：基于 DWM 玻璃合成通道注入，结合动态 GDI 区域计算，使浮层空白处完全硬件级穿透点击底层的游戏窗口或桌面。
- **灵活拖拽与交互**：
  - 支持右侧抓手拖动、字幕卡片拖动，或全局按住 `Alt + 左键` / `鼠标中键` 任意位置拖拽移动。
  - 全局唤醒/沉浸模式热键一键呼出与隐藏（默认 `Ctrl + Shift + C` / `Ctrl + Shift + Z`，支持自由重绑定）。

### 5. 🔌 开放 Inbound API (HTTP / OSC)
- 内置 HTTP REST 服务与 OSC Inbound 监听端口。
- 支持由外部自动化脚本、第三方翻译插件或虚拟主播工具向本程序或 VRChat 推送文本。

---

## 🏗️ 架构与模块化设计

项目采用高内聚、低耦合的模块化设计，原核心主文件保留生命周期管理，具体业务逻辑按功能拆分至功能文件夹：

```
VrcChatboxDemo/
├── Services/                      # 核心服务层
│   ├── SettingsService.cs         # 配置单例与全局调度
│   ├── Settings/                  # SettingsService 功能拆分模块
│   │   ├── SettingsService.Osc.cs       # 实时打字、心跳与 OSC 互斥流控
│   │   ├── SettingsService.Speech.cs    # 字幕队列(FIFO)、断句与直接发送
│   │   └── SettingsService.Lifecycle.cs # 音频设备热切、全局热键与安全退出
│   ├── SpeechService.cs           # Whisper 本地 AI 语音识别调度
│   ├── Speech/                    # SpeechService 功能拆分模块
│   │   ├── SpeechService.Transcribe.cs  # 实时人声静音检测与 GPU 队列循环
│   │   ├── SpeechService.Model.cs       # Whisper 模型下载与动态装载
│   │   ├── SpeechService.Cuda.cs        # CUDA 运行时多路径动态探测与注入
│   │   ├── SpeechService.Chinese.cs     # 简繁中文系统级转换
│   │   └── LiveCaptionsService.Win32.cs # Win11 实时字幕窗口句柄与进程唤起
│   ├── LiveCaptionsService.cs     # Windows 11 字幕 UIAutomation 读取
│   ├── OllamaService.cs           # 本地 Ollama 翻译客户端
│   ├── OscService.cs              # VRChat OSC UDP 协议包编码与网络发送
│   ├── PersistentTextService.cs   # 头顶常驻文本循环守护线程
│   ├── HotkeyService.cs           # 全局键盘钩子 (RegisterHotKey)
│   └── InboundService.cs          # 外部 HTTP / OSC 接收服务端
│
└── Views/                         # UI 视图层 (WinUI 3 / XAML)
    ├── ChatPage.xaml              # 桌面主聊天与字幕主屏
    ├── SettingsPage.xaml          # 核心功能与综合参数设置
    ├── TranslationSettingsPage.xaml # Ollama AI 翻译配置页面
    ├── SpeechSettingsPage.xaml    # 语音识别引擎与音频设备选择页面
    ├── InteractionSettingsPage.xaml # 常驻文本与打字交互配置
    ├── HotkeySettingsPage.xaml    # 快捷键自定义页面
    ├── NetworkLogSettingsPage.xaml # OSC 网络收发日志监视器
    ├── ImmersiveWindow.xaml       # 沉浸式透明全屏悬浮窗
    └── Immersive/                 # ImmersiveWindow 功能拆分模块
        ├── ImmersiveWindow.Win32.cs     # DWM 窗口透明与 Subclass 消息拦截
        ├── ImmersiveWindow.HitTest.cs   # 动态命中测试与硬件级点击穿透
        └── ImmersiveWindow.Dragging.cs  # 多点/抓手鼠标拖拽移动
```

---

## 💻 系统需求

- **操作系统**：Windows 11（推荐 22H2 及以上版本，使用 Windows 原生实时字幕功能必须；Windows 10 支持 Whisper 本地模型模式）。
- **运行环境**：.NET 10.0 Runtime / SDK。
- **GPU（可选）**：NVIDIA 独立显卡（若使用 Whisper GPU 加速或本地 Ollama 模型）。
- **VRChat**：需在游戏中开启 OSC 功能（Action Menu -> Options -> OSC -> Enabled）。

---

## 🚀 编译与构建

### 1. 源码编译 (Debug / Release)
```bash
git clone git@github.com:permanentmercy/VRC-Chatbox-translator.git
cd VRC-Chatbox-translator
dotnet build -c Release
```

### 2. 独立免安装打包发布 (Self-contained)
```bash
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```
发布产物位于 `./publish` 目录，包含所有必需的 .NET 运行库和资源索引，开箱即用。

---

## 📖 使用指南

1. **启动程序**：启动后程序常驻系统托盘，默认呼出主窗口。
2. **选择语音识别模式**：
   - **Windows 11 原生字幕（推荐）**：点击设置中的“启用 Windows 11 原生实时字幕”，系统将在后台自动唤醒 `LiveCaptions.exe` 并将其窗口隐匿在后台。
   - **Whisper 本地模型**：选择需要的模型规格（如 `tiny` / `base`），首次使用将自动下载权重文件至本地缓存。
3. **配置 AI 翻译（可选）**：
   - 安装并启动 [Ollama](https://ollama.ai/)，拉取翻译模型（例如 `ollama run RogerBen/HY-MT2-1.8B:latest`）。
   - 在本软件“AI 翻译”页面勾选“启用 Ollama AI 实时翻译”，并选择翻译目标语言。
4. **游戏内显示**：
   - 勾选 **“识别/翻译直接发送到 VRChat”** 后，您说出的每一句话在定稿后将立即浮现于您的 VRChat 角色头顶！
   - 按下 `Ctrl + Shift + Z` 呼出沉浸式透明悬浮窗，在游戏画面上方实时悬浮展示双语字幕和打字输入框。

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源发布。欢迎提交 Issue 与 Pull Request！
