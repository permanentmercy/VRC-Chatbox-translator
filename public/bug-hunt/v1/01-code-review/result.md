# 缺陷排查与修复结果

## 当前状态

已完成十轮全项目静态审查，并修复 85 处有代码证据的缺陷。最新构建：`dotnet build .\VrcChatboxDemo.csproj --no-restore --output .\artifacts\bugfix-build -p:UseAppHost=false` → **0 错误，20 条既有 CS0618 警告**（均来自 `TtsService.cs` 的过时 `WasapiOut` API，非本轮引入）。`git diff --check` 通过。

本轮验证仅限编译与静态推理；未启动完整应用做手动回归（启动会连接 VRChat OSC、Ollama、IndexTTS 与系统实时字幕，环境不具备稳定复现条件）。

## 已修复缺陷

### 导航与 UI 状态

1. **首次启动/重置后 TTS 导航项落到末尾**
   - 位置：`Services/Common/AppConfig.cs` 默认 `NavOrder`。
   - 原因：默认列表缺少 `"tts"`，`ApplyNavOrder` 的补全逻辑把它追加到末尾，与 XAML 初始顺序（翻译配置之后、快捷热键之前）不一致。
   - 修复：默认列表补入 `"tts"`。

2. **启动时选中项与内容页不一致**
   - 位置：`MainWindow.xaml.cs` `InitializeNavItems`。
   - 原因：先选中 `MenuItems` 第一项，随后无条件 `Navigate(ChatPage)`；用户把语音/翻译排到第一时，高亮项与内容页不一致。
   - 修复：新增 `GetPageTypeForTag` / `NavigateToTag`，启动时导航到第一项对应页面。

3. **重排导航后选中项与内容页脱节**
   - 位置：`MainWindow.xaml.cs` `MoveNavItem` / `EndNavDrag`。
   - 原因：在 `_isReorderingNav = true` 期间把 `SelectedItem` 改成被移动项，`SelectionChanged` 被抑制，内容页不切换。
   - 修复：重排前记录原选中项，插入后恢复；重排只改变顺序，不切换页面。

4. **恢复默认排序后选中项丢失**
   - 位置：`MainWindow.xaml.cs` `ResetNavOrder`。
   - 原因：`ApplyNavOrder` 清空 `MenuItems`，`SelectedItem` 变为 null，未恢复。
   - 修复：记录原选中 tag，重置后恢复选中并导航；无原选中时选第一项。

5. **TTS 自动化卡片在最小内容宽度下横向溢出**
   - 位置：`Views/Sections/TtsSection.xaml` 两列 `340 + 340 + 20`。
   - 原因：共享内容列最小宽度 700 DIP，卡片内边距 18×2，内部可用约 664 DIP；700 的固定网格溢出。
   - 修复：两列改为 `320 + 320`。

6. **清空常驻文本后开关/状态仍显示开启**
   - 位置：`PersistentTextService.ClearAsync`、`InteractionSection.ClearPersistentTextButton_Click`。
   - 原因：只清空 `CustomText`，未把 `IsEnabled` 置 false；UI 仍显示“常驻保活中”，配置也仍为开启。
   - 修复：`ClearAsync` 同步关闭服务；UI 清空时同步关闭开关与 `IsPersistentTextEnabled`。

7. **沉浸窗 Alt+F4 关闭后无法再进入沉浸模式**
   - 位置：`Views/ImmersiveWindow.xaml.cs`。
   - 原因：沉浸窗没有拦截 `AppWindow.Closing`；真正关闭后，切换沉浸模式时 `AppWindow.Show()` 抛异常，主窗已隐藏，只剩托盘。
   - 修复：拦截 Closing，取消关闭并隐藏窗口。

### 语音识别与实时字幕

8. **LiveCaptionsService.Dispose 竞态**
   - 位置：`Services/Speech/LiveCaptionsService.cs`。
   - 原因：`Dispose` 里 `_ = StopAsync()` 后立即 `_sessionLock.Dispose()`；StopAsync 真正执行时会对已释放信号量 `WaitAsync/Release`，可能抛 `ObjectDisposedException`，LiveCaptions 进程也可能未被终止。
   - 修复：提取同步 `StopCore()`；`StopAsync` 在锁内调用；`Dispose` 同步停止并清理进程，不再在并发 Release 前释放信号量。

9. **Whisper 模型重复下载可能保留尾部脏数据**
   - 位置：`SpeechService.Model.cs` `EnsureModelPathAsync`。
   - 原因：`File.OpenWrite` 不截断已有文件；上次下载中断留下半截文件时，覆盖写入会保留尾部旧字节，模型损坏。
   - 修复：写入 `.download` 临时文件，成功后 `File.Move(..., overwrite: true)`；失败清理临时文件。

