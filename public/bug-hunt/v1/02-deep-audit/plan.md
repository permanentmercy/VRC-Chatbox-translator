# 深度复审与残留缺陷修复计划

## 版本与阶段

- 功能模块：`bug-hunt`（全项目缺陷排查）
- 版本：`v1`
- 阶段：`02-deep-audit`（智能体团队全量深度复审与残留缺陷清理）
- 状态：已完成 (所有缺陷均已修复且 114 项回归用例全部通过)

## 目标

1. 通过并行智能体团队（三大审计子代理：核心服务层、语音/TTS 层、UI/窗口层）对全项目源码进行深度代码缺陷审查。
2. 排查残留并发竞态、未捕获 `async void` 异常、UI 与配置状态不同步、硬编码绝对路径、资源泄漏与边界条件缺陷。
3. 执行最小化、可验证的代码修复，确保 `dotnet build` 0 错误通过。
4. 完整记录本阶段所有修复项与验证结果至 `result.md`，并同步更新 `plan.md` 与 `public/plan.md`。

## 智能体团队分工

1. **核心服务审计代理（Core Service Auditor）**：
   - 会话 ID：`288eecc2-753e-452e-bad7-7698bdeebce9`
   - 审查范围：`Services/Settings/`、`Services/Osc/`、`Services/Template/`、`Services/Translation/`、`Services/Common/`
   - 重点：并发死锁、SemaphoreSlim 释放契约、Socket/Http 泄漏、模板渲染边界与配置状态机。
2. **语音与 TTS 审计代理（Audio Speech Auditor）**：
   - 会话 ID：`f68e7ab9-6be1-44a3-bdbb-28893ae6a7f0`
   - 审查范围：`Services/Speech/`、`Services/Tts/`
   - 重点：音频流生命周期、Wasapi/Capture 泄漏与释放、进程挂钩与退出监控、模型并发推理安全。
3. **UI 与窗口交互审计代理（UI Window Auditor）**：
   - 会话 ID：`4420d73a-1a5e-40c1-b80b-670e3f5e1421`
   - 审查范围：`MainWindow.xaml(.cs)`、`App.xaml(.cs)`、`Views/` 及 `Views/Sections/`
   - 重点：UI 线程 Dispatcher 调度安全、`async void` 事件兜底、事件双向绑定回环、窗口沉浸模式切换。

## 执行步骤

1. [x] 启动三个并行只读审计智能体，划分模块执行全量源码排查。
2. [x] 汇聚并交叉比对智能体上报的潜在缺陷，验证代码上下文，剔除误报。
3. [x] 制定修复清单，实施高确定性缺陷修复，确保不引入行为破坏。
4. [x] 运行 `dotnet build` 验证 0 错误编译通过，并运行全部 9 大回归测试套件（114/114 通过）。
5. [x] 编写阶段结果产物 `public/bug-hunt/v1/02-deep-audit/result.md` 并标记计划完成。
