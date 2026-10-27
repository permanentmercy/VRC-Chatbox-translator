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

    /// <summary>
    /// 带 ##秒数 标记的 [] 块的独立到期时间，key 为完整的 "[...]" 原文（含 ## 标记）。
    /// </summary>
    private readonly Dictionary<string, DateTime> _blockTransientUntil = new(StringComparer.Ordinal);

    // 发送消息在模板未显式引用 {sent_text}/{sent_translation} 时，用隐藏变量追加显示，
    // 避免消息里的 [] / {} 破坏模板解析。
    private const string SentDisplayVar = "__sent_display";
    private const string SentTransDisplayVar = "__sent_trans_display";
    private DateTime _manualSubmitTransientUntil = DateTime.MinValue;
    private string _lastRenderedText = string.Empty;

    // 速率门控与尾随合并队列控制 (遵守 VRChat 9包/2秒频控限制)
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private DateTime _lastOscSendTime = DateTime.MinValue;
    private volatile bool _hasPendingUpdate = false;
    private const int MinSendIntervalMs = 120;
    /// <summary>
    /// 统一的发送间隔门控。变量推送与 3 秒保活循环是两条独立路径，
    /// 若都直接发送会在“变量更新紧邻保活 tick”时短时间超过 VRChat 限额，
    /// 因此两条路径都必须经过这里串行化。
    /// </summary>
    private async Task<bool> SendWithThrottleAsync(OscService oscService, string text)
    {
        await _sendGate.WaitAsync();
        try
        {
            var elapsed = (DateTime.UtcNow - _lastOscSendTime).TotalMilliseconds;
            if (_lastOscSendTime != DateTime.MinValue && elapsed < MinSendIntervalMs)
            {
                int delayMs = (int)Math.Ceiling(MinSendIntervalMs - elapsed);
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }
            }

            bool ok = await oscService.SendChatboxMessageAsync(text, direct: true, playSound: false, recordLog: false);
            _lastOscSendTime = DateTime.UtcNow;
            return ok;
        }
        finally
        {
            _sendGate.Release();
        }
    }

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
    /// 块级保留时长标记：每个 [] 内可用开头的 ##秒数 覆盖全局保留时长，
    /// 例如 [##5 临时消息]、[##5:临时消息]、[##5s 临时消息]。
    /// </summary>
    private static readonly Regex BlockDurationMarker = new(
        @"^\s*##\s*(\d{1,4})\s*(?:s|秒)?\s*[:：]?\s*",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析 [] 块开头的 ##秒数 标记；返回去掉标记后的实际内容。
    /// </summary>
    public static bool TryParseBlockDuration(string innerContent, out int seconds, out string contentWithoutMarker)
    {
        seconds = 0;
        contentWithoutMarker = innerContent;
        if (string.IsNullOrEmpty(innerContent)) return false;

        var m = BlockDurationMarker.Match(innerContent);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out int parsed)) return false;

        seconds = Math.Clamp(parsed, 1, 3600);
        contentWithoutMarker = innerContent.Substring(m.Length);
        return true;
    }

    /// <summary>
    /// 处理模板中被 [] 标注的瞬态文本 (纯函数，在锁外部执行):
    /// 独立评估每一个 [] 标注块：
    /// - 若块内写了 ##秒数，则用该块的独立到期时间判定（到期时间由调用方维护）；
    /// - 否则若块内包含变量，任意变量未过期则展开；
    /// - 纯文字块依据 manualSubmitUntil 判定；
    /// - 随后智能清洗由于移除文本产生的多余分隔符与空格。
    /// </summary>
    public static string PrepareTemplateForRender(
        string template,
        IReadOnlyDictionary<string, DateTime> activeVars,
        IReadOnlyDictionary<string, DateTime> blockExpiries,
        DateTime manualSubmitUntil,
        DateTime now)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        // 1. 先将显式换行标识（{\n}, \n, {newline}, {换行}）预先转换为受控安全占位符，防止被后续分隔符清洗破坏
        string processed = ApplyNewlinePlaceholders(template);

        // 2. 使用 MatchEvaluator 逐个独立评估每一个中括号块
        processed = Regex.Replace(processed, @"\[([^\]]+)\]", match =>
        {
            string innerContent = match.Groups[1].Value;

            // 块内 ##秒数：该块使用独立生命周期，不再依赖变量/手动提交时限
            bool hasBlockDuration = TryParseBlockDuration(innerContent, out _, out string blockContent);
            string contentToRender = hasBlockDuration ? blockContent : innerContent;

            bool isBlockActive;

            if (hasBlockDuration)
            {
                isBlockActive = blockExpiries.TryGetValue(match.Value, out var blockExpiry) && now < blockExpiry;
            }
            else
            {
                // 提取该中括号块中包含的所有 {变量名}
                // 变量名允许中文（VariableService 支持中文变量名与中文别名），
                // 否则 [{字幕语言}] 会被误判为“纯文本块”，只受手动提交时限控制。
                var varMatches = Regex.Matches(innerContent, @"\{([a-zA-Z0-9_\-\u4e00-\u9fa5]+)\}");
                isBlockActive = false;

                if (varMatches.Count > 0)
                {
                    // 如果包含变量：只要其中任意一个变量在当前时间仍然有效，该括号块即处于激活期
                    foreach (Match vm in varMatches)
                    {
                        string varName = VariableService.GetCanonicalVariableName(vm.Groups[1].Value);
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
            }

            if (isBlockActive)
            {
                // 激活态: 展开中括号，保留内部内容（##标记已剥离）
                return contentToRender;
            }
            else
            {
                // 过期态: 先放一个占位符，稍后只清理“被移除块”附近的分隔符，
                // 不能对全文做分隔符替换，否则普通逗号/连字符会被改成 " | "。
                return "\uE001";
            }
        });

        // Only normalize separators touching an actually removed block. A global
        // slash/punctuation replacement corrupts https://, ordinary -- and literal edges.
        string withExpiredBlocks = processed;
        processed = Regex.Replace(withExpiredBlocks,
            @"(?:[ \t]*[|,，、；;\-／/][ \t]*)*\uE001(?:[ \t]*(?:[|,，、；;\-／/]|\uE001)[ \t]*)*",
            match =>
            {
                int left = match.Index - 1;
                int right = match.Index + match.Length;
                while (left >= 0 && (withExpiredBlocks[left] == ' ' || withExpiredBlocks[left] == '\t')) left--;
                while (right < withExpiredBlocks.Length && (withExpiredBlocks[right] == ' ' || withExpiredBlocks[right] == '\t')) right++;
                bool hasLeftText = left >= 0 && withExpiredBlocks[left] is not ('\uE000' or '\r' or '\n');
                bool hasRightText = right < withExpiredBlocks.Length && withExpiredBlocks[right] is not ('\uE000' or '\r' or '\n');
                bool hasSeparator = match.Value.IndexOfAny(new[] { '|', ',', '，', '、', '；', ';', '-', '／', '/' }) >= 0;
                return hasLeftText && hasRightText && hasSeparator ? " | " : string.Empty;
            });
        return processed.Trim();
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

                // 3. 游戏内正在打字 (停止击键 5 秒后恢复，给予充分的思考与选词时间)
                double elapsedSinceInGameType = (now - _lastInGameTypingTime).TotalSeconds;
                if (elapsedSinceInGameType < 5.0)
                {
                    remainingSeconds = (int)Math.Ceiling(5.0 - elapsedSinceInGameType);
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
    /// 把显式换行标识统一替换成受控占位符。块级到期时间与渲染必须使用同一份
    /// 预处理结果，否则含 {\n} 的块会因 key 不一致而永远无法激活。
    /// </summary>
    private static string ApplyNewlinePlaceholders(string template)
    {
        return Regex.Replace(template, @"\{(\\n|newline|换行)\}|\\n", "\uE000", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 提交/切换模板时，为所有带 ##秒数 的块建立初始到期时间。调用方需持有 _lock。
    /// </summary>
    private void ResetBlockExpiriesForTemplate_NoLock(string template, DateTime now)
    {
        _blockTransientUntil.Clear();
        string processedTemplate = ApplyNewlinePlaceholders(template);
        foreach (Match block in Regex.Matches(processedTemplate, @"\[([^\]]+)\]"))
        {
            if (TryParseBlockDuration(block.Groups[1].Value, out int seconds, out _))
            {
                _blockTransientUntil[block.Value] = now.AddSeconds(seconds);
            }
        }
    }

    /// <summary>
    /// 某个变量更新时，刷新所有包含该变量、且带 ##秒数 的块。调用方需持有 _lock。
    /// </summary>
    private void RefreshBlockExpiriesForVariable_NoLock(VariableService variableService, string variableName, DateTime now)
    {
        string processedTemplate = ApplyNewlinePlaceholders(CustomText);
        foreach (Match block in Regex.Matches(processedTemplate, @"\[([^\]]+)\]"))
        {
            if (!TryParseBlockDuration(block.Groups[1].Value, out int seconds, out string content)) continue;
            if (!variableService.UsesVariable(content, variableName)) continue;
            _blockTransientUntil[block.Value] = now.AddSeconds(seconds);
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
            _varTransientUntil[VariableService.GetCanonicalVariableName(variableName)] = now.AddSeconds(_transientDurationSeconds);
            RefreshBlockExpiriesForVariable_NoLock(variableService, variableName, now);
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
                Dictionary<string, DateTime> blockExpiriesSnapshot;
                DateTime manualSubmitUntil;
                DateTime now;
                string lastRendered;

                lock (_lock)
                {
                    if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
                    now = DateTime.UtcNow;
                    template = BuildEffectiveTemplate_NoLock(variableService, now);
                    activeVarsSnapshot = new Dictionary<string, DateTime>(_varTransientUntil, StringComparer.OrdinalIgnoreCase);
                    blockExpiriesSnapshot = new Dictionary<string, DateTime>(_blockTransientUntil, StringComparer.Ordinal);
                    manualSubmitUntil = _manualSubmitTransientUntil;
                    lastRendered = _lastRenderedText;
                }

                string processedTemplate = PrepareTemplateForRender(template, activeVarsSnapshot, blockExpiriesSnapshot, manualSubmitUntil, now);
                string rendered = variableService.Render(processedTemplate);

                // 如果渲染结果为空或与当前游戏内已呈现内容完全一致，则无需重复发送
                if (string.IsNullOrWhiteSpace(rendered) || string.Equals(rendered, lastRendered, StringComparison.Ordinal))
                {
                    continue;
                }

                // 速率控制：确保向 VRChat 发送的频率不超过限额 (至少间隔 MinSendIntervalMs)。
                // _lastOscSendTime 的初值是 DateTime.MinValue，elapsed 约为 6.4e13；
                // 若直接 (int)elapsed 会溢出成 int.MinValue，再做减法同样溢出为负数，
                // Task.Delay(负数) 会抛 ArgumentOutOfRangeException，导致首次变量更新
                // 永远推不出去（异常被 catch 吞成一行 Debug 输出）。
                var elapsed = (DateTime.UtcNow - _lastOscSendTime).TotalMilliseconds;
                if (_lastOscSendTime != DateTime.MinValue && elapsed < MinSendIntervalMs)
                {
                    int delayMs = (int)Math.Ceiling(MinSendIntervalMs - elapsed);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs);
                    }
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
    /// 判断当前常驻模板是否接管「我发送的消息」的显示。
    ///
    /// 条件：常驻已开启、模板非空。满足时发送消息不再直接写 /chatbox/input，
    /// 而是由常驻模板统一渲染、限流并维持气泡（所有消息单一出口）。
    /// </summary>
    public bool IsManagingSentMessages(VariableService variableService)
    {
        lock (_lock)
        {
            return IsEnabled && !string.IsNullOrWhiteSpace(CustomText);
        }
    }

    /// <summary>
    /// 构造实际用于渲染的模板：如果用户模板没有显式引用 {sent_text}/{sent_translation}，
    /// 就在末尾追加一个瞬态块来显示最近发送的消息/译文，保证“发送了但看不到”不会发生。
    /// 调用方需持有 _lock。
    /// </summary>
    private string BuildEffectiveTemplate_NoLock(VariableService variableService, DateTime now)
    {
        string template = CustomText;
        if (string.IsNullOrWhiteSpace(template)) return template;

        if (!variableService.UsesVariable(template, "sent_text") &&
            _varTransientUntil.TryGetValue(SentDisplayVar, out var sentUntil) && now < sentUntil)
        {
            template += " | [{" + SentDisplayVar + "}]";
        }

        if (!variableService.UsesVariable(template, "sent_translation") &&
            _varTransientUntil.TryGetValue(SentTransDisplayVar, out var transUntil) && now < transUntil)
        {
            template += " | [{" + SentTransDisplayVar + "}]";
        }

        return template;
    }

    /// <summary>
    /// 由发送流程调用：把“我发送的原文”并入常驻模板并立即推送一次。
    /// </summary>
    public async Task NotifySentMessageAsync(string text, VariableService variableService, OscService oscService)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_lock)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
            DateTime now = DateTime.UtcNow;
            _varTransientUntil[SentDisplayVar] = now.AddSeconds(_transientDurationSeconds);
            _varTransientUntil["sent_text"] = now.AddSeconds(_transientDurationSeconds);
            _varTransientUntil.Remove(SentTransDisplayVar);
            RefreshBlockExpiriesForVariable_NoLock(variableService, "sent_text", now);
        }

        variableService.SetVariable(SentDisplayVar, text);
        await ForceRenderAndSendAsync(variableService, oscService);
    }

    /// <summary>
    /// 翻译完成后调用：把“我发送消息的译文”并入常驻模板并立即推送一次。
    /// </summary>
    public async Task NotifySentTranslationAsync(string translation, VariableService variableService, OscService oscService)
    {
        if (string.IsNullOrWhiteSpace(translation)) return;

        lock (_lock)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
            DateTime now = DateTime.UtcNow;
            _varTransientUntil[SentTransDisplayVar] = now.AddSeconds(_transientDurationSeconds);
            _varTransientUntil["sent_translation"] = now.AddSeconds(_transientDurationSeconds);
            RefreshBlockExpiriesForVariable_NoLock(variableService, "sent_translation", now);
        }

        variableService.SetVariable(SentTransDisplayVar, translation);
        await ForceRenderAndSendAsync(variableService, oscService);
    }

    /// <summary>
    /// 立即按当前模板渲染并推送一次（用于发送消息/译文，绕过避让与 3 秒保活等待）。
    /// </summary>
    private async Task ForceRenderAndSendAsync(VariableService variableService, OscService oscService)
    {
        string template;
        Dictionary<string, DateTime> activeVarsSnapshot;
        Dictionary<string, DateTime> blockExpiriesSnapshot;
        DateTime manualSubmitUntil;
        DateTime now;

        lock (_lock)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
            now = DateTime.UtcNow;
            template = BuildEffectiveTemplate_NoLock(variableService, now);
            activeVarsSnapshot = new Dictionary<string, DateTime>(_varTransientUntil, StringComparer.OrdinalIgnoreCase);
            blockExpiriesSnapshot = new Dictionary<string, DateTime>(_blockTransientUntil, StringComparer.Ordinal);
            manualSubmitUntil = _manualSubmitTransientUntil;
        }

        string processed = PrepareTemplateForRender(template, activeVarsSnapshot, blockExpiriesSnapshot, manualSubmitUntil, now);
        string rendered = variableService.Render(processed);

        if (string.IsNullOrWhiteSpace(rendered))
        {
            await SendWithThrottleAsync(oscService, string.Empty);
            lock (_lock) { _lastRenderedText = string.Empty; }
            return;
        }

        await SendWithThrottleAsync(oscService, rendered);
        lock (_lock) { _lastRenderedText = rendered; }
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
        Dictionary<string, DateTime> blockExpiriesSnapshot;
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

            // 提交时默认激活模板中引用的所有变量初次展示（同样支持中文变量名）
            var allVarMatches = Regex.Matches(template, @"\{([a-zA-Z0-9_\-\u4e00-\u9fa5]+)\}");
            foreach (Match m in allVarMatches)
            {
                string vName = VariableService.GetCanonicalVariableName(m.Groups[1].Value);
                _varTransientUntil[vName] = now.AddSeconds(_transientDurationSeconds);
            }

            // 每个带 ##秒数 的 [] 块建立独立到期时间
            ResetBlockExpiriesForTemplate_NoLock(template, now);

            activeVarsSnapshot = new Dictionary<string, DateTime>(_varTransientUntil, StringComparer.OrdinalIgnoreCase);
            blockExpiriesSnapshot = new Dictionary<string, DateTime>(_blockTransientUntil, StringComparer.Ordinal);
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
        string processed = PrepareTemplateForRender(template, activeVarsSnapshot, blockExpiriesSnapshot, manualSubmitUntil, now);
        string rendered = variableService.Render(processed);

        // 立即向 VRChat 推送（走统一门控，避免与保活/变量推送叠加超限）
        bool sent = await SendWithThrottleAsync(oscService, rendered);

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
            // 清空常驻文本等同于关闭常驻，否则 UI 会继续显示“常驻保活中”，
            // 且 IsEnabled 与 CustomText 空值组合会让状态判断自相矛盾。
            IsEnabled = false;
            CustomText = string.Empty;
            _varTransientUntil.Clear();
            _blockTransientUntil.Clear();
            _manualSubmitTransientUntil = DateTime.MinValue;
            _lastRenderedText = string.Empty;
            StopLoopInternal();
        }
        // 清空气泡也走统一门控，避免与 3 秒保活 tick 紧邻时叠加超限
        await SendWithThrottleAsync(oscService, string.Empty);
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
                        Dictionary<string, DateTime> blockExpiriesSnapshot;
                        DateTime manualSubmitUntil;
                        DateTime now;
                        string previousRendered;

                        // 1. 锁内仅读取状态快照
                        lock (_lock)
                        {
                            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) continue;
                            now = DateTime.UtcNow;
                            template = BuildEffectiveTemplate_NoLock(variableService, now);
                            activeVarsSnapshot = new Dictionary<string, DateTime>(_varTransientUntil, StringComparer.OrdinalIgnoreCase);
                            blockExpiriesSnapshot = new Dictionary<string, DateTime>(_blockTransientUntil, StringComparer.Ordinal);
                            manualSubmitUntil = _manualSubmitTransientUntil;
                            previousRendered = _lastRenderedText;
                        }

                        // 2. 锁外执行避让检测
                        if (IsInAvoidance(out _, out _))
                        {
                            continue;
                        }

                        // 3. 锁外完成文本独立判定拼接与渲染
                        string processed = PrepareTemplateForRender(template, activeVarsSnapshot, blockExpiriesSnapshot, manualSubmitUntil, now);
                        string textToSend = variableService.Render(processed);

                        // 如果常驻内容在移除过期的 [] 之后变为空
                        if (string.IsNullOrWhiteSpace(textToSend))
                        {
                            if (!string.IsNullOrEmpty(previousRendered))
                            {
                                await SendWithThrottleAsync(oscService, string.Empty);
                                lock (_lock) { _lastRenderedText = string.Empty; }
                            }
                            continue;
                        }

                        // 循环保活推送：无声、不记录刷屏日志、绝对不发送打字动画。
                        // 经过统一门控，避免与变量推送叠加突破 VRChat 限额。
                        await SendWithThrottleAsync(oscService, textToSend);

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