10. **音频设备下拉重建时把已保存设备重置为“系统默认”**
    - 位置：`Views/Sections/SpeechSection.xaml.cs` `AudioDeviceComboBox_SelectionChanged`。
    - 原因：`Items.Clear()` 使 `SelectedIndex = -1`，旧逻辑把 `idx <= 0` 当作用户选择“系统默认”，调用 `SwitchAudioDeviceAsync(null)`。
    - 修复：`idx < 0` 直接返回；只有 `0` 才表示用户选择系统默认。

11. **引擎默认值 UI 与实际不一致**
    - 位置：`SpeechSection.InitSpeechEngine`。
    - 原因：配置为空时 UI 默认选 Whisper，而 `SettingsService.SpeechEngine` getter 默认 LiveCaptions。
    - 修复：UI 默认改为 LiveCaptions。

12. **并发切换语音引擎导致 Start/Stop 交错**
    - 位置：`SettingsService.Speech.cs` `ApplySpeechStateAsync`。
    - 原因：引擎切换/开关可并发调用，旧引擎刚停、新引擎又被另一条任务停掉，最终状态不确定。
    - 修复：增加 `_speechStateLock` 串行化所有启停请求。

13. **自动语种切换失败仍写入配置**
    - 位置：`LiveCaptionsLanguageAutoSwitcher.cs`。
    - 原因：忽略 `SwitchLanguageAsync` 返回值，切换失败也调用 `SetLiveCaptionsLanguageCodeSilent`，配置显示新语言、实际仍是旧语言。
    - 修复：仅在返回 true 时同步配置；失败更新状态提示。

14. **Ear 模型在 UI 线程同步加载造成卡顿**
    - 位置：`EarLanguageDetector.InitializeAsync`。
    - 原因：`WhisperFactory.FromPath` + `Build()` 读取上百 MB 权重，在 UI 线程执行会冻结界面数秒。
    - 修复：工厂与处理器构建放入 `Task.Run`。

### TTS 与音频播放

15. **`X-Sample-Rate` 解析失败把采样率置 0**
    - 位置：`TtsService.SynthesizeAsync`。
    - 原因：`int.TryParse(..., out sr)` 失败时把 `sr` 覆盖为 0，而不是保留默认 24000。
    - 修复：先解析到临时变量，仅在 `> 0` 时覆盖；同时用 `using` 释放响应。

16. **停止播放后仍返回成功**
    - 位置：`TtsService.PlayStreamAsync`。
    - 原因：取消时已播放块数可能 >0，`startedPlayback` 为 true，方法仍返回 true；UI 误报“推流完成”。
    - 修复：返回前检查 `token.IsCancellationRequested`，取消则返回 false。

17. **取消流式合成仍上报“合成完成”**
    - 位置：`TtsService.SynthesizeStreamAsync`。
    - 原因：`ReadAsync` 取消后 `break`，随后照常执行完成日志与 `SynthesisCompleted`。
    - 修复：记录 `canceled`，取消时设置“流式合成已取消”并 `yield break`。

18. **停止引擎被误报为“启动超时”**
    - 位置：`TtsService.StartManagedServerAsync` 启动轮询。
    - 原因：`StopManagedServer` 先置空 `_serverProcess` 再 `Dispose` 进程；轮询里 `proc.HasExited` 在 Dispose 后抛异常被吞，`hasExited` 恒 false，循环跑满 35 秒后报错，且一直持有 `_serverStartLock`。
    - 修复：每轮先判断 `_serverProcess != proc` 立即退出；超时分支再确认一次。

19. **物理 “Line Out” 被误判为虚拟声卡**
    - 位置：`TtsService.IsVirtualAudioDevice`。
    - 原因：`lower.Contains("line ")` 命中 `Line Out (Realtek Audio)`，导致推流目标被强制指向物理输出，VRChat 收不到 TTS。
    - 修复：移除宽泛的 `"line "`，只保留 cable/virtual/voicemeeter/vb-audio/blackhole/loopback 等白名单。

20. **没有输出端点时仍返回“成功”**
    - 位置：`TtsService.PlayStreamAsync`。
    - 原因：无 player 时仍累加 `totalBufferedBytes`，达到预缓冲阈值就把 `startedPlayback` 置 true；音频块被静默丢弃。
    - 修复：初始化后检查 `playerSame/playerMic/playerMon` 是否至少一个存在，否则记录原因并返回 false。

21. **多声道（5.1/7.1）输出设备格式不匹配导致整段无声**
    - 位置：`TtsService.PlayStreamAsync` / `PlayToSingleEndpointAsync`。
    - 原因：只处理 `mixFormat.Channels == 2` 的单→双复制；>2 声道时仍是单声道格式，共享模式 `WasapiOut.Init` 可能失败。
    - 修复：新增 `MatchDeviceChannelCount`，>2 声道用 `MultiplexingSampleProvider` 把单声道复制到前 L/R，其余静音。

