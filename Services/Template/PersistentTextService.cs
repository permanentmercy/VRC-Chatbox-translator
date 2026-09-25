using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 独立的头顶常驻与动态模板文本服务
/// 负责：
/// 1. 解析常驻模板中的 {变量名} 占位符；
/// 2. 支持使用 "[]" 标注瞬态文本，仅在变量更新或提交时临时保留指定秒数，过期后自动隐藏；
/// 3. 在锁内维护提交与状态时间戳，在锁外完成文本拼接与正则处理；
/// 4. 定时循环刷新 VRChat 头顶气泡以维持常驻；
/// 5. 严密检测玩家在本程序及 VRChat 游戏内打字、回车发送与外部避让，杜绝覆盖游戏内聊天。
/// </summary>
public class PersistentTextService : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _loopCts;

    private DateTime _lastUserSentTime = DateTime.MinValue;
    private bool _isUserTyping = false;

    // 游戏内输入与避让状态
    private DateTime _lastInGameSentTime = DateTime.MinValue;
    private DateTime _lastInGameTypingTime = DateTime.MinValue;
    private DateTime _pausedUntil = DateTime.MinValue;
    private int _avoidanceSeconds = 12;

    // [] 瞬态标注文本生命周期控制
    private int _transientDurationSeconds = 10;
    private DateTime _transientUntil = DateTime.MinValue;
    private bool _wasTransientActive = false;

    public bool IsEnabled { get; set; } = false;
    public string CustomText { get; set; } = string.Empty;
    public bool EnableInGameAvoidance { get; set; } = true;

    public int AvoidanceSeconds
    {
        get => _avoidanceSeconds;
        set => _avoidanceSeconds = Math.Clamp(value, 3, 60);
    }

    /// <summary>
    /// 被 "[]" 标注的文本在变量更新时的保留秒数 (默认 10 秒)
    /// </summary>
    public int TransientDurationSeconds
    {
        get => _transientDurationSeconds;
        set => _transientDurationSeconds = Math.Clamp(value, 1, 300);
    }

    public event Action<string>? StatusNotice;
    public event Action? StateChanged;

    /// <summary>
    /// 处理模板中被 [] 标注的瞬态文本 (纯函数，在锁外部执行):
    /// - isTransientActive 为 true 时: 将 [内容] 展开为 内容
    /// - isTransientActive 为 false 时: 将 [内容] 整体移除，并自动修剪多余的分隔符和空格
    /// </summary>
    public static string PrepareTemplateForRender(string template, bool isTransientActive)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        if (isTransientActive)
        {
            // 激活态: 展开中括号
            return Regex.Replace(template, @"\[([^\]]+)\]", "$1");
        }
        else
        {
            // 过期/未激活态: 移除所有中括号及其内部内容
            string stripped = Regex.Replace(template, @"\[[^\]]*\]", string.Empty);

            // 智能清洗遗留的多余分隔符 (比如连续的 " |  | " 归一化为 " | ")
            stripped = Regex.Replace(stripped, @"(\s*[\|／/\\,\-]\s*)+", " | ");
            stripped = stripped.Trim(' ', '|', '-', '/', '\\', ',');
            return stripped;
        }
    }

    /// <summary>
    /// 本程序主窗口手动发送了聊天消息
    /// </summary>
    public void NotifyUserSentMessage()
    {
        lock (_lock)
        {
            _lastUserSentTime = DateTime.UtcNow;
            _isUserTyping = false;
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 本程序主窗口正在打字
    /// </summary>
    public void NotifyUserTyping(bool isTyping)
    {
        lock (_lock)
        {
            _isUserTyping = isTyping;
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 捕获到玩家在 VRChat 游戏内按回车发送了聊天消息
    /// </summary>
    public void NotifyInGameMessageSent(int? customSeconds = null)
    {
        if (!EnableInGameAvoidance) return;

        lock (_lock)
        {
            _lastInGameSentTime = DateTime.UtcNow;
            _lastInGameTypingTime = DateTime.MinValue;
            if (customSeconds.HasValue)
            {
                _avoidanceSeconds = Math.Clamp(customSeconds.Value, 3, 60);
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 捕获到玩家在 VRChat 游戏内正在打字按键
    /// </summary>
    public void NotifyInGameTyping()
    {
        if (!EnableInGameAvoidance) return;

        lock (_lock)
        {
            _lastInGameTypingTime = DateTime.UtcNow;
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 外部 API 或命令请求临时避让/暂停指定秒数
    /// </summary>
    public void PauseAvoidance(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 300);
        lock (_lock)
        {
            _pausedUntil = DateTime.UtcNow.AddSeconds(seconds);
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 外部 API 请求立即恢复常驻
    /// </summary>
    public void ResumeAvoidance()
    {
        lock (_lock)
        {
            _pausedUntil = DateTime.MinValue;
            _lastInGameSentTime = DateTime.MinValue;
            _lastInGameTypingTime = DateTime.MinValue;
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 检查当前是否处于需要避让的状态，并返回原因描述及剩余秒数
    /// </summary>
    public bool IsInAvoidance(out string reason, out int remainingSeconds)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            // 1. 外部 API 暂停
            if (now < _pausedUntil)
            {
                remainingSeconds = (int)Math.Ceiling((_pausedUntil - now).TotalSeconds);
                reason = $"外部接口避让中 (剩余 {remainingSeconds} 秒)";
                return true;
            }

            // 2. 游戏内按回车发送避让
            if (EnableInGameAvoidance)
            {
                double elapsedSinceInGameSend = (now - _lastInGameSentTime).TotalSeconds;
                if (elapsedSinceInGameSend < _avoidanceSeconds)
                {
                    remainingSeconds = (int)Math.Ceiling(_avoidanceSeconds - elapsedSinceInGameSend);
                    reason = $"游戏内消息展示避让中 (剩余 {remainingSeconds} 秒)";
                    return true;
                }

                // 3. 游戏内正在打字 (停止击键 3 秒后恢复)
                double elapsedSinceInGameType = (now - _lastInGameTypingTime).TotalSeconds;
                if (elapsedSinceInGameType < 3.0)
                {
                    remainingSeconds = (int)Math.Ceiling(3.0 - elapsedSinceInGameType);
                    reason = "检测到游戏内正在输入文字...";
                    return true;
                }
            }

            // 4. 本软件输入框打字中
            if (_isUserTyping)
            {
                remainingSeconds = 0;
                reason = "本软件打字草稿中...";
                return true;
            }

            // 5. 本软件手动发送消息后避让 9 秒
            double elapsedSinceUserSend = (now - _lastUserSentTime).TotalSeconds;
            if (elapsedSinceUserSend < 9.0)
            {
                remainingSeconds = (int)Math.Ceiling(9.0 - elapsedSinceUserSend);
                reason = $"本机手动消息避让中 (剩余 {remainingSeconds} 秒)";
                return true;
            }

            remainingSeconds = 0;
            reason = "正常保活中";
            return false;
        }
    }

    /// <summary>
    /// 当变量值更新时，由外部通知。
    /// 锁内维持提交与瞬态生命周期状态；锁外完成文本拼接、展开与 OSC 发送。
    /// </summary>
    public async Task OnVariableUpdatedAsync(string variableName, VariableService variableService, OscService oscService)
    {
        string template;
        bool isTransientActive;

        // 1. 锁内维持提交与瞬态生命周期状态
        lock (_lock)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
            template = CustomText;

            // 检查当前模板是否使用了这个被更新的变量
            if (!variableService.UsesVariable(template, variableName))
            {
                return;
            }

            // 变量更新，激活标注文本生命周期计时器
            _transientUntil = DateTime.UtcNow.AddSeconds(_transientDurationSeconds);
            isTransientActive = true;
            _wasTransientActive = true;
        }

        // 2. 锁外部执行避让检测
        if (IsInAvoidance(out _, out _))
        {
            return;
        }

        // 3. 锁外部完成模板预处理与变量渲染拼接
        string processedTemplate = PrepareTemplateForRender(template, isTransientActive);
        string rendered = variableService.Render(processedTemplate);
        if (string.IsNullOrWhiteSpace(rendered)) return;

        try
        {
            await oscService.SendTypingAsync(false, recordLog: false);
            await Task.Delay(40);
            await oscService.SendChatboxMessageAsync(rendered, direct: true, playSound: false, recordLog: false);
            await Task.Delay(40);
            await oscService.SendTypingAsync(false, recordLog: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PersistentTextService] OnVariableUpdated error: {ex.Message}");
        }
    }

    /// <summary>
    /// 用户在界面上点击【提交】按钮时调用：
    /// 1. 锁内维持提交状态与瞬态激活时限；
    /// 2. 锁外完成文本拼接并立即推送至 VRChat；
    /// 3. 启动常驻定时保活循环。
    /// </summary>
    public async Task<bool> SubmitAsync(string template, VariableService variableService, OscService oscService, SettingsService settingsService)
    {
        template = template?.Trim() ?? string.Empty;

        // 1. 锁内维护提交状态
        lock (_lock)
        {
            IsEnabled = !string.IsNullOrWhiteSpace(template);
            CustomText = template;
            _lastInGameSentTime = DateTime.MinValue;
            _lastInGameTypingTime = DateTime.MinValue;
            _pausedUntil = DateTime.MinValue;

            // 提交时默认激活瞬态展示
            _transientUntil = DateTime.UtcNow.AddSeconds(_transientDurationSeconds);
            _wasTransientActive = true;
        }

        settingsService.SuppressTypingAnimation();

        if (string.IsNullOrWhiteSpace(template))
        {
            await ClearAsync(oscService);
            StatusNotice?.Invoke("常驻文本已清空");
            StateChanged?.Invoke();
            return false;
        }

        // 2. 锁外完成文本预处理与拼接
        string processed = PrepareTemplateForRender(template, isTransientActive: true);
        string rendered = variableService.Render(processed);

        // 立即强制向 VRChat 推送
        await oscService.SendTypingAsync(false, recordLog: false);
        await Task.Delay(40);
        bool sent = await oscService.SendChatboxMessageAsync(rendered, direct: true, playSound: false, recordLog: false);
        await Task.Delay(40);
        await oscService.SendTypingAsync(false, recordLog: false);

        RestartLoop(variableService, oscService);
        StatusNotice?.Invoke("已成功提交至游戏头顶气泡");
        StateChanged?.Invoke();
        return sent;
    }

    /// <summary>
    /// 清除常驻文本并消除游戏内头顶残留气泡
    /// </summary>
    public async Task ClearAsync(OscService oscService)
    {
        lock (_lock)
        {
            CustomText = string.Empty;
            _transientUntil = DateTime.MinValue;
            _wasTransientActive = false;
            StopLoopInternal();
        }
        await oscService.SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: false);
        await oscService.SendTypingAsync(false, recordLog: false);
        StateChanged?.Invoke();
    }

    public void RestartLoop(VariableService variableService, OscService oscService)
    {
        lock (_lock)
        {
            StopLoopInternal();
            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;

            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 每 3 秒检测并保活一次（VRChat 气泡约 9 秒自动消失）
                        await Task.Delay(3000, token);
                        if (token.IsCancellationRequested) break;

                        string template;
                        bool isTransientActive;
                        bool justExpired = false;

                        // 1. 锁内仅读取状态快照与判定过期过渡
                        lock (_lock)
                        {
                            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) continue;
                            template = CustomText;

                            isTransientActive = DateTime.UtcNow < _transientUntil;
                            // 检测是否刚好从激活状态转为过期状态
                            if (_wasTransientActive && !isTransientActive)
                            {
                                justExpired = true;
                            }
                            _wasTransientActive = isTransientActive;
                        }

                        // 2. 锁外执行避让检测
                        if (IsInAvoidance(out _, out _))
                        {
                            continue;
                        }

                        // 3. 锁外完成文本拼接与渲染
                        string processed = PrepareTemplateForRender(template, isTransientActive);
                        string textToSend = variableService.Render(processed);

                        // 如果常驻内容在移除 [] 之后变为空，且刚好过期，则清口气泡
                        if (string.IsNullOrWhiteSpace(textToSend))
                        {
                            if (justExpired)
                            {
                                await oscService.SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: false);
                            }
                            continue;
                        }

                        // 循环保活推送：无声、不记录刷屏日志、绝对不发送打字动画
                        await oscService.SendChatboxMessageAsync(textToSend, direct: true, playSound: false, recordLog: false);
                        await oscService.SendTypingAsync(false, recordLog: false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[PersistentTextService] Loop error: {ex.Message}");
                    }
                }
            }, token);
        }
    }

    public void StopLoop()
    {
        lock (_lock)
        {
            StopLoopInternal();
        }
        StateChanged?.Invoke();
    }

    private void StopLoopInternal()
    {
        try
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
        }
        catch { }
    }

    public void Dispose()
    {
        StopLoop();
    }
}
