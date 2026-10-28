using System;
using System.Threading;
using System.Threading.Tasks;
using VrcChatboxDemo.Services.Text;

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

            // 常驻模板接管消息显示时，草稿预览也不再独立写 /chatbox/input，
            // 否则模板气泡会被原始草稿覆盖（“原本的发送逻辑”仍然存在）。
            bool templateManaged = PersistentTextService.IsManagingSentMessages(VariableService);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                StopTypingHeartbeat();
                _ = OscService.SendTypingAsync(false, recordLog: false);

                // 仅当用户此前确有正在实时预览的草稿文字且回退清空时，才发送清空消息
                // 如果用户刚发送完上一句话，或切换回窗口但尚未输入任何字符，绝不发送清空消息，完整保留上一句话气泡正常显示
                if (_hasActiveLiveText)
                {
                    _hasActiveLiveText = false;
                    if (!templateManaged)
                    {
                        _ = OscService.SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: false);
                    }
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

                    // 常驻模板接管时不写草稿预览；否则立即更新游戏内头顶气泡（逐字上屏，静音）
                    if (!templateManaged)
                    {
                        string textToSend = ChatTextNormalizer.ExpandExplicitNewlines(rawText);
                        await OscService.SendChatboxMessageAsync(textToSend, direct: true, playSound: false, recordLog: false);
                        _lastOscSendTime = DateTime.UtcNow;
                    }

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

    /// <summary>兼容旧调用点：144 字符截断规则已移入 <see cref="ChatTextNormalizer"/>。</summary>
    private static string TruncateForChatbox(string text) => ChatTextNormalizer.TruncateForChatbox(text);

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

                // 严格保证两次 /chatbox/input 间隔不低于 110ms，杜绝 VRChat 内部限流丢包。
                // 用 double 计算：_lastOscSendTime 的初值是 DateTime.MinValue，
                // 直接转 int 会溢出成 int.MinValue，110 - elapsed 再溢出为负数，
                // Task.Delay(负数) 会抛 ArgumentOutOfRangeException，表现为回车发送毫无反应。
                double elapsed = (DateTime.UtcNow - _lastOscSendTime).TotalMilliseconds;
                if (_lastOscSendTime != DateTime.MinValue && elapsed < 110)
                {
                    await Task.Delay(Math.Max(0, (int)(110 - elapsed)));
                }

                // 发送最终确认消息（包含声音配置并计入日志）。
                // 显式换行展开 + 144 字符截断统一走 ChatTextNormalizer，避免三处各写一份正则。
                string finalMsg = ChatTextNormalizer.PrepareForChatbox(text);

                // 常驻模板接管发送消息时，不再独立直发：
                // 只更新 {sent_text}，由模板统一渲染、限流并维持气泡。
                // 这样“手动发送 / 语音翻译 / 外部接入”最终都走同一个出口。
                bool routedToTemplate = PersistentTextService.IsManagingSentMessages(VariableService);
                bool result;
                if (routedToTemplate)
                {
                    VariableService.SetVariable("sent_text", finalMsg, "我发送的消息原文",
                        "本程序最近一次发送到 VRChat 的聊天文本（原文）");

                    // 由常驻模板统一推送：模板显式引用 {sent_text} 时按用户位置显示，
                    // 未引用时自动追加一个瞬态块，避免“发送了但看不到”。
                    await PersistentTextService.NotifySentMessageAsync(finalMsg, VariableService, OscService);
                    AddLog("Chat", $"消息已交给常驻模板显示: \"{finalMsg}\"", true);
                    result = true;
                }
                else
                {
                    result = await OscService.SendChatboxMessageAsync(finalMsg, IsDirectSendEnabled, IsSoundEnabled, recordLog: true);
                    _lastOscSendTime = DateTime.UtcNow;
                    PersistentTextService.NotifyUserSentMessage();
                    VariableService.SetVariable("sent_text", finalMsg, "我发送的消息原文",
                        "本程序最近一次发送到 VRChat 的聊天文本（原文）");
                }

                if (result && !string.IsNullOrWhiteSpace(finalMsg))
                {
                    // 原文朗读与“朗读译文”互斥：同时开启时优先朗读译文，
                    // 否则同一句话会被播报两遍。
                    bool willReadTranslation = IsTtsEnabled && IsTtsAutoReadSentTranslation
                                               && IsSentMessageTranslationEnabled;
                    if (IsTtsEnabled && IsTtsAutoReadSentMessage && !willReadTranslation)
                    {
                        string cleanTts = PrepareTextForTts(finalMsg);
                        if (!string.IsNullOrWhiteSpace(cleanTts))
                        {
                            _ = SpeakTextAsync(cleanTts);
                        }
                    }

                    // 自动翻译发送内容（结果写入 {sent_translation}，可选朗读）
                    if (IsSentMessageTranslationEnabled)
                    {
                        _ = TranslateSentMessageAsync(finalMsg);
                    }
                }

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
        string finalMsg = ChatTextNormalizer.PrepareForChatbox(text);
        return await OscService.SendChatboxMessageAsync(finalMsg, IsDirectSendEnabled, IsSoundEnabled);
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

    /// <summary>兼容旧调用点：声学清洗规则已移入 <see cref="ChatTextNormalizer"/>。</summary>
    public static string PrepareTextForTts(string input) => ChatTextNormalizer.PrepareForTts(input);
}