22. **打开 TTS 页会覆盖已保存的音频设备**
    - 位置：`Views/Sections/TtsSection.xaml.cs` `LoadAudioDevices`。
    - 原因：加载时无条件回写 `TtsVirtualMicDeviceId` / `TtsMonitorDeviceId`，并强制把物理设备纠偏为虚拟卡；设备暂时不可用时永久丢失原选择。
    - 修复：加载只做展示，不写配置；已保存设备优先（即使不是虚拟卡）；只有从未保存/找不到时才推荐虚拟卡或默认设备；新增 `_isLoadingDevices` 守卫，避免程序化选中触发持久化。

23. **TTS 端点端口与托管服务端口不同步**
    - 位置：`TtsSection.ServerEndpointTextBox_TextChanged`。
    - 原因：UI 只编辑 `TtsServerEndpoint`，托管启动却用 `TtsServerPort`；改成 `:9000` 后服务端仍监听 9880，请求必然失败。
    - 修复：本机端点解析到显式端口时同步 `TtsServerPort`（排除默认端口，避免输入过程误写 80）。

24. **按端口清理进程可能误杀无关应用**
    - 位置：`TtsService.KillProcessesByPort`。
    - 原因：`netstat | findstr :{port}` 会匹配远端端口/子串（80 命中 8080），且旧解析收集所有行 PID。
    - 修复：逐行校验“本地端口 == 目标端口”且状态为 `LISTENING`；只终止进程名包含 `python` 的 TTS 残留，其他占用者跳过并记录日志。

### OSC / 设置 / 模板

25. **Inbound 监听失败仍报告“已就绪”**
    - 位置：`InboundService.Start` / `StartHttpServer` / `StartOscUdpServer`。
    - 原因：`_isRunning` 在启动监听前就置 true；两个监听器都失败时仍发“已就绪”，配置也会持久化为开启。
    - 修复：监听函数返回 bool；至少一个成功才置 `_isRunning = true`；全部失败清理 CTS 并报告失败；部分成功给出明确提示。

26. **Inbound 开关与实际状态不一致**
    - 位置：`NetworkLogSection.InboundSwitch_Toggled`。
    - 原因：Start 失败后开关仍保持开启。
    - 修复：Start 后若 `IsRunning` 为 false，把开关拨回关闭。

27. **中文变量名在瞬态块中失效**
    - 位置：`PersistentTextService.PrepareTemplateForRender` / `SubmitAsync`。
    - 原因：变量正则只允许 ASCII，`[{字幕语言}]` 被当成纯文本块，只受手动提交时限控制。
    - 修复：两处正则加入 CJK 范围。

28. **`PlayStreamAsync` 取消/无端点修复的配套日志与状态**
    - 说明：第 16、20 项修复后，`SettingsService.SpeakTextStreamAsync` 不再把取消/无输出误记为“推流完成”；`TtsSection` 会显示“推流未完成或已取消”。

29. **连续有声时语音缓冲区无上限增长**
    - 位置：`SpeechService.Transcribe.cs` `ProcessLiveAudioLoopAsync`。
    - 原因：`maxSpeechBytes` / `maxSpeechMs` 只在静音分支检查；VAD 阈值很低，背景音乐/连续说话时 `isVoice` 几乎恒 true，`speechStream` 永不清空，假说快照 O(n²) 复制，最终卡死且不提交。
    - 修复：抽出 `CommitSpeechSegment()`；有声分支达到切片上限时也强制提交并清空。

30. **关闭语音时无法取消“启动中”的引擎**
    - 位置：`SpeechService.cs` `StartAsync` / `StopAsync`。
    - 原因：`_listeningCts` 在模型加载之后才创建；`StopAsync` 1 秒超时后 `StopInternalAsync` 因 `!_isListening && _capture==null` 直接返回，StartAsync 继续跑完并把 `IsSpeechRecognitionEnabled` 写回 true。
    - 修复：新增 `_startCts`，StopAsync 先取消；StartAsync 在模型加载后、开始录音前/后检查取消并回滚。

31. **启动过程中切换模型把 `_processor` 置 null → 监听中永久零字幕**
    - 位置：`SpeechService.Model.cs` `SwitchModelAsync`。
    - 原因：两次独立读 `_isListening`；启动窗口内读到 false 就不建 processor 并把它置 null，StartAsync 随后把 `_isListening=true`，转写方法见 `proc == null` 静默返回。
    - 修复：切换模型时始终构建 processor；不再把 `_processor` 置 null。

32. **切换识别语言与切换模型竞争工厂释放**
    - 位置：`SpeechService.Model.cs` `SwitchLanguageAsync`。
    - 原因：语言切换不取 `_modelSwitchLock`，可能对已被释放的 `WhisperFactory` 调 `CreateBuilder()`，或安装旧工厂产出的 processor。
    - 修复：语言切换也取 `_modelSwitchLock`，在锁内读取工厂、构建并交换 processor。

