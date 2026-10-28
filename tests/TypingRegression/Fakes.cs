using VrcChatboxDemo.Services.Messaging;

namespace TypingRegressionTests;

/// <summary>测试用 ITypingContext：把「配置事实」变成可设置的属性，并记录转发给常驻服务的意图。</summary>
public sealed class FakeContext : ITypingContext
{
    public bool LiveTypingEnabled { get; set; } = true;
    public bool TypingAnimationEnabled { get; set; } = true;
    public bool IsTemplateManagingMessages { get; set; }

    public int NotifyCount { get; private set; }
    public bool? LastNotifyValue { get; private set; }

    public void NotifyUserTyping(bool typing)
    {
        NotifyCount++;
        LastNotifyValue = typing;
    }
}
