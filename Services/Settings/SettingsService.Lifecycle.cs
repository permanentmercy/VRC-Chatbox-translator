using System;
using System.Threading;
using System.Threading.Tasks;
using VrcChatboxDemo.Services.Lifecycle;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 退出流程门面：保留原有公开方法名，实际编排交给 <see cref="ShutdownCoordinator"/>。
/// 本文件只实现 <see cref="IShutdownContext"/> 要求的「具体动作」。
/// </summary>
public partial class SettingsService : IShutdownContext
{
    private ShutdownCoordinator? _shutdown;

    private ShutdownCoordinator Shutdown => _shutdown ??= new ShutdownCoordinator(this, TerminateCurrentProcess);

    public Task QuitApplicationAsync() => Shutdown.QuitAsync();

    /// <summary>窗口关闭和托盘退出共用同一次模型清理，必须在强制结束进程之前等待。</summary>
    public Task CleanupModelServicesForExitAsync() => Shutdown.CleanupModelServicesAsync();

    public Task ShutdownAndKillProcess() => Shutdown.ShutdownAsync();

    /// <summary>
    /// 供回归测试注入终止器：用同一套编排逻辑，但绝不真正结束进程。
    /// </summary>
    internal Task ShutdownAndKillProcess(Action terminateProcess) =>
        new ShutdownCoordinator(this, terminateProcess).ShutdownAsync();

    // ---- IShutdownContext -------------------------------------------------

    void IShutdownContext.SaveConfiguration() => SaveConfigImmediately();

    void IShutdownContext.DisposeTts()
    {
        // TTS 必须完成自有子进程清理，不与其它服务共用短时间预算。
        try { TtsService.Dispose(); } catch { }
    }

    async Task IShutdownContext.UnloadOllamaModelsAsync(TimeSpan budget)
    {
        // 给「查询驻留模型 + 全部卸载」一个总预算，而不是每个模型各等一次超时。
        using var unloadBudget = new CancellationTokenSource(budget);
        await OllamaService.UnloadAllModelsAsync(OllamaEndpoint, unloadBudget.Token)
            .WaitAsync(unloadBudget.Token);
    }

    async Task IShutdownContext.ReleaseBackgroundServicesAsync()
    {
        try { PersistentTextService.Dispose(); } catch { }
        try { await SpeechService.StopAsync(); } catch { }
        try { SpeechService.Dispose(); } catch { }
        try { await LiveCaptionsService.StopAsync(); } catch { }
        try { LiveCaptionsService.Dispose(); } catch { }
        try { InboundService.Dispose(); } catch { }
        try { HotkeyService.Dispose(); } catch { }
        try { VrcInGameGuard.Dispose(); } catch { }
    }

    void IShutdownContext.ReleaseSingleInstanceLock() => App.ReleaseSingleInstanceMutex();

    void IShutdownContext.StopTypingAndClearBubble()
    {
        SuppressTypingAnimation();
        PersistentTextService.StopLoop();
        _ = OscService.SendTypingAsync(false, recordLog: false);
        _ = OscService.ClearChatboxAsync();
        // 给 UDP 包一点时间发出去再杀进程
        Task.Delay(80).GetAwaiter().GetResult();
    }

    void IShutdownContext.DisposeServices()
    {
        try { SpeechService.Dispose(); } catch { }
        try { LiveCaptionsService.Dispose(); } catch { }
        try { HotkeyService.Dispose(); } catch { }
        try { InboundService.Dispose(); } catch { }
        try { OscService.Dispose(); } catch { }
    }

    private static void TerminateCurrentProcess()
    {
        try
        {
            // 立即彻底终结进程，防止未托管底层线程（如 CUDA/Whisper 驱动层）挂起主进程
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        catch
        {
            Environment.Exit(0);
        }
    }
}