33. **进程隔离捕获异常停止被吞掉**
    - 位置：`ProcessLoopbackCapture.cs` `OnRecorderRecordingStopped`。
    - 原因：条件写成 `if (!_isRecording)`，录制中的异常停止永远不转发；`_isProcessFound` 不复位，watcher 已退出，UI 一直显示“已连接”但永久静音。
    - 修复：新增 `_isStopping` 去重；录制中停止时复位 `_isProcessFound` 并向上抛出带异常信息的 `RecordingStopped`。

34. **LiveCaptions 重启被误判为用户关闭语音**
    - 位置：`LiveCaptionsService.RestartAsync`、`LiveCaptionsService.Language.cs`。
    - 原因：重启时触发 `RunningStateChanged(false)`，SettingsService 把它当作关闭并落盘；重启失败后语音被永久关闭；重启窗口内并发切换语言被守卫跳过。
    - 修复：重启不再触发 false；新增 `_isRestarting`，语言切换守卫同时考虑重启中。

35. **Ear 把不支持的语种映射成 en-US**
    - 位置：`EarLanguageDetector.MapToLiveCaptionsLanguage`。
    - 原因：默认分支返回 `en-US`；俄语/葡语等连续命中 3 次会把字幕强制切英语并写入配置。
    - 修复：未知/不支持语种返回 null，不参与命中计数与自动切换。

36. **目标进程切换导致设备下拉重复/引擎反复重启**
    - 位置：`SpeechSection.ApplyTargetProcess` / `InitAudioDevicesAsync`。
    - 原因：setter 触发 `TargetAudioProcessChanged` 回调，同时 `ApplyTargetProcess` 又直接起一次 `InitAudioDevicesAsync`；两次并发清空/重建下拉框。
    - 修复：`InitAudioDevicesAsync` 加 `_isRefreshingAudioDevices` 重入保护；`ApplyTargetProcess` 只保留事件回调一条刷新路径。

37. **Whisper 主模型加载仍在 UI 线程**
    - 位置：`SpeechService.Model.cs` `EnsureProcessorAsync` / `SwitchModelAsync` / `SwitchLanguageAsync`。
    - 原因：`WhisperFactory.FromPath` + `Build()` 是重 IO/初始化，直接 await 在 UI 线程上会冻结窗口数秒。
    - 修复：工厂创建与 processor 构建放入 `Task.Run`（Ear 已在第 14 项修复）。

38. **Ollama 可编辑模型名手输内容不保存**
    - 位置：`TranslationSection.xaml(.cs)`。
    - 原因：`OllamaModelComboBox` 是 `IsEditable=True`，只有 `SelectionChanged`，手输列表外模型名不会持久化。
    - 修复：新增 `LostFocus`，将非空且变化的文本写入 `OllamaModel`。

## 第二轮追加修复（39-52）

39. **关闭「逐字实时上屏」后回车发送必然失败**
    - 位置：`SettingsService.Osc.cs` `FinalizeSendAsync`。
    - 原因：`_lastOscSendTime` 初值 `DateTime.MinValue`，`(int)` 转换溢出为 `int.MinValue`，`110 - elapsed` 再溢出为负数，`Task.Delay(负数)` 抛异常；`IsLiveTypingEnabled=false` 时该字段永远不会被赋值。
    - 修复：改用 `double` 计算，`MinValue` 时跳过等待，延迟值 `Math.Max(0, ...)`。

40. **导航拖拽没有指针捕获，窗口外松开后状态卡死**
    - 位置：`MainWindow.xaml.cs`。
    - 原因：`StartNavDrag` 未 `CapturePointer`，鼠标移出窗口后收不到 Released/CaptureLost，`_isDragging` 永久为 true，幽灵卡片不消失，下一次点击会意外改写顺序。
    - 修复：记录 `Pointer` 并 `CapturePointer`，`EndNavDrag` 释放；`PointerMoved` 检测左键已松开时强制结束。

41. **托盘“打开主界面”在沉浸模式下不退出沉浸**
    - 位置：`MainWindow.xaml.cs` 托盘回调。
    - 原因：只 `AppWindow.Show()`，`IsImmersiveMode` 仍为 true，两个窗口同时出现，唤出热键/自动填入被短路。
    - 修复：显示前若处于沉浸模式先 `SetImmersiveMode(false)`。

42. **主窗口默认尺寸按物理像素下发，高 DPI 下内容区不足**
    - 位置：`MainWindow.xaml.cs`。
    - 原因：`AppWindow.Resize` 用像素，`820×560` 在 150% 缩放下只有约 547×373 DIP，小于设置页 700 DIP 内容宽度。
    - 修复：默认尺寸按 `GetDpiForWindow` 换算；已保存的非默认尺寸仍按像素恢复。

