using System;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

public partial class SettingsService
{
    public async Task SwitchAudioDeviceAsync(string? endpointId, string deviceDisplayName)
    {
        AudioInputDeviceId = endpointId ?? string.Empty;
        if (!string.IsNullOrEmpty(endpointId))
        {
            SpeechStatusUpdated?.Invoke($"已选择扬声器监听设备: {deviceDisplayName}");
        }
        else
        {
            SpeechStatusUpdated?.Invoke("已选择系统默认设备 (自动跟随系统当前扬声器)");
        }

        // 若选择的是进程隔离监听，因微软 Windows 实时字幕原生仅监听全局扬声器，自动切换为 Whisper 本地 AI 引擎以实现绝对隔离
        if (AudioInputDeviceId.StartsWith("process:", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SpeechEngine, "LiveCaptions", StringComparison.OrdinalIgnoreCase))
        {
            SpeechEngine = "Whisper";
            SpeechStatusUpdated?.Invoke($"已自动切换为 [Whisper 本地 AI 模型] 配合进程隔离 (过滤音乐与外部杂音)");
        }

        if (LiveCaptionsAutoSwitcher.IsEnabled)
        {
            _ = LiveCaptionsAutoSwitcher.RebindAudioCaptureAsync();
        }

        if (IsSpeechRecognitionEnabled)
        {
            await ApplySpeechStateAsync();
        }
    }

    public void ApplyHotkey()
    {
        if (IsHotkeyEnabled)
        {
            HotkeyService.RegisterWake(CustomHotkeyModifiers, CustomHotkeyKey);
        }
        else
        {
            HotkeyService.UnregisterWake();
        }

        if (IsImmersiveHotkeyEnabled)
        {
            HotkeyService.RegisterImmersive(CustomImmersiveHotkeyModifiers, CustomImmersiveHotkeyKey);
        }
        else
        {
            HotkeyService.UnregisterImmersive();
        }
    }

    public async Task QuitApplicationAsync()
    {
        try
        {
            SaveConfigImmediately();
        }
        catch { }

        try
        {
            var cleanupTask = Task.Run(async () =>
            {
                try { PersistentTextService.Dispose(); } catch { }
                try { await SpeechService.StopAsync(); } catch { }
                try { SpeechService.Dispose(); } catch { }
                try { await LiveCaptionsService.StopAsync(); } catch { }
                try { LiveCaptionsService.Dispose(); } catch { }
                try { InboundService.Dispose(); } catch { }
                try { HotkeyService.Dispose(); } catch { }
                try { VrcInGameGuard.Dispose(); } catch { }
            });
            // 给语音/字幕/热键清理留出足够时间。500ms 对 LiveCaptions 进程终止、
            // Whisper 停止来说太短，会导致 ShutdownAndKillProcess 与清理任务并发 Dispose。
            await Task.WhenAny(cleanupTask, Task.Delay(2000));
        }
        catch { }

        try
        {
            App.ReleaseSingleInstanceMutex();
        }
        catch { }

        await ShutdownAndKillProcess();
    }

    private readonly object _exitModelCleanupLock = new();
    private Task? _exitModelCleanupTask;
    private readonly TimeSpan _exitOllamaUnloadTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 窗口关闭和托盘退出共用同一次模型清理，必须在强制结束进程之前等待。
    /// </summary>
    public Task CleanupModelServicesForExitAsync()
    {
        lock (_exitModelCleanupLock)
        {
            // 在后台执行同步 TTS 清理；发布同一个 Task，避免两个退出入口重复清理。
            return _exitModelCleanupTask ??= Task.Run(CleanupModelServicesForExitCoreAsync);
        }
    }

    private async Task CleanupModelServicesForExitCoreAsync()
    {
        // TTS 必须完成自有子进程清理，不与其它服务共用短时间预算。
        try { TtsService.Dispose(); } catch { }

        // 给“查询驻留模型 + 全部卸载”一个总预算，而不是每个模型各等一次超时。
        using var unloadBudget = new CancellationTokenSource(_exitOllamaUnloadTimeout);
        try
        {
            await OllamaService.UnloadAllModelsAsync(OllamaEndpoint, unloadBudget.Token)
                .WaitAsync(unloadBudget.Token);
        }
        catch
        {
            // Ollama 不可达、响应挂起或卸载失败都不能阻止退出；超时同时取消底层请求。
        }
    }

    public Task ShutdownAndKillProcess() => ShutdownAndKillProcess(TerminateCurrentProcess);

    // 将终止动作与清理分开，回归测试注入记录器，绝不真正结束测试/宿主进程。
    internal async Task ShutdownAndKillProcess(Action terminateProcess)
    {
        ArgumentNullException.ThrowIfNull(terminateProcess);
        // 托盘退出不会触发 MainWindow.Closed，也必须等待同一份有界 Ollama 清理。
        await CleanupModelServicesForExitAsync();

        try
        {
            SuppressTypingAnimation();
            PersistentTextService.StopLoop();
            _ = OscService.SendTypingAsync(false, recordLog: false);
            _ = OscService.ClearChatboxAsync();
            await Task.Delay(80);
        }
        catch { }

        try
        {
            SpeechService.Dispose();
        }
        catch { }

        try
        {
            LiveCaptionsService.Dispose();
        }
        catch { }

        try
        {
            HotkeyService.Dispose();
        }
        catch { }

        try
        {
            InboundService.Dispose();
        }
        catch { }

        try
        {
            OscService.Dispose();
        }
        catch { }

        terminateProcess();
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
