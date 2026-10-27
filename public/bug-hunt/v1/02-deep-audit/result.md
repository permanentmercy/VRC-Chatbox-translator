# 阶段结果产物：深度复审与残留缺陷修复 (02-deep-audit)

- **功能模块**：`bug-hunt`
- **版本**：`v1`
- **阶段**：`02-deep-audit`
- **执行时间**：2026-10-27
- **结果状态**：已完成 (全部验证通过)

---

## 1. 阶段概述

本阶段采用多智能体协同审查与测试驱动验证的方式，针对核心服务、语音与转写引擎、音频采集与解码、TTS 引擎、常驻模板渲染及系统托盘/生命周期管理等模块展开了全量深度代码审计。

重点排查并消除了并发竞态、异步状态回写冲突、模型与资源泄漏、网络流超时悬挂、硬编码绝对路径以及边界格式解码等深层缺陷。累计补充并执行了 9 大独立确定性回归测试套件，共计 **114 项自动化测试全部通过**，主工程 `VrcChatboxDemo.csproj` 实现 **0 错误编译通过**。

---

## 2. 缺陷修复清单与技术方案

### 2.1 语音引擎与设置层意图丢失 (SpeechModelType A-B-A)
- **问题定位**：`Services/Settings/SettingsService.cs`
- **缺陷现象**：当当前模型为 `tiny` 时，UI 快速切换至 `base`，在模型加载未完成时立即切回 `tiny`。旧实现通过对比尚未更新的底层 `CurrentModelType` 判断无变更而跳过排队，导致最终加载了 `base`，违背用户最终选择。
- **修复方案**：`SpeechModelType` 的 setter 将用户规范化目标直接登记入配置，并排入 `SpeechService.SwitchModelAsync`。底层服务通过已有的内部互斥锁执行安全去重与最新意图覆盖。

### 2.2 自动语种检测音频格式解码缺失 (PCM24 / PCM32 / Extensible)
- **问题定位**：`Services/Speech/LiveCaptionsLanguageAutoSwitcher.cs`
- **缺陷现象**：输入采样解码仅支持 float 与 16-bit PCM；遇 24-bit PCM 设备时所有样本解码为 0 导致静默；遇 32-bit 整数 PCM 时因 `BitsPerSample == 32` 被误当 IEEE float 解码，导致音频严重失真。
- **修复方案**：引入 `WaveFormatExtensible` 与 `SubFormat` 判别；新增 24-bit 符号扩展算术右移解码与 32-bit 容器整数归一化计算，并对 48kHz 输入采用 3:1 FIR 抗混叠低通滤波重采样。

### 2.3 LiveCaptions 重复语句与字幕滚屏消费锚点失效
- **问题定位**：`Services/Speech/LiveCaptionsService.cs`
- **缺陷现象**：原实现使用 `LastIndexOf` 查找已提交文本，当用户连续重复同一短句（如连续说“你好。你好。”）时，定位会跳跃至最新未消费的短句位置，导致后续句子漏上屏。
- **修复方案**：改用精确水位与已消费字符位置追踪，结合输入法组合窗口生命周期重定位，确保中英文重复短句与动态滚动下均不发生字幕丢失或回退。

### 2.4 系统托盘退出绕过 Ollama 显存卸载
- **问题定位**：`Services/Settings/SettingsService.Lifecycle.cs`、`MainWindow.xaml.cs`
- **缺陷现象**：Ollama 显存释放原本仅注册在主窗口的 `Closed` 事件中。用户通过托盘右键“退出”时直接调用清理并终止进程，导致驻留显存无法释放。
- **修复方案**：将 Ollama 显存卸载（带有限总时间预算和超时取消的 `keep_alive: 0` 调用）纳入统一的 `ShutdownAndKillProcess` 退出清理生命周期，主窗关闭与托盘退出共用幂等清理。

### 2.5 TTS 停止与流式响应异步状态回写竞争
- **问题定位**：`Services/Tts/TtsService.cs`
- **缺陷现象**：在流式合成等待响应头或正文读取期间点击停止引擎，`StopManagedServer` 先将状态设为 `Stopped`，被取消的旧请求在异步 continuation 或 `finally` 中将状态改回 `Ready`，导致 UI 禁用启动按钮且无法重启。
- **修复方案**：为合成生命周期引入代际（Generation）与序列号（Sequence）机制，所有状态变更必须验证当前代际；若请求已被取消或已被停止，旧请求严禁修改全局服务状态。