43. **翻译页加载时把程序化模型选择当成用户切换**
    - 位置：`TranslationSection.xaml.cs` `LoadOllamaModelsAsync`。
    - 原因：`_isInitializing` 在 await 前已置 false，重建列表/回退选中触发 `SelectionChanged`，静默改写 `OllamaModel` 并卸载旧模型。
    - 修复：新增 `_isLoadingModels` 闸门；`LostFocus` 只处理手输列表外模型，选中项不重复持久化。

44. **沉浸窗多行输入把退出按钮裁出窗口**
    - 位置：`ImmersiveWindow.xaml(.cs)`。
    - 原因：`MeasureTextBlock` 只在清空时重置为 "A"，输入框无最大高度，多行时输入卡片向上无限增长，底部按钮落到窗口外。
    - 修复：输入变化时同步 `MeasureTextBlock.Text`；输入框与测量块 `MaxHeight=120`，垂直滚动条改为 Auto。

45. **停止语音时 `_wave16` 字段竞态**
    - 位置：`SpeechService.Transcribe.cs`。
    - 原因：循环直接读 `_wave16`，`StopInternalAsync` 置 null 后可能在检查与调用之间产生 NullReference。
    - 修复：循环开始捕获本地 `wave16` 引用。

46. **SpeechService.Dispose 与在途推理/信号量释放竞态**
    - 位置：`SpeechService.cs` `Dispose`。
    - 原因：不等待 `_transcribeLock` 就 Dispose processor/factory；提前 Dispose 三个 SemaphoreSlim，后台 finally 再 Release 会抛 ObjectDisposedException。
    - 修复：尝试 500ms 获取转写锁后再释放原生对象；不再 Dispose 三个 SemaphoreSlim。

47. **AutoSwitcher.Dispose 与 SetEnabledAsync 竞态**
    - 位置：`LiveCaptionsLanguageAutoSwitcher.Dispose`。
    - 原因：不取 `_lock` 就清空 `_activeCaptures`，并发 SetEnabled 可能重新启动捕获；Dispose `_lock` 后并发 Release 抛异常。
    - 修复：尝试获取 `_lock` 后停止，不释放 `_lock`。

48. **退出清理只等 500ms**
    - 位置：`SettingsService.Lifecycle.cs` `QuitApplicationAsync`。
    - 原因：`Task.WhenAny(cleanupTask, Task.Delay(500))` 太短，`ShutdownAndKillProcess` 可能与清理任务并发 Dispose。
    - 修复：等待上限提高到 2000ms。

49. **超过 144 字符仍原样发送**
    - 位置：`SettingsService.Osc.cs` `FinalizeSendAsync` / `SendMessageAsync`。
    - 原因：UI 只把超限标红，发送前不截断，VRChat 会静默截断/丢弃。
    - 修复：发送前统一截断到 144 字符。

50. **常驻模板的分隔符清洗误伤普通标点**
    - 位置：`PersistentTextService.PrepareTemplateForRender`。
    - 原因：全局正则把任意单逗号/连字符替换成 `" | "`，正常文本 "Hello, world" 会被改写。
    - 修复：过期块先放占位符，只清理被移除块附近的管道符/连续分隔符；单逗号、单连字符、URL 中的 `/` 保留。

51. **MMDevice / Process 枚举后未释放**
    - 位置：`AudioDeviceService`、`AudioProcessService`、`TtsService.GetPlaybackDevices`、`ProcessLoopbackCapture`、`LiveCaptionsService.Win32`。
    - 原因：COM 设备与 Process 句柄长期泄漏，长时间运行/频繁刷新设备会累积。
    - 修复：枚举后 Dispose 设备/进程；TTS 播放结束释放解析出的 mic/mon 设备。

52. **Ollama / TTS 健康检查响应未释放**
    - 位置：`OllamaService`、`TtsService.CheckHealthAsync`。
    - 原因：`HttpResponseMessage` 未 `using`，连接资源延迟回收。
    - 修复：所有响应改为 `using`。

53. **托盘图标子类拦截 WM_CLOSE，绕过关闭保存**
    - 位置：`TrayIconService.cs` `WindowSubclass`。
    - 原因：子类直接 `ShowWindow(SW_HIDE)` 并返回 0，WinUI 的 `AppWindow.Closing` 不执行，关到托盘时 `SaveConfigImmediately` 与清理逻辑被跳过。
    - 修复：不再拦截 WM_CLOSE，交给 `DefSubclassProc` → `AppWindow.Closing` 统一隐藏并保存配置。

## 第三轮追加修复（54-56）

54. **进程隔离热切换后 WaveFormat 变化导致变调/错乱音频**
    - 位置：`ProcessLoopbackCapture.TrySwitchToProcessAsync`。
    - 原因：`SpeechService` 的 `BufferedWaveProvider`/重采样链按启动时旧 recorder 格式构建，热切换到新 recorder 后若采样率/声道/位深不同，仍按旧格式解释新字节。
    - 修复：切换前比较新旧 WaveFormat；不一致时释放新 recorder、停止 watcher、提示重启语音识别，保持原监听，绝不静默播放损坏音频。

