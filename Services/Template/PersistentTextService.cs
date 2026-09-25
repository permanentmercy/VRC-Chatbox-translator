using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 独立的头顶常驻与动态模板文本服务
/// 负责：
/// 1. 解析常驻模板中的 {变量名} 占位符；
/// 2. 支持使用 "[]" 标注瞬态文本，每个括号块内的变量生命周期独立计算，过期后自动隐藏；
/// 3. 在锁内维持提交与状态时间戳，在锁外完成文本拼接与正则处理；
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

    // [] 瞬态标注文本独立生命周期控制
    private int _transientDurationSeconds = 10;
    private readonly Dictionary<string, DateTime> _varTransientUntil = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _manualSubmitTransientUntil = DateTime.MinValue;
    private string _lastRenderedText = string.Empty;

    // 速率门控与尾随合并队列控制 (遵守 VRChat 9包/2秒频控限制)
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private DateTime _lastOscSendTime = DateTime.MinValue;
    private volatile bool _hasPendingUpdate = false;
    private const int MinSendIntervalMs = 120;

    public bool IsEnabled { get; set; } = false;
    public string CustomText { get; set; } = string.Empty;
    public bool EnableInGameAvoidance { get; set; } = true;

    public int AvoidanceSeconds
    {
        get => _avoidanceSeconds;
        set => _avoidanceSeconds = Math.Clamp(value, 3, 60);
    }

    /// <summary>
    /// 被 "[]" 标注的文本在对应变量更新时的保留秒数 (默认 10 秒)
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
    /// 独立评估每一个 [] 标注块：
    /// - 若该块内包含的任意变量在当前时间戳未过期，则展开该块 [内容] -> 内容；
    /// - 若该块内所有变量均已过期 (或未曾激活)，则剥离该块；
    /// - 若该块不包含变量 (纯文字)，则依据 manualSubmitUntil 判定；
    /// - 随后智能清洗由于移除文本产生的多余分隔符与空格。
    /// </summary>
    public static string PrepareTemplateForRender(
        string template,
        IReadOnlyDictionary<string, DateTime> activeVars,
        DateTime manualSubmitUntil,
        DateTime now)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        // 1. 先将显式换行标识（{\n}, \n, {newline}, {换行}）预先转换为受控安全占位符，防止被后续分隔符清洗破坏
        string processed = Regex.Replace(template, @"\{(\\n|newline|换行)\}|\\n", "\uE000", RegexOptions.IgnoreCase);

        // 2. 使用 MatchEvaluator 逐个独立评估每一个中括号块
        processed = Regex.Replace(processed, @"\[([^\]]+)\]", match =>
        {
            string innerContent = match.Groups[1].Value;

            // 提取该中括号块中包含的所有 {变量名}
            var varMatches = Regex.Matches(innerContent, @"\{([a-zA-Z0-9_\-]+)\}");

            bool isBlockActive = false;

            if (varMatches.Count > 0)
            {
                // 如果包含变量：只要其中任意一个变量在当前时间仍然有效，该括号块即处于激活期
                foreach (Match vm in varMatches)
                {
                    string varName = vm.Groups[1].Value;
                    if (activeVars.TryGetValue(varName, out var expiry) && now < expiry)
                    {
                        isBlockActive = true;
                        break;
                    }
                }
            }
            else
            {
                // 如果是纯文本中括号块 (不含任何变量)，使用手动提交的过期时间
                isBlockActive = now < manualSubmitUntil;
            }

            if (isBlockActive)
            {
                // 激活态: 展开中括号，保留内部内容
                return innerContent;
            }
            else
            {
                // 过期态: 剥离该中括号内容
                return string.Empty;
            }
        });

        // 3. 智能清洗遗留的多余分隔符 (注意：绝不包含反斜杠，避免破坏转义)
        processed = Regex.Replace(processed, @"(\s*[\|／/,\-]\s*)+", " | ");
        // 4. 清理换行占位符周围残留的多余分隔符 (例如 " | <NL>" 或 "<NL> | ")
        processed = Regex.Replace(processed, @"\s*\|\s*\uE000", "\uE000");
        processed = Regex.Replace(processed, @"\uE000\s*\|\s*", "\uE000");
        processed = processed.Trim(' ', '|', '-', '/', ',');
        return processed;
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
    /// 锁内维持提交状态并独立重置该变量对应的生命周期计时；
    /// 锁外通过带尾随合并（Trailing Coalescing）的单槽速率门控队列，平滑将最新渲染结果推送至 VRChat，
    /// 既避免多任务并发竞争与丢包，又严格遵守 VRChat 9包/2秒频控限制。
    /// </summary>
    public async Task OnVariableUpdatedAsync(string variableName, VariableService variableService, OscService oscService)
    {
        // 1. 锁内维持提交状态并独立更新该变量生命周期时间戳 (极小临界区)
        lock (_lock)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
            if (!variableService.UsesVariable(CustomText, variableName)) return;

            DateTime now = DateTime.UtcNow;
            _varTransientUntil[variableName] = now.AddSeconds(_transientDurationSeconds);
        }

        // 标记有更新需要推送
        _hasPendingUpdate = true;

        // 若已有发送队列在处理，直接退出，运行中的队列会在末尾自动提取最新文本完成补偿推送 (Coalescing / Trailing)
        if (!await _sendGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            while (_hasPendingUpdate)
            {
                _hasPendingUpdate = false;

                if (IsInAvoidance(out _, out _))
                {
                    return;
                }

                string template;
                Dictionary<string, DateTime> activeVarsSnapshot;
                DateTime manualSubmitUntil;
                DateTime now;
                string lastRendered;

                lock (_lock)
                {
                    if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
                    template = CustomText;
                    now = DateTime.UtcNow;
                    activeVarsSnapshot = new Dictionary<string, DateTime>(_varTransientUntil, StringComparer.OrdinalIgnoreCase);
                    manualSubmitUntil = _manualSubmitTransientUntil;
                    lastRendered = _lastRenderedText;
                }

                string processedTemplate = PrepareTemplateForRender(template, activeVarsSnapshot, manualSubmitUntil, now);
                string rendered = variableService.Render(processedTemplate);

                // 如果渲染结果为空或与当前游戏内已呈现内容完全一致，则无需重复发送
                if (string.IsNullOrWhiteSpace(rendered) || string.Equals(rendered, lastRendered, StringComparison.Ordinal))
                {
                    continue;
                }

                // 速率控制：确保向 VRChat 发送的频率不超过限额 (至少间隔 MinSendIntervalMs)
                var elapsed = (DateTime.UtcNow - _lastOscSendTime).TotalMilliseconds;
                if (elapsed < MinSendIntervalMs)
                {
                    int delayMs = MinSendIntervalMs - (int)elapsed;
                    await Task.Delay(delayMs);
                }

                // 若在等待间隔中又有新的变量输入（如用户连续快速吐字），跳过过时文本，立即进入下一轮拉取最新文本
                if (_hasPendingUpdate)
                {
                    continue;
                }

                await oscService.SendChatboxMessageAsync(rendered, direct: true, playSound: false, recordLog: false);
                _lastOscSendTime = DateTime.UtcNow;

                lock (_lock)
                {
                    _lastRenderedText = rendered;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PersistentTextService] OnVariableUpdated error: {ex.Message}");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// 用户在界面上点击【提交】按钮时调用：
    /// 1. 锁内维持提交状态并重置所有变量初次展示时限；
    /// 2. 锁外完成文本拼接并立即推送至 VRChat；
    /// 3. 启动常驻定时保活循环。
    /// </summary>
    public async Task<bool> SubmitAsync(string template, VariableService variableService, OscService oscService, SettingsService settingsService)
    {
        template = template?.Trim() ?? string.Empty;
        DateTime now = DateTime.UtcNow;
        Dictionary<string, DateTime> activeVarsSnapshot;
        DateTime manualSubmitUntil;

        // 1. 锁内维护提交状态与各括号内变量初态
        lock (_lock)
        {
            IsEnabled = !string.IsNullOrWhiteSpace(template);
            CustomText = template;
            _lastInGameSentTime = DateTime.MinValue;
            _lastInGameTypingTime = DateTime.MinValue;
            _pausedUntil = DateTime.MinValue;

            _varTransientUntil.Clear();
            _manualSubmitTransientUntil = now.AddSeconds(_transientDurationSeconds);

            // 提交时默认激活模板中引用的所有变量初次展示
            var allVarMatches = Regex.Matches(template, @"\{([a-zA-Z0-9_\-]+)\}");
            foreach (Match m in allVarMatches)
            {
                string vName = m.Groups[1].Value;
                _varTransientUntil[vName] = now.AddSeconds(_transientDurationSeconds);
            }

            activeVarsSnapshot = new Dictionary<string, DateTime>(_varTransientUntil, StringComparer.OrdinalIgnoreCase);
            manualSubmitUntil = _manualSubmitTransientUntil;
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
        string processed = PrepareTemplateForRender(template, activeVarsSnapshot, manualSubmitUntil, now);
        string rendered = variableService.Render(processed);

        // 立即强制向 VRChat 推送
        bool sent = await oscService.SendChatboxMessageAsync(rendered, direct: true, playSound: false, recordLog: false);
        _lastOscSendTime = DateTime.UtcNow;

        lock (_lock)
        {
            _lastRenderedText = rendered;
        }

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
            _varTransientUntil.Clear();
            _manualSubmitTransientUntil = DateTime.MinValue;
            _lastRenderedText = string.Empty;
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
                        Dictionary<string, DateTime> activeVarsSnapshot;
                        DateTime manualSubmitUntil;
                        DateTime now;
                        string previousRendered;

                        // 1. 锁内仅读取状态快照
                        lock (_lock)
                        {
                            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) continue;
                            template = CustomText;
                            now = DateTime.UtcNow;
                            activeVarsSnapshot = new Dictionary<string, DateTime>(_varTransientUntil, StringComparer.OrdinalIgnoreCase);
                            manualSubmitUntil = _manualSubmitTransientUntil;
                            previousRendered = _lastRenderedText;
                        }

                        // 2. 锁外执行避让检测
                        if (IsInAvoidance(out _, out _))
                        {
                            continue;
                        }

                        // 3. 锁外完成文本独立判定拼接与渲染
                        string processed = PrepareTemplateForRender(template, activeVarsSnapshot, manualSubmitUntil, now);
                        string textToSend = variableService.Render(processed);

                        // 如果常驻内容在移除过期的 [] 之后变为空
                        if (string.IsNullOrWhiteSpace(textToSend))
                        {
                            if (!string.IsNullOrEmpty(previousRendered))
                            {
                                await oscService.SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: false);
                                lock (_lock) { _lastRenderedText = string.Empty; }
                            }
                            continue;
                        }

                        // 循环保活推送：无声、不记录刷屏日志、绝对不发送打字动画
                        await oscService.SendChatboxMessageAsync(textToSend, direct: true, playSound: false, recordLog: false);
                        _lastOscSendTime = DateTime.UtcNow;

                        lock (_lock)
                        {
                            _lastRenderedText = textToSend;
                        }
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
        _sendGate.Dispose();
    }
}
