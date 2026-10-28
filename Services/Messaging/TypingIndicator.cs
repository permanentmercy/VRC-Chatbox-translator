using System;
using System.Threading;
using System.Threading.Tasks;
using VrcChatboxDemo.Services.Text;

namespace VrcChatboxDemo.Services.Messaging;

/// <summary>
/// 打字状态机提供方需要告诉指示器的少量事实与动作。
/// 抽成接口是为了让 <see cref="TypingIndicator"/> 不依赖 SettingsService 这个门面，
/// 从而可以脱离 WinUI 与单例直接测试。
/// </summary>
public interface ITypingContext
{
    /// <summary>「逐字上屏」开关：关闭时只维持打字动画，不写草稿气泡。</summary>
    bool LiveTypingEnabled { get; }

    /// <summary>「打字动画」开关。指示器在只发动画的路径上会读它，并在 SetTyping 时写回。</summary>
    bool TypingAnimationEnabled { get; set; }

    /// <summary>常驻模板是否接管了消息显示；接管时不得独立写 /chatbox/input。</summary>
    bool IsTemplateManagingMessages { get; }

    /// <summary>把「用户正在本软件里打字」的意图转达给常驻文本服务（用于避让）。</summary>
    void NotifyUserTyping(bool typing);
}

/// <summary>
/// VRChat 打字动画与「逐字上屏」草稿预览的完整状态机。
///
/// 从 SettingsService 拆出来的原因：这里全是时序逻辑（120ms 防抖、1.5s 心跳、
/// 取消令牌与最终发送的互斥），与配置读写、语音编排毫无关系，混在一起既难读也难测。
/// 依赖只有 <see cref="OscService"/> 与 <see cref="ITypingContext"/>。
/// </summary>
public sealed class TypingIndicator : IDisposable
{
    /// <summary>按键防抖：合并快速连续输入，避免触碰 VRChat 的 9 包 / 2 秒限制。</summary>
    public const int TypingDebounceMs = 120;

    /// <summary>打字动画心跳间隔（VRChat 约 3 秒会自行取消打字状态）。</summary>
    public const int HeartbeatIntervalMs = 1500;

    private readonly OscService _osc;
    private readonly ITypingContext _context;
    private readonly object _lock = new();

    private CancellationTokenSource? _liveTypingCts;
    private CancellationTokenSource? _heartbeatCts;
    private bool _isSendingFinalMessage;
    private bool _hasActiveLiveText;
    private bool _disposed;

    public TypingIndicator(OscService osc, ITypingContext context)
    {
        _osc = osc ?? throw new ArgumentNullException(nameof(osc));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>用户输入变化：刷新草稿预览并维持打字中状态。</summary>
    public void UpdateLiveTyping(string? rawText)
    {
        lock (_lock)
        {
            if (_disposed || _isSendingFinalMessage) return;

            CancelLiveTyping_NoLock();

            // 常驻模板接管消息显示时，草稿预览也不再独立写 /chatbox/input，
            // 否则模板气泡会被原始草稿覆盖。
            bool templateManaged = _context.IsTemplateManagingMessages;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                StopHeartbeat_NoLock();
                _ = _osc.SendTypingAsync(false, recordLog: false);

                // 只有此前确实在预览草稿时才发清空消息：用户刚发完上一句、或切回窗口还没输入，
                // 都不应该抹掉上一句气泡。
                if (_hasActiveLiveText)
                {
                    _hasActiveLiveText = false;
                    if (!templateManaged)
                    {
                        _ = _osc.SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: false);
                    }
                }
                _context.NotifyUserTyping(false);
                return;
            }

            if (!_context.LiveTypingEnabled)
            {
                if (_context.TypingAnimationEnabled)
                {
                    _ = SetTypingAsync(true);
                }
                return;
            }

            _hasActiveLiveText = true;
            _context.NotifyUserTyping(true);

            var cts = new CancellationTokenSource();
            _liveTypingCts = cts;
            var token = cts.Token;
            string text = rawText!;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TypingDebounceMs, token);
                    if (token.IsCancellationRequested) return;

                    lock (_lock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) return;
                    }

                    if (!templateManaged)
                    {
                        string textToSend = ChatTextNormalizer.ExpandExplicitNewlines(text);
                        await _osc.SendChatboxMessageAsync(textToSend, direct: true, playSound: false, recordLog: false);
                    }

                    lock (_lock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) return;
                    }

                    await _osc.SendTypingAsync(true, recordLog: false);

                    lock (_lock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) return;
                        EnsureHeartbeat_NoLock();
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
        }
    }

    /// <summary>只发一次打字状态（不影响心跳）。</summary>
    public async Task<bool> SetTypingAsync(bool isTyping)
    {
        _context.TypingAnimationEnabled = isTyping;
        return await _osc.SendTypingAsync(isTyping);
    }

    /// <summary>
    /// 进入「最终发送」：抑制一切草稿预览与心跳，并返回需要取消的旧令牌。
    /// 调用方必须在发送结束后调用 <see cref="EndFinalSend"/>。
    /// </summary>
    public CancellationTokenSource? BeginFinalSend()
    {
        CancellationTokenSource? liveCts;
        lock (_lock)
        {
            _isSendingFinalMessage = true;
            _hasActiveLiveText = false;
            liveCts = _liveTypingCts;
            _liveTypingCts = null;
            StopHeartbeat_NoLock();
        }
        _context.NotifyUserTyping(false);
        return liveCts;
    }

    /// <summary>最终发送结束，恢复可接收输入的状态。</summary>
    public void EndFinalSend()
    {
        lock (_lock)
        {
            _isSendingFinalMessage = false;
        }
    }

    /// <summary>
    /// 强制停掉打字动画与心跳（不影响用户的配置开关）。
    /// 用于提交常驻模板、清空输入等场景。
    /// </summary>
    public void Suppress()
    {
        lock (_lock)
        {
            _hasActiveLiveText = false;
            CancelLiveTyping_NoLock();
            StopHeartbeat_NoLock();
        }
        _context.NotifyUserTyping(false);
        _ = _osc.SendTypingAsync(false, recordLog: false);
    }

    private void EnsureHeartbeat_NoLock()
    {
        if (_disposed || _heartbeatCts != null || _isSendingFinalMessage) return;

        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatIntervalMs, token);
                    if (token.IsCancellationRequested) break;

                    lock (_lock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) break;
                    }

                    await _osc.SendTypingAsync(true, recordLog: false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    private void CancelLiveTyping_NoLock()
    {
        try
        {
            _liveTypingCts?.Cancel();
            _liveTypingCts?.Dispose();
        }
        catch { }
        _liveTypingCts = null;
    }

    private void StopHeartbeat_NoLock()
    {
        try
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
        }
        catch { }
        _heartbeatCts = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            CancelLiveTyping_NoLock();
            StopHeartbeat_NoLock();
        }
    }
}
