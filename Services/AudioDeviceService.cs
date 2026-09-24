#pragma warning disable CA1416

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace VrcChatboxDemo.Services;

public record AudioDeviceInfo(string Id, string EndpointId, string Name, bool IsDefault);

public static class AudioDeviceService
{
    public static bool IsCaptureEndpoint(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return false;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var dev = enumerator.GetDevice(endpointId);
            return dev.DataFlow == DataFlow.Capture;
        }
        catch
        {
            return endpointId.Contains("{0.0.1.");
        }
    }

    /// <summary>
    /// 获取当前系统中所有活跃的音频播放设备 (扬声器/耳机) 与音频捕获设备 (麦克风)
    /// </summary>
    public static Task<List<AudioDeviceInfo>> GetAudioRenderDevicesAsync()
    {
        return Task.Run(() =>
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                var enumerator = new MMDeviceEnumerator();
                string defaultRenderId = string.Empty;
                string defaultCaptureId = string.Empty;

                try
                {
                    var defRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultRenderId = defRender.ID;
                }
                catch { }

                try
                {
                    var defCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    defaultCaptureId = defCapture.ID;
                }
                catch { }

                // 1. 扬声器 / 耳机 (Render)
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    bool isDef = !string.IsNullOrEmpty(defaultRenderId) && string.Equals(d.ID, defaultRenderId, StringComparison.OrdinalIgnoreCase);
                    list.Add(new AudioDeviceInfo(d.ID, d.ID, $"[扬声器/耳机] {d.FriendlyName}", isDef));
                }

                // 2. 麦克风 (Capture)
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    bool isDef = !string.IsNullOrEmpty(defaultCaptureId) && string.Equals(d.ID, defaultCaptureId, StringComparison.OrdinalIgnoreCase);
                    list.Add(new AudioDeviceInfo(d.ID, d.ID, $"[麦克风] {d.FriendlyName}", isDef));
                }
            }
            catch { }

            return list;
        });
    }

    public static async Task OpenSoundSettingsAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound"));
        }
        catch { }
    }
}
