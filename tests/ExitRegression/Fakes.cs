using VrcChatboxDemo.Services.Lifecycle;

namespace ExitRegressionTests;

/// <summary>
/// 可编排的退出上下文：记录调用顺序，并允许把某一步做成挂起/抛异常。
/// 真实实现里的 Win32/HTTP 细节全部被隔离在外，测试只验证编排语义。
/// </summary>
public sealed class FakeShutdownContext : IShutdownContext
{
    public List<string> Calls { get; } = new();
    public int DisposeTtsCount { get; private set; }
    public int SaveCount { get; private set; }
    public int ReleaseLockCount { get; private set; }
    public int StopTypingCount { get; private set; }
    public int DisposeServicesCount { get; private set; }
    public int BackgroundReleaseCount { get; private set; }
    public TimeSpan? LastOllamaBudget { get; private set; }

    /// <summary>非空时 DisposeTts 抛异常（模拟 TTS 清理失败）。</summary>
    public Exception? DisposeTtsError { get; set; }

    /// <summary>非空时 Ollama 卸载等待它（模拟不响应取消的请求）。</summary>
    public TaskCompletionSource<bool>? OllamaGate { get; set; }

    /// <summary>非空时 Ollama 卸载抛异常（模拟服务不可达）。</summary>
    public Exception? OllamaError { get; set; }

    /// <summary>非空时后台服务释放等待它（模拟进程终止卡住）。</summary>
    public TaskCompletionSource<bool>? BackgroundGate { get; set; }

    public void SaveConfiguration()
    {
        SaveCount++;
        Calls.Add("save");
    }

    public void DisposeTts()
    {
        DisposeTtsCount++;
        Calls.Add("dispose-tts");
        if (DisposeTtsError != null) throw DisposeTtsError;
    }

    public async Task UnloadOllamaModelsAsync(TimeSpan budget)
    {
        LastOllamaBudget = budget;
        Calls.Add("unload-ollama");
        if (OllamaError != null) throw OllamaError;
        if (OllamaGate != null) await OllamaGate.Task;
    }

    public async Task ReleaseBackgroundServicesAsync()
    {
        BackgroundReleaseCount++;
        Calls.Add("release-background");
        if (BackgroundGate != null) await BackgroundGate.Task;
    }

    public void ReleaseSingleInstanceLock()
    {
        ReleaseLockCount++;
        Calls.Add("release-lock");
    }

    public void StopTypingAndClearBubble()
    {
        StopTypingCount++;
        Calls.Add("stop-typing");
    }

    public void DisposeServices()
    {
        DisposeServicesCount++;
        Calls.Add("dispose-services");
    }
}
