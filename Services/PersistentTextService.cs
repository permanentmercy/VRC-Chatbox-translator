using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 独立的头顶常驻自定义文本服务
/// 负责定时循环刷新 VRChat 头顶气泡以维持常驻，并与普通聊天打字、语音识别自动填入做严格隔离与状态互斥
/// </summary>
public class PersistentTextService : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _loopCts;
    private DateTime _lastUserSentTime = DateTime.MinValue;
    private bool _isUserTyping = false;

    public bool IsEnabled { get; set; } = false;
    public string CustomText { get; set; } = string.Empty;

    public event Action<string>? StatusNotice;

    public void NotifyUserSentMessage()
    {
        lock (_lock)
        {
            _lastUserSentTime = DateTime.UtcNow;
            _isUserTyping = false;
        }
    }

    public void NotifyUserTyping(bool isTyping)
    {
        lock (_lock)
        {
            _isUserTyping = isTyping;
        }
    }

    /// <summary>
    /// 用户在界面上点击【提交】按钮时调用：
    /// 1. 强制激活常驻状态并更新文本；
    /// 2. 强力终止所有打字动画与心跳；
    /// 3. 立即强制向 VRChat 发送文本气泡，并确保无打字动画；
    /// 4. 启动常驻定时保活循环。
    /// </summary>
    public async Task<bool> SubmitAsync(string text, OscService oscService, SettingsService settingsService)
    {
        text = text?.Trim() ?? string.Empty;
        lock (_lock)
        {
            IsEnabled = !string.IsNullOrWhiteSpace(text);
            CustomText = text;
        }

        // 强行终止并重置可能残留的任何打字预览与心跳
        settingsService.SuppressTypingAnimation();

        if (string.IsNullOrWhiteSpace(text))
        {
            await ClearAsync(oscService);
            StatusNotice?.Invoke("常驻文本已清空");
            return false;
        }

        // 立即强制向 VRChat 推送（双保险确保无打字动画）
        await oscService.SendTypingAsync(false, recordLog: false);
        await Task.Delay(40);
        bool sent = await oscService.SendChatboxMessageAsync(text, direct: true, playSound: false, recordLog: false);
        await Task.Delay(40);
        await oscService.SendTypingAsync(false, recordLog: false);

        RestartLoop(oscService);
        StatusNotice?.Invoke("已成功提交至游戏头顶气泡");
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
            StopLoopInternal();
        }
        await oscService.SendChatboxMessageAsync(string.Empty, direct: true, playSound: false, recordLog: false);
        await oscService.SendTypingAsync(false, recordLog: false);
    }

    public void RestartLoop(OscService oscService)
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
                        // 每 5 秒检测并保活一次（VRChat 气泡约 9 秒自动消失）
                        await Task.Delay(5000, token);
                        if (token.IsCancellationRequested) break;

                        string textToSend;
                        lock (_lock)
                        {
                            if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) continue;

                            // 避让玩家手动发送的正式聊天消息 9 秒
                            if ((DateTime.UtcNow - _lastUserSentTime).TotalSeconds < 9.0) continue;

                            // 避让玩家当前正在输入的草稿
                            if (_isUserTyping) continue;

                            textToSend = CustomText;
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
