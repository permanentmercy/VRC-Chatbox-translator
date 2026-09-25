using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace VrcChatboxDemo.Services;

public record AudioProcessInfo(string ProcessName, string DisplayName, int Pid, bool IsRunning);

public static class AudioProcessService
{
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "devenv", "svchost", "csrss", "smss", "services", "lsass", "System", "Idle", "Registry",
        "dwm", "sihost", "taskhostw", "ShellExperienceHost", "StartMenuExperienceHost", "RuntimeBroker",
        "conhost", "explorer", "Taskmgr", "SearchHost", "LockApp", "TextInputHost", "SearchApp",
        "ctfmon", "SecurityHealthSystray", "SecurityHealthService", "smartscreen"
    };

    /// <summary>
    /// 获取候选音频隔离进程列表：
    /// 1. 当前已配置的目标进程（置顶显示，标明运行中或未找到）；
    /// 2. 具有活动系统音频会话的进程（如正在发声的游戏、播放器、语音软件、浏览器等）；
    /// 3. 具有可见主窗口的应用程序进程；
    /// 4. 常见游戏预设（如 VRChat）。
    /// </summary>
    public static List<AudioProcessInfo> GetCandidateProcesses(string configuredProcessName)
    {
        var result = new List<AudioProcessInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string targetName = NormalizeProcessName(configuredProcessName);
        if (string.IsNullOrWhiteSpace(targetName)) targetName = "VRChat";

        // 1. 检查已配置的目标进程状态并置顶
        var targetProcs = Process.GetProcessesByName(targetName);
        if (targetProcs.Length > 0)
        {
            var p = targetProcs[0];
            result.Add(new AudioProcessInfo(targetName, $"{targetName} (PID: {p.Id}，运行中)", p.Id, true));
        }
        else
        {
            result.Add(new AudioProcessInfo(targetName, $"{targetName} (未找到 / 未运行)", 0, false));
        }
        seenNames.Add(targetName);

        // 2. 查询当前正在发声的音频会话进程
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = defaultDevice.AudioSessionManager;
            if (sessionManager != null)
            {
                var sessions = sessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    int pid = (int)s.GetProcessID;
                    if (pid <= 4) continue;

                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        string pName = p.ProcessName;
                        if (!seenNames.Contains(pName) && !IgnoredProcesses.Contains(pName))
                        {
                            seenNames.Add(pName);
                            string title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? string.Empty : $" - {p.MainWindowTitle}";
                            result.Add(new AudioProcessInfo(pName, $"{pName} (PID: {pid}{title})", pid, true));
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 3. 查询具有可见窗口的用户级进程
        try
        {
            var allProcs = Process.GetProcesses();
            foreach (var p in allProcs)
            {
                try
                {
                    string pName = p.ProcessName;
                    if (!seenNames.Contains(pName) &&
                        !IgnoredProcesses.Contains(pName) &&
                        !string.IsNullOrEmpty(p.MainWindowTitle))
                    {
                        seenNames.Add(pName);
                        result.Add(new AudioProcessInfo(pName, $"{pName} (PID: {p.Id} - {p.MainWindowTitle})", p.Id, true));
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        // 4. 若 VRChat 仍未在其中，追加预设
        if (!seenNames.Contains("VRChat"))
        {
            var vrProcs = Process.GetProcessesByName("VRChat");
            if (vrProcs.Length > 0)
            {
                result.Add(new AudioProcessInfo("VRChat", $"VRChat (PID: {vrProcs[0].Id}，运行中)", vrProcs[0].Id, true));
            }
            else
            {
                result.Add(new AudioProcessInfo("VRChat", "VRChat (未找到 / 未运行)", 0, false));
            }
        }

        return result;
    }

    /// <summary>
    /// 检查指定进程是否正在运行，返回其 PID
    /// </summary>
    public static bool IsProcessRunning(string processName, out int pid)
    {
        pid = 0;
        string clean = NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(clean)) return false;

        var procs = Process.GetProcessesByName(clean);
        if (procs.Length > 0)
        {
            pid = procs[0].Id;
            return true;
        }
        return false;
    }

    public static string NormalizeProcessName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        string clean = rawName.Trim();
        // 移除可能附带的说明后缀或 .exe
        int parenIdx = clean.IndexOf('(');
        if (parenIdx > 0)
        {
            clean = clean.Substring(0, parenIdx).Trim();
        }
        int spaceIdx = clean.IndexOf(' ');
        if (spaceIdx > 0)
        {
            clean = clean.Substring(0, spaceIdx).Trim();
        }
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(0, clean.Length - 4);
        }
        return clean;
    }
}