### 2.6 TTS 流式正文缺乏超时保护
- **问题定位**：`Services/Tts/TtsService.cs`
- **缺陷现象**：使用 `HttpCompletionOption.ResponseHeadersRead` 后，`HttpClient.Timeout` 在标头解析后不再对正文流读取生效；若服务端返回 200 标头后连接卡死，播放队列会被永久占用。
- **修复方案**：在 `ReadStreamBodyAsync` 中对每个 PCM 分块读取建立带超时限制的 Linked Token Source 与空闲超时门禁，超时自动释放 HTTP 响应与播放队列。

### 2.7 常驻文本模板分隔符与标点误清洗
- **问题定位**：`Services/Template/PersistentTextService.cs`
- **缺陷现象**：`PrepareTemplateForRender` 在移除过期瞬态块时，执行了全局正则替换，将普通 URL（如 `https://example.com/a -- b,,c / d`）中的 `--`、`,,`、` / ` 误改写为 ` | `，并截除了文本首尾的字面连字符。
- **修复方案**：将分隔符清洗严格限制在过期块占位符（`\uE001`）直接相邻区域；无过期块或普通文本内容保留原始字面量，不再执行全局破损替换。

### 2.8 动态变量连字符与别名对称性支持
- **问题定位**：`Services/Template/VariableService.cs`
- **缺陷现象**：`VariableRegex` 正则未包含 `-`，导致 `{heart-rate}` 等带连字符的外部变量无法匹配与渲染；`UsesVariable` 别名检测单向生效，在检查 `{language}` 是否使用 `{字幕语言}` 时返回 false。
- **修复方案**：在变量名匹配正则中加入 `\-`；提供双向规范化名称映射 `CanonicalizeVariableName`，统一处理内置变量与其全量中英文别名。

### 2.9 消除硬编码项目绝对路径
- **问题定位**：`Services/Common/TrayIconService.cs`、`Services/Speech/SpeechService.Model.cs`、`Services/Speech/SpeechService.Transcribe.cs`
- **缺陷现象**：代码中存在写死的 `H:\program\chatbox\VrcChatboxDemo\...` 绝对路径，在其他环境部署或移动目录后导致资源加载失败。
- **修复方案**：统一采用 `AppContext.BaseDirectory` 与 `Environment.CurrentDirectory` 相对路径解析，完全消除源码中的机器特定硬编码绝对路径。

---

## 3. 验证与回归测试结果

所有修复项均通过了自动化测试验证：

| 测试套件项目 | 覆盖功能模块 | 用例数 | 结果 |
| :--- | :--- | :---: | :---: |
| **AudioRegression** | Whisper 模型意图队列、PCM 16/24/32-bit 及 Extensible 音频解码、FIR 滤波 | 25 | **25/25 PASS** |
| **CaptionRegression** | LiveCaptions 滚动字幕定位、去重消抖、中英文重复句与消费水位 | 21 | **21/21 PASS** |
| **ConfigRegression** | 配置持久化意图与防抖落盘契约 | 4 | **4/4 PASS** |
| **ExitRegression** | 托盘与窗口退出 Ollama 显存卸载、超时保护、幂等清理生命周期 | 11 | **11/11 PASS** |
| **GuardRegression** | 游戏内输入守护进程判定、Windows 输入法宿主前台关联矩阵 | 7 | **7/7 PASS** |
| **SpeechRegression** | Whisper / LiveCaptions 启停回滚、引擎切换、语言 A-B-A 切换 | 9 | **9/9 PASS** |
| **TemplateRegression** | 常驻文本模板瞬态块生命周期、URL 保持、变量别名对称性、表情代理对截断 | 15 | **15/15 PASS** |
| **TtsRegression** | TTS 停止代际隔离、流式正文超时中断、播放互斥锁释放 | 15 | **15/15 PASS** |
| **UiRegression** | 音频播放设备枚举重构、Prompt 配套音色自动跟踪与对齐 | 7 | **7/7 PASS** |
| **总计** | **全系统核心功能与边界回归** | **114** | **114/114 全部通过 (0 失败)** |

### 主工程编译构建验证
```
dotnet build VrcChatboxDemo.csproj --no-restore -v minimal
已成功生成: 0 个错误，20 个已过时 API 警告 (NAudio WasapiOut 提示，非错误)。
```

---

## 4. 结论

本阶段的所有审查目标均已达成：
1. 潜在的并发竞态与状态回写缺陷已全部闭环修复；
2. 边界条件（多格式音频、异常网络流、标点/URL 字符处理）均获得安全处理；
3. 硬编码绝对路径已完全清除；
4. 114 个自动化测试和主项目构建全部通过，无残留已知问题。
