using System;
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

        if (IsSpeechRecognitionEnabled)
        {
            await SpeechService.StopAsync();
            await SpeechService.StartAsync(SpeechLanguageTag, AudioInputDeviceId);
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
            await Task.WhenAny(cleanupTask, Task.Delay(500));
        }
        catch { }

        try
        {
            App.ReleaseSingleInstanceMutex();
        }
        catch { }

        await ShutdownAndKillProcess();
    }

    public async Task ShutdownAndKillProcess()
    {
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