55. **InboundEnabled 被运行时状态覆盖，启动失败后永久不再重试**
    - 位置：`SettingsService.Config.cs` / `SettingsService.cs` / `NetworkLogSection.xaml.cs`。
    - 原因：`SaveConfigImmediately` 用 `InboundService.IsRunning` 覆盖配置；端口被占用导致启动失败时，用户“想开启”的意图被写成 false，下次启动不再重试。
    - 修复：新增 `SettingsService.InboundEnabled` 表示用户意图，保存时不再用运行时状态覆盖；UI 开关写入意图，启动失败时保留意图供下次重试。

56. **重启接口按钮导致开关/运行状态分裂**
    - 位置：`NetworkLogSection.RestartInboundButton_Click`。
    - 原因：直接 `Start` 后把开关设为 `IsRunning`；启动失败会把开关拨回关闭并连带把用户意图写成 false。
    - 修复：重启视为希望保持开启；开关原本关闭时先拨到开启，由 Toggled 统一启动。

## 第四轮追加修复（57-59）

57. **Windows 关机/注销被关闭拦截阻止**
    - 位置：`MainWindow.xaml.cs` `AppWindow.Closing`。
    - 原因：所有 Closing 都 `args.Cancel = true`，系统关机时也会取消关闭，可能阻止关机。
    - 修复：用 `GetSystemMetrics(SM_SHUTTINGDOWN)` / `Environment.HasShutdownStarted` 识别关机，关机时只保存配置、不取消关闭。

58. **TTS 启动中途异常留下孤儿 Python 进程**
    - 位置：`TtsService.StartManagedServerAsync`。
    - 原因：`proc.Start()` 之后、登记 `_serverProcess` 之前抛异常时，局部 Process 未终止/释放，可能留下占用显存的孤儿进程。
    - 修复：记录 `startedProc`，catch 中先尝试 `Kill(entireProcessTree: true)` 再 Dispose。

59. **Inbound 意图开启但启动失败时状态文案误导**
    - 位置：`NetworkLogSection` 加载状态文案。
    - 原因：开关按用户意图显示为开启，但状态栏只写“外部接口已关闭”，用户不知道是启动失败。
    - 修复：区分“运行中 / 意图开启但未运行（可重启重试）/ 用户关闭”三种状态。

## 第五轮追加修复（60-64）

60. **OscService 的 UdpClient 在发送途中被替换/释放**
    - 位置：`Services/Osc/OscService.cs` `ResetClient` / `GetClient` / `Dispose`。
    - 原因：修改 Host/Port 会 `ResetClient()` 直接 Dispose `_udpClient`，而发送协程可能正持有同一实例，导致 `ObjectDisposedException`/静默丢包；若用信号量跨 await 持锁，UI 线程改 Host 会死锁。
    - 修复：改用 `_clientLock` 只保护引用的读取/替换，释放放在锁外；发送路径取到的旧引用失效时按失败记录日志，不会静默丢包。

61. **热键允许注册“无修饰键的普通字符键”**
    - 位置：`Views/Sections/HotkeySection.xaml.cs` `ParseAndApplyKey`。
    - 原因：用户只按字母键时 `mods == 0`，会注册成全局裸键，抢走所有程序里该字母的正常输入。
    - 修复：无修饰键时只允许 F1~F24，其余组合直接拒绝并在 InfoBar 提示；新增 `IsFunctionKey`。

62. **144 字符截断可能切断 emoji 代理对**
    - 位置：`SettingsService.Osc.cs`（`FinalizeSendAsync` / `SendMessageAsync`）、`VariableService.Render`、`InteractionSection.InsertTextAtCursor`。
    - 原因：`Substring(0, 144)` 若正好落在高代理项上，会留下半个字符，VRChat 气泡显示乱码。
    - 修复：统一改为“若第 144 位是高代理项则回退一位”的截断；`SettingsService` 抽出 `TruncateForChatbox` 复用。

## 第六轮追加修复（65-72）

65. **`InboundService.Start` 内部 Stop 会刷一条误导性“已关闭”**
    - 位置：`InboundService.Stop`。
    - 原因：`Start` 先调 `Stop` 做清理，旧逻辑无条件广播“外部接口监听已关闭”，刚启动就会先闪一条关闭提示。
    - 修复：只有原本处于运行中才广播关闭状态。

66. **修改 Inbound 端口只改属性，不迁移监听**
    - 位置：`NetworkLogSection` 的 HTTP/OSC 端口 TextBox。
    - 原因：`HttpListener`/`UdpClient` 在 Start 时按旧端口创建，改属性不会重建监听，界面显示新端口但实际仍监听旧端口。
    - 修复：端口实际变化且服务处于开启状态时，自动按新端口重启监听；相同值不重复启动。

