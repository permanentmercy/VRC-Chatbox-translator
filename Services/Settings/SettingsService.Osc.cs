using System;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public partial class SettingsService
{
    private readonly object _typingLock = new();
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private CancellationTokenSource? _liveTypingCts;
    private CancellationTokenSource? _typingHeartbeatCts;
    private bool _isSendingFinalMessage = false;
    private bool _hasActiveLiveText = false;
    private DateTime _lastOscSendTime = DateTime.MinValue;

    /// <summary>
    /// 当用户输入变化时，实时向 VRChat 发送当前内容并持续维持打字中状态，直到按下回车
    /// </summary>
    public void UpdateLiveTyping(string rawText)
    {
        lock (_typingLock)
        {
            if (_isSendingFinalMessage)
            {
                return;
            }

            _liveTypingCts?.Cancel();
            _liveTypingCts?.Dispose();
            _liveTypingCts = null;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                StopTypingHeartbeat();
                _ = OscService.SendTypingAsync(false, recordLog: false);

                // 仅当用户此前确有正在实时预览的草稿文字且回退清空时，才发送清空消息
                // 如果用户刚发送完上一句话，或切换回窗口但尚未输入任何字符，绝不发送清空消息，完整保留上一句话气泡正常显示
                if (_hasActiveLiveText)
                {
                    _hasActiveLiveText = false;
                    _ = OscService.SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: false);
                }
                PersistentTextService.NotifyUserTyping(false);
                return;
            }

            if (!IsLiveTypingEnabled)
            {
                if (IsTypingEnabled)
                {
                    _ = SetTypingAsync(!string.IsNullOrWhiteSpace(rawText));
                }
                return;
            }

            _hasActiveLiveText = true;
            PersistentTextService.NotifyUserTyping(true);

            var cts = new CancellationTokenSource();
            _liveTypingCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // 防抖 120ms，平滑快速按键并避免极端打字频率触碰 VRChat 9包/2秒 OSC限流
                    await Task.Delay(120, token);
                    if (token.IsCancellationRequested) return;

                    lock (_typingLock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) return;
                    }

                    // 立即更新游戏内头顶气泡（逐字上屏，静音，不刷屏日志）
                    await OscService.SendChatboxMessageAsync(rawText, direct: true, playSound: false, recordLog: false);
                    _lastOscSendTime = DateTime.UtcNow;

                    lock (_typingLock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) return;
                    }

                    // 维持打字动画状态
                    await OscService.SendTypingAsync(true, recordLog: false);

                    lock (_typingLock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) return;
                        EnsureTypingHeartbeat();
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    private void EnsureTypingHeartbeat()
    {
        if (_typingHeartbeatCts != null) return;
        if (_isSendingFinalMessage) return;

        _typingHeartbeatCts = new CancellationTokenSource();
        var token = _typingHeartbeatCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1500, token);
                    if (token.IsCancellationRequested) break;

                    lock (_typingLock)
                    {
                        if (_isSendingFinalMessage || token.IsCancellationRequested) break;
                    }

                    await OscService.SendTypingAsync(true, recordLog: false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void StopTypingHeartbeat()
    {
        _typingHeartbeatCts?.Cancel();
        _typingHeartbeatCts?.Dispose();
        _typingHeartbeatCts = null;
    }

    /// <summary>
    /// 强制抑制/停止当前可能正在运行的打字动画与心跳，向 VRChat 发送 /chatbox/typing false，但不修改用户的打字配置开关
    /// </summary>
    public void SuppressTypingAnimation()
    {
        lock (_typingLock)
        {
            _hasActiveLiveText = false;
            _liveTypingCts?.Cancel();
            _liveTypingCts?.Dispose();
            _liveTypingCts = null;
            StopTypingHeartbeat();
        }
        PersistentTextService.NotifyUserTyping(false);
        _ = OscService.SendTypingAsync(false, recordLog: false);
    }

    /// <summary>
    /// 用户确认发送消息或语音识别/翻译定稿直接发送，停止打字动画并播放提示音，带速率防抖与发送互斥保底
    /// </summary>
    public async Task<bool> FinalizeSendAsync(string text)
    {
        await _sendSemaphore.WaitAsync();
        try
        {
            CancellationTokenSource? liveCts;
            lock (_typingLock)
            {
                _isSendingFinalMessage = true;
                _hasActiveLiveText = false;
                liveCts = _liveTypingCts;
                _liveTypingCts = null;
                StopTypingHeartbeat();
            }
            PersistentTextService.NotifyUserTyping(false);

            try
            {
                liveCts?.Cancel();
                liveCts?.Dispose();

                // 立即停止打字状态动画
                await OscService.SendTypingAsync(false, recordLog: false);

                // 严格保证两次 /chatbox/input 间隔不低于 110ms，杜绝 VRChat 内部限流丢包
                int elapsed = (int)(DateTime.UtcNow - _lastOscSendTime).TotalMilliseconds;
                if (elapsed < 110)
                {
                    await Task.Delay(110 - elapsed);
                }

                // 发送最终确认消息（包含声音配置并计入日志）
                bool result = await OscService.SendChatboxMessageAsync(text, IsDirectSendEnabled, IsSoundEnabled, recordLog: true);
                _lastOscSendTime = DateTime.UtcNow;
                PersistentTextService.NotifyUserSentMessage();

                // 再次确保打字状态在发完后处于关闭
                await OscService.SendTypingAsync(false, recordLog: false);

                return result;
            }
            finally
            {
                lock (_typingLock)
                {
                    _isSendingFinalMessage = false;
                }
            }
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    public async Task<bool> SendMessageAsync(string text)
    {
        PersistentTextService.NotifyUserSentMessage();
        return await OscService.SendChatboxMessageAsync(text, IsDirectSendEnabled, IsSoundEnabled);
    }

    public async Task<bool> SetTypingAsync(bool isTyping)
    {
        IsTypingEnabled = isTyping;
        return await OscService.SendTypingAsync(isTyping);
    }

    private void OnMessageSent(OscLogItem item)
    {
        AddLog(item.Address, item.Content, item.Success, item.Error);
    }
}
