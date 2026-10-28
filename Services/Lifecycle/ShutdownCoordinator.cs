using System;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services.Lifecycle;

/// <summary>
/// 退出流程需要的动作。抽成接口后，<see cref="ShutdownCoordinator"/> 只负责
/// 顺序、幂等与时间预算，不依赖 SettingsService 这个门面，也不依赖任何 WinUI 类型。
/// </summary>
public interface IShutdownContext
{
    /// <summary>把当前配置落盘（失败不应阻止退出）。</summary>
    void SaveConfiguration();

    /// <summary>释放 TTS（必须完成自有子进程清理，不受短预算限制）。</summary>
    void DisposeTts();

    /// <summary>卸载 Ollama 驻留模型；实现必须在该预算内返回，超时即放弃。</summary>
    Task UnloadOllamaModelsAsync(TimeSpan budget);

    /// <summary>停止语音/字幕/热键/外部接口等后台服务。</summary>
    Task ReleaseBackgroundServicesAsync();

    /// <summary>释放单实例互斥量，让用户能重新启动程序。</summary>
    void ReleaseSingleInstanceLock();

    /// <summary>停掉打字动画与常驻保活，并清空游戏内气泡。</summary>
    void StopTypingAndClearBubble();

    /// <summary>释放剩余服务（语音引擎、热键、外部接口、OSC）。</summary>
    void DisposeServices();
}

/// <summary>
/// 退出编排：保存配置 → 清理模型 → 停打字/清气泡 → 释放服务 → 终止进程。
///
/// 从 SettingsService 拆出来的原因：这是唯一一处「必须在进程被杀死之前按顺序跑完」
/// 的代码，混在门面里只能靠语法重写注入假终止器来测试。现在终止动作是构造参数，
/// 清理任务是可注入的上下文，11 个退出相关用例可以直接跑真实编排逻辑。
/// </summary>
public sealed class ShutdownCoordinator
{
    /// <summary>退出时留给语音/字幕/热键清理的时间。</summary>
    public static readonly TimeSpan BackgroundReleaseBudget = TimeSpan.FromMilliseconds(2000);

    /// <summary>「查询驻留模型 + 全部卸载」的总预算（不是每个模型各一份）。</summary>
    public static readonly TimeSpan OllamaUnloadBudget = TimeSpan.FromSeconds(3);

    private readonly IShutdownContext _context;
    private readonly Action _terminateProcess;
    private readonly object _cleanupLock = new();
    private Task? _cleanupTask;

    public ShutdownCoordinator(IShutdownContext context, Action terminateProcess)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _terminateProcess = terminateProcess ?? throw new ArgumentNullException(nameof(terminateProcess));
    }

    /// <summary>
    /// 模型清理（TTS 子进程 + Ollama 卸载）。窗口关闭与托盘退出共用同一个 Task，
    /// 保证只执行一次；重复调用直接返回进行中的那一份。
    /// </summary>
    public Task CleanupModelServicesAsync()
    {
        lock (_cleanupLock)
        {
            return _cleanupTask ??= Task.Run(CleanupModelServicesCoreAsync);
        }
    }

    private async Task CleanupModelServicesCoreAsync()
    {
        try { _context.DisposeTts(); } catch { }

        try
        {
            // 双重保险：即使上下文实现忽略了传入的预算（例如底层请求不响应取消），
            // 这里也会在预算到点后放行，绝不让退出流程无限挂起。
            await _context.UnloadOllamaModelsAsync(OllamaUnloadBudget).WaitAsync(OllamaUnloadBudget);
        }
        catch
        {
            // Ollama 不可达、响应挂起或卸载失败都不能阻止退出。
        }
    }

    /// <summary>
    /// 托盘「退出」：保存配置、给后台服务留出有界清理时间、释放互斥量，最后强制结束进程。
    /// </summary>
    public async Task QuitAsync()
    {
        try { _context.SaveConfiguration(); } catch { }

        try
        {
            var release = Task.Run(_context.ReleaseBackgroundServicesAsync);
            await Task.WhenAny(release, Task.Delay(BackgroundReleaseBudget));
        }
        catch { }

        try { _context.ReleaseSingleInstanceLock(); } catch { }

        await ShutdownAsync();
    }

    /// <summary>
    /// 窗口关闭路径：清理模型、停打字/清气泡、释放服务，然后终止进程。
    /// </summary>
    public async Task ShutdownAsync()
    {
        // 托盘退出不会触发 MainWindow.Closed，两条路径都必须等待同一份有界清理。
        await CleanupModelServicesAsync();

        try { _context.StopTypingAndClearBubble(); } catch { }

        try { _context.DisposeServices(); } catch { }

        _terminateProcess();
    }
}