67. **`InboundAutoSendToVrc` 只改运行时字段**
    - 位置：`NetworkLogSection.AutoSendExternalSwitch_Toggled`。
    - 原因：只写 `InboundService.AutoSendToVrc`，依赖后续 `SaveConfigImmediately` 反向同步；若中途发生其它保存，可能被旧值覆盖。
    - 修复：同时写 `Config.InboundAutoSendToVrc`。

68. **设置页/聊天页事件重复订阅**
    - 位置：`TtsSection`、`InteractionSection`、`SpeechSection`、`NetworkLogSection`、`ChatPage` 的 `Loaded`。
    - 原因：页面启用 `NavigationCacheMode`，`Loaded` 可能在没有 `Unloaded` 的情况下再次触发，直接 `+=` 会叠加处理函数（状态刷新/设备重建执行多次、定时器重复）。
    - 修复：统一改为“先退订再订阅”；`InteractionSection` 的状态定时器先 Stop 再重建。

## 第七轮追加修复（73-75）

73. **常驻文本首次推送因限流计算溢出而永远失败**
    - 位置：`PersistentTextService.OnVariableUpdatedAsync`。
    - 原因：`_lastOscSendTime` 初值 `DateTime.MinValue`，`(int)elapsed` 溢出成 `int.MinValue`，`MinSendIntervalMs - (int)elapsed` 再次溢出为负数，`Task.Delay(负数)` 抛 `ArgumentOutOfRangeException`，被 catch 吞成一行 Debug 输出 → 首次变量更新永远推不出去（与第 39 项同类，但这条在模板推送路径）。
    - 修复：`MinValue` 时跳过等待，延迟值用 `Math.Ceiling` + 正数判断。

74. **音频捕获回调在停止时读已置空的字段**
    - 位置：`SpeechService.StartAsync` 的 `DataAvailable` 处理器。
    - 原因：回调直接读 `_bufferedWaveProvider`，`StopInternalAsync` 会把它置 null；最后一个音频回调与停止重叠时抛 `NullReferenceException`，异常会冒到音频线程。
    - 修复：启动时捕获本地 `bufferedProvider` 引用，并吞掉 `ObjectDisposedException` / `InvalidOperationException`。

75. **流式合成被提前放弃时状态永久停在“正在合成语音”**
    - 位置：`TtsService.SynthesizeStreamAsync`。
    - 原因：`SetState(Synthesizing)` 在 `yield` 之前设置；若调用方取消后不再 `MoveNext`（`PlayStreamAsync` 的取消路径），收尾的 `SetState(Ready)` 不会执行。
    - 修复：用 `finished` 标记 + `finally` 复位：未正常完成且状态仍为 Synthesizing 时置为 Ready。

## 第八轮追加修复（76-77）

76. **常驻文本的三条发送路径未共享同一速率门控**
    - 位置：`PersistentTextService`（`RestartLoop` 保活循环、`SubmitAsync`、`OnVariableUpdatedAsync`）。
    - 原因：变量推送走 `_sendGate`，但 3 秒保活循环和“提交”按钮直接调 `SendChatboxMessageAsync`，不读 `_lastOscSendTime`；变量更新紧邻保活 tick 时会短时间超过 VRChat 的 9 包/2 秒限额，导致消息被丢弃。
    - 修复：新增 `SendWithThrottleAsync`（`_sendGate` + `MinSendIntervalMs` + 更新时间戳），保活循环与 `SubmitAsync` 统一走它；`OnVariableUpdatedAsync` 本身已持有门控，保持原样避免嵌套死锁。

77. **`EarLanguageDetector.Dispose` 与在途检测竞态**
    - 位置：`Services/Speech/EarLanguageDetector.cs`。
    - 原因：`Dispose` 直接释放 `_processor`/`_factory` 并 `_lock.Dispose()`；若此时 `DetectFromAudioAsync` 正在持锁推理，或随后 `finally` 里 `Release()`，会抛 `ObjectDisposedException` / 使用已释放的原生句柄。
    - 修复：先把 `_isInitialized` 置 false（在途检测入口直接返回），再尝试 500ms 获取锁后释放原生对象并置 null；不再 Dispose `_lock`。

## 第九轮追加修复（78-81）

78. **`ClearAsync` 清空气泡未走统一门控**
    - 位置：`PersistentTextService.ClearAsync`。
    - 原因：清空常驻文本时直接发送 `/chatbox/input` 空串，与 3 秒保活 tick 紧邻时可能叠加超限。
    - 修复：改走 `SendWithThrottleAsync`。

