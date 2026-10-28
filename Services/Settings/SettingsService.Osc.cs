using System;
using System.Threading;
using System.Threading.Tasks;
using VrcChatboxDemo.Services.Messaging;
using VrcChatboxDemo.Services.Text;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 发送门面：把「发一条消息」的编排留在这里，把打字动画的时序逻辑交给
/// <see cref="TypingIndicator"/>，把文本规范化交给 <see cref="ChatTextNormalizer"/>。
///
/// 本文件剩下的职责只有三件：发送互斥（_sendSemaphore）、
/// 两次 /chatbox/input 之间的 110ms 保底间隔、发送成功后的朗读/翻译分派。
/// </summary>
public partial class SettingsService : ITypingContext
{
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private DateTime _lastOscSendTime = DateTime.MinValue;

    private TypingIndicator? _typingIndicator;
    private TypingIndicator Typing => _typingIndicator ??= new TypingIndicator(OscService, this);

    // ---- ITypingContext -------------------------------------------------
    // 显式实现：这些事实只服务于 TypingIndicator，不需要出现在门面的公开 API 上。
    bool ITypingContext.LiveTypingEnabled => IsLiveTypingEnabled;
    bool ITypingContext.TypingAnimationEnabled
    {
        get => IsTypingEnabled;
        set => IsTypingEnabled = value;
    }
    bool ITypingContext.IsTemplateManagingMessages => PersistentTextService.IsManagingSentMessages(VariableService);
    void ITypingContext.NotifyUserTyping(bool typing) => PersistentTextService.NotifyUserTyping(typing);

    /// <summary>用户输入变化：刷新草稿预览并维持打字中状态。</summary>
    public void UpdateLiveTyping(string rawText) => Typing.UpdateLiveTyping(rawText);

    /// <summary>强制停掉打字动画与心跳（不改用户的配置开关）。</summary>
    public void SuppressTypingAnimation() => Typing.Suppress();

    /// <summary>只发一次打字状态。</summary>
    public Task<bool> SetTypingAsync(bool isTyping) => Typing.SetTypingAsync(isTyping);

    /// <summary>
    /// 用户确认发送消息或语音识别/翻译定稿直接发送，停止打字动画并播放提示音，带速率防抖与发送互斥保底
    /// </summary>
    public async Task<bool> FinalizeSendAsync(string text)
    {
        await _sendSemaphore.WaitAsync();
        try
        {
            CancellationTokenSource? liveCts = Typing.BeginFinalSend();
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
                Typing.EndFinalSend();
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

    private void OnMessageSent(OscLogItem item)
    {
        AddLog(item.Address, item.Content, item.Success, item.Error);
    }

    /// <summary>兼容旧调用点：声学清洗规则已移入 <see cref="ChatTextNormalizer"/>。</summary>
    public static string PrepareTextForTts(string input) => ChatTextNormalizer.PrepareForTts(input);
}
