using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace VrcChatboxDemo.Services.Tts;

/// <summary>
/// TTS 的设备相关逻辑（分部类，机械拆分）：播放设备枚举、虚拟声卡识别、
/// 播放/监听端点解析与 COM 资源释放。
///
/// 从 TtsService.cs 拆出来的原因：这部分只跟 WASAPI 端点打交道，
/// 与进程管理、HTTP 合成、音频播放管线没有交集，混在一起时文件难以导航。
/// 所有字段仍在 TtsService.cs 中（partial 共享），本次不改变任何行为。
/// </summary>
public sealed partial class TtsService
{
    public List<TtsDeviceInfo> GetPlaybackDevices()
    {
        var devices = new List<TtsDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultRenderId = string.Empty;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultRenderId = def.ID;
            }
            catch { }

            // 端点集合本身是 COM 对象，枚举完必须释放；只释放每个 MMDevice 会留下集合泄漏。
            using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in collection)
            {
                try
                {
                    bool isDef = string.Equals(d.ID, defaultRenderId, StringComparison.OrdinalIgnoreCase);
                    string name = d.FriendlyName ?? "未知设备";
                    bool isVirtual = IsVirtualAudioDevice(name);

                    devices.Add(new TtsDeviceInfo(
                        Id: d.ID,
                        Name: (isVirtual ? "★ [推荐虚拟声卡] " : "") + name,
                        IsVirtualRecommendation: isVirtual,
                        IsDefault: isDef
                    ));
                }
                finally
                {
                    d.Dispose();
                }
            }

            // 排序：优先将推荐的虚拟声卡排在最前
            devices.Sort((a, b) =>
            {
                if (a.IsVirtualRecommendation && !b.IsVirtualRecommendation) return -1;
                if (!a.IsVirtualRecommendation && b.IsVirtualRecommendation) return 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"枚举播放设备异常: {ex.Message}");
        }

        return devices;
    }

    private static bool IsVirtualAudioDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lower = name.ToLowerInvariant();

        // 只认真正的虚拟声卡/虚拟线缆。不能用 "line " 这种宽泛子串：
        // 物理设备 "Line Out (Realtek Audio)" 会被误判成虚拟声卡，
        // 导致推流目标被强制指向物理输出，VRChat 完全收不到 TTS 声音。
        return lower.Contains("cable") ||
               lower.Contains("virtual") ||
               lower.Contains("voicemeeter") ||
               lower.Contains("vb-audio") ||
               lower.Contains("vb-cable") ||
               lower.Contains("blackhole") ||
               lower.Contains("loopback");
    }

    public static MMDevice? FindFirstVirtualAudioDevice(MMDeviceEnumerator enumerator)
    {
        MMDeviceCollection? collection = null;
        try
        {
            collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in collection)
            {
                bool isVirtual;
                try
                {
                    isVirtual = IsVirtualAudioDevice(d.FriendlyName);
                }
                catch
                {
                    // 读取设备属性失败（驱动异常/设备已拔出）时仍要释放该包装，否则 COM 句柄累积。
                    d.Dispose();
                    throw;
                }

                if (isVirtual) return d;

                // 扫描过程中不选中的设备必须立即释放：这里返回后调用方只拥有一个设备，
                // 旧实现把沿途枚举出的所有 MMDevice 全部泄漏。
                d.Dispose();
            }
        }
        catch { }
        finally
        {
            try { collection?.Dispose(); } catch { }
        }
        return null;
    }

    private static (MMDevice? micDevice, MMDevice? monDevice, bool isSameDevice) ResolveDevices(
        MMDeviceEnumerator enumerator,
        string? virtualMicDeviceId,
        string? monitorDeviceId,
        bool enableMonitor)
    {
        MMDevice? micDevice = null;
        MMDevice? monDevice = null;

        if (!string.IsNullOrEmpty(virtualMicDeviceId))
        {
            try { micDevice = enumerator.GetDevice(virtualMicDeviceId); } catch { }
        }

        if (enableMonitor && !string.IsNullOrEmpty(monitorDeviceId))
        {
            try { monDevice = enumerator.GetDevice(monitorDeviceId); } catch { }
        }

        MMDevice? defaultDevice = null;
        try
        {
            defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch { }

        // 1. 如果未指定虚拟麦克风或指定设备失效，优先自动在系统中查找虚拟声卡 (如 CABLE Input、Virtual Audio Cable、VoiceMeeter 等)
        if (micDevice == null)
        {
            micDevice = FindFirstVirtualAudioDevice(enumerator);
        }

        // 2. 如果依然未找到虚拟声卡，才回退到默认输出设备
        micDevice ??= defaultDevice;

        // 3. 耳返监听设备：默认为系统默认播放设备（物理耳机/扬声器）
        if (enableMonitor)
        {
            monDevice ??= defaultDevice;
        }

        bool isSameDevice = enableMonitor && micDevice != null && monDevice != null &&
                            string.Equals(micDevice.ID, monDevice.ID, StringComparison.OrdinalIgnoreCase);

        // 默认设备只在真正被采用时交给调用方释放；显式指定了麦克风/监听设备时，
        // 这个额外打开的默认端点必须在这里释放，否则每次播放都泄漏一个 MMDevice。
        if (defaultDevice != null &&
            !ReferenceEquals(defaultDevice, micDevice) &&
            !ReferenceEquals(defaultDevice, monDevice))
        {
            try { defaultDevice.Dispose(); } catch { }
        }

        return (micDevice, monDevice, isSameDevice);
    }

    /// <summary>
    /// 释放 ResolveDevices 返回的 COM 设备。mic/mon 可能指向同一个 MMDevice，
    /// 引用相同时只释放一次，避免重复 Dispose。
    /// </summary>
    private static void DisposeDevices(MMDevice? micDevice, MMDevice? monDevice)
    {
        try { micDevice?.Dispose(); } catch { }
        if (!ReferenceEquals(micDevice, monDevice))
        {
            try { monDevice?.Dispose(); } catch { }
        }
    }
}