79. **`StartManagedServerAsync` 用阻塞锁，第二次点击会卡住 UI**
    - 位置：`TtsService.StartManagedServerAsync`。
    - 原因：启动流程最长轮询 35 秒（模型加载），期间 `_serverStartLock.WaitAsync()` 会阻塞；UI 线程上的第二次点击没有任何反馈。
    - 修复：改为 `WaitAsync(0)` 非阻塞获取，已有启动流程时立即返回并写日志。

80. **Ollama 批量卸载可能把退出拖住十几秒**
    - 位置：`OllamaService.UnloadAllModelsAsync`。
    - 原因：每个模型单独 5 秒超时，3 个模型常驻时退出最多等 15 秒。
    - 修复：整批共用一个 6 秒总预算，超时后放弃剩余模型（交给 Ollama 自身 keep_alive 到期释放）。

81. **LiveCaptions 启动失败不回滚开关状态**
    - 位置：`LiveCaptionsService.StartAsync`。
    - 原因：`EnsureLiveCaptionsProcessAsync` 返回零窗口或抛异常时只报错返回，`_isRunning` 仍为 false 但 `SettingsService.IsSpeechRecognitionEnabled` 仍为 true；UI 显示“已开启”实际没有监听，且不再有状态事件来纠正。
    - 修复：失败路径清理 `_workerCts`、置 `_isRunning=false` 并触发 `RunningStateChanged(false)`，让开关同步回滚。

## 第十轮追加修复（82-85）

82. **`SpeechSection_Loaded` 异常后 `_isInitializing` 永久为 true**
    - 位置：`Views/Sections/SpeechSection.xaml.cs`。
    - 原因：`_isInitializing = true` 后有一串初始化与 `await InitAudioDevicesAsync()`，若任一步抛异常，末尾的 `_isInitializing = false` 不会执行；之后所有控件的 SelectionChanged/Toggled 都被当成“初始化中”而忽略，语音设置页整体失去响应。
    - 修复：整段包进 `try/finally`，在 finally 中复位。

83. **`RefreshAudioDevicesButton_Click` 同样的 `_isInitializing` 泄漏**
    - 位置：同上。
    - 修复：`await` 包进 `try/finally`。

84. **TTS 启动/停止时在 UI 线程同步清理进程，窗口冻结数秒**
    - 位置：`TtsService.StartManagedServerAsync` 的 `StopManagedServer()` 调用、`TtsSection.StopServerButton_Click`。
    - 原因：`StopManagedServer` 内部同步执行 `netstat`（最多等 3 秒）与 PowerShell 孤儿进程清理（最多等 3 秒），直接在 UI 线程调用会冻结窗口。
    - 修复：启动路径改为 `await Task.Run(StopManagedServer)`；停止按钮改为后台执行并在 finally 里刷新按钮状态。

## 仍需下一轮处理（候选）

- `TtsService` 仍使用过时 `WasapiOut` API（20 条 CS0618 警告），未来可迁移到 `WasapiPlayerBuilder`。
- 沉浸窗仍固定 620×640、输入卡片底部保留 160 DIP；已限制多行高度避免按钮被裁，但“按内容自适应窗口高度”仍未实现（`RequestImmersiveResize` 是死代码）。
- `TtsService.StartManagedServerAsync` 在等待启动期间持有 `_serverStartLock`；虽然用户停止现在能快速退出，但启动失败/超时的路径仍会让下一次启动等待。
- `OllamaService.UnloadAllModelsAsync` 对每个模型的卸载请求仍使用 15 秒 HttpClient 超时，批量卸载可能较慢。
- `TtsService` 的 `StopManagedServer` 仍通过 `netstat`/PowerShell 做端口与孤儿进程清理，属于外部进程调用，存在被杀进程/命令不可用的环境差异。
- `HotkeyService` 用 `SetWindowLongPtr` 子类化窗口过程，未处理 `WM_NCDESTROY` 时恢复；应用正常退出时 `Dispose` 会恢复，异常退出则由进程结束兜底。
- `ImmersiveWindow` 的 `OverlayRootGrid` 固定 `Width="620"`，多显示器/非 100% 缩放时未按 DPI 重算窗口尺寸。
- `TtsService.StopManagedServer` 仍依赖 `netstat` 与 PowerShell 做端口/孤儿进程清理，属于外部进程调用，存在命令不可用或被安全软件拦截的环境差异。
- `MainWindow_Closed` 中 `TtsService.Dispose()` 仍会在 UI 线程同步执行 `StopManagedServer`（关闭流程，影响相对小）。

## 检查范围

- `Services/Settings/*`、`Services/Osc/*`、`Services/Speech/*`、`Services/Tts/*`、`Services/Template/*`、`Services/Translation/*`、`Services/Common/*`、`Services/Input/*`。
- `App.xaml(.cs)`、`MainWindow.xaml(.cs)`、`Views/*` 与 `Views/Sections/*`。
- 工作区已有 UI 优化与 TTS 流式参数修改保持不变。
