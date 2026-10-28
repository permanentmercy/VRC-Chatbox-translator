using System;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 「把设置应用到平台层」的动作集合：切换监听设备、重新注册全局热键。
///
/// 这两个方法原先放在 Lifecycle.cs 里，但它们与退出流程无关；
/// 生命周期文件现在只保留退出/清理相关逻辑。
/// </summary>
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

        // 若选择的是进程隔离监听，因微软 Windows 实时字幕原生仅监听全局扬声器，
        // 自动切换为 Whisper 本地 AI 引擎以实现绝对隔离
        if (AudioInputDeviceId.StartsWith("process:", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SpeechEngine, "LiveCaptions", StringComparison.OrdinalIgnoreCase))
        {
            SpeechEngine = "Whisper";
            SpeechStatusUpdated?.Invoke("已自动切换为 [Whisper 本地 AI 模型] 配合进程隔离 (过滤音乐与外部杂音)");
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

    /// <summary>按当前配置（重新）注册全局热键。</summary>
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
}
