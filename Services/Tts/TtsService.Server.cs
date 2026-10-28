using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VrcChatboxDemo.Services.Synthesis;

namespace VrcChatboxDemo.Services.Tts;

/// <summary>
/// TTS 服务端进程生命周期（分部类，机械拆分）：托管启动、健康轮询、只终止自有进程。
///
/// 从 TtsService.cs 拆出来的原因：这段是「启动一个 Python 子进程并等它加载模型」的完整状态机
/// （非阻塞启动锁、generation 守卫、35 秒轮询、意外退出与超时的区分），
/// 与 HTTP 合成、音频播放没有交集。所有字段仍在 TtsService.cs 中（partial 共享），
/// 本次不改变任何行为。
/// </summary>
public sealed partial class TtsService
{
    /// <summary>
    /// 托管启动后台 IndexTTS 1.5 Python 服务
    /// </summary>
    public async Task<bool> StartManagedServerAsync()
    {
        // 0. 若当前正在启动中，直接返回，避免并发重复启动导致双进程爆显存
        if (State == TtsServerState.Starting)
        {
            LogOccurred?.Invoke("[Server] 引擎正在启动中，请等待其加载完成，无需重复发起启动。");
            return false;
        }

        // 使用非阻塞获取：启动流程最长会轮询 35 秒（模型加载），
        // 若这里用 WaitAsync() 阻塞，UI 线程上的第二次点击会一直卡住且没有任何反馈。
        if (!await _serverStartLock.WaitAsync(0))
        {
            LogOccurred?.Invoke("[Server] 已有启动流程在进行中，请等待其完成。");
            return false;
        }

        Process? startedProc = null;
        long generation;
        lock (_serverProcessLock)
        {
            generation = _serverGeneration;
        }
        try
        {
            if (Volatile.Read(ref _disposeStarted) != 0 || State == TtsServerState.Starting)
            {
                return false;
            }

            var cfg = SettingsService.Instance.Config;
            if (SettingsService.MigrateLegacyTtsPaths(cfg))
            {
                SettingsService.Instance.SaveConfigDebounced();
            }

            // 1. 先探测是否已经有运行中的服务（例如用户此前已启动或独立启动）
            var health = await CheckHealthAsync(cfg.TtsServerEndpoint);
            lock (_serverProcessLock)
            {
                if (_serverGeneration != generation || Volatile.Read(ref _disposeStarted) != 0) return false;
                if (health != null && health.Ready)
                {
                    string modeDesc = health.LowVram ? "低显存模式" : "流式全显存模式";
                    SetState(TtsServerState.Ready, $"已成功连接到运行中的 IndexTTS 服务 (模型: {health.Model}, {modeDesc})");
                    return true;
                }
            }

            // 2. 检查 Python 解释器与服务端脚本是否存在
            if (!File.Exists(cfg.TtsPythonExePath))
            {
                SetState(TtsServerState.Error, $"未找到 Python 解释器: {cfg.TtsPythonExePath}");
                return false;
            }
            if (!File.Exists(cfg.TtsServerScriptPath))
            {
                SetState(TtsServerState.Error, $"未找到服务端脚本: {cfg.TtsServerScriptPath}");
                return false;
            }

            // 只回收本实例之前创建的进程。端口占用或同名脚本不代表进程属于本应用。
            Process? previous;
            lock (_serverProcessLock)
            {
                if (_serverGeneration != generation || Volatile.Read(ref _disposeStarted) != 0) return false;
                previous = _serverProcess;
                _serverProcess = null;
            }
            await Task.Run(() => StopOwnedProcess(previous));

            lock (_serverProcessLock)
            {
                if (_serverGeneration != generation || Volatile.Read(ref _disposeStarted) != 0) return false;
                SetState(TtsServerState.Starting, "正在启动 IndexTTS 后台服务并加载模型 (耗时约 15~20 秒)...");
            }

            string workDir = Path.GetDirectoryName(cfg.TtsServerScriptPath) ?? "";

            // 把所有扫描到的权重一次性注册给服务端（--models 别名=文件名）。
            // 这样界面上切换权重时服务端只需要重载 GPT 主干（几秒），不用整个重启（十几秒）。
            // 服务端不支持 --models 时会忽略（脚本用 parse_known_args），退化成只认 --model。
            string registryArgs = BuildModelRegistryArgs(cfg.TtsModelName, cfg.TtsServerScriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = cfg.TtsPythonExePath,
                Arguments = $"\"{cfg.TtsServerScriptPath}\" --port {cfg.TtsServerPort} " +
                            $"--model \"{cfg.TtsModelName}\"{registryArgs}",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            startedProc = proc;

            proc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    LogOccurred?.Invoke($"[PyServer] {e.Data}");
                }
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    LogOccurred?.Invoke($"[PyServer Err] {e.Data}");
                }
            };

            proc.EnableRaisingEvents = true;
            proc.Exited += (s, e) =>
            {
                lock (_serverProcessLock)
                {
                    if (_serverProcess == proc)
                    {
                        // 保留句柄直至显式停止/再次启动，以便确定归属并统一释放。
                        SetState(TtsServerState.Stopped, "TTS 引擎已停止");
                    }
                }
            };

            lock (_serverProcessLock)
            {
                if (_serverGeneration != generation || Volatile.Read(ref _disposeStarted) != 0)
                {
                    proc.Dispose();
                    return false;
                }
                // 启动与登记必须原子化：Stop/Dispose 不能漏掉尚未登记的新进程。
                proc.Start();
                _serverProcess = proc;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }

            // 4. 异步轮询等待就绪状态 (最多等待 35 秒)
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 35000)
            {
                await Task.Delay(1000);

                // StopManagedServer 会先把 _serverProcess 置空再 Dispose 进程。
                // 一旦发现托管进程已被替换/停止，就必须立即退出启动等待，
                // 否则 proc.HasExited 在 Dispose 后只会抛异常并被吞掉，循环会一直
                // 跑到 35 秒超时，把“用户主动停止”误报成“启动超时”。
                if (_serverProcess != proc)
                {
                    return false;
                }

                bool hasExited = false;
                int exitCode = -1;
                try
                {
                    hasExited = proc.HasExited;
                    if (hasExited) exitCode = proc.ExitCode;
                }
                catch
                {
                    // 避免并发停止时抛出 No process is associated with this object
                }

                if (hasExited)
                {
                    if (_serverProcess == null || _serverProcess != proc)
                    {
                        // 用户主动停止或清理，不再上报为意外退出
                        return false;
                    }
                    SetState(TtsServerState.Error, $"TTS 引擎进程意外退出，退出码: {exitCode}");
                    return false;
                }

                health = await CheckHealthAsync(cfg.TtsServerEndpoint);
                lock (_serverProcessLock)
                {
                    if (_serverProcess != proc || _serverGeneration != generation) return false;
                    if (health != null && health.Ready)
                    {
                        string modeDesc = health.LowVram ? "低显存模式" : "流式全显存模式";
                        SetState(TtsServerState.Ready, $"IndexTTS 引擎加载就绪 (模型: {health.Model}, {modeDesc})");
                        return true;
                    }
                }
            }

            // 超时也可能恰好发生在用户停止的同一瞬间；再确认一次托管进程仍是本次启动的进程。
            if (_serverProcess != proc)
            {
                return false;
            }

            SetState(TtsServerState.Error, "启动超时：35 秒内未能完成权重加载");
            return false;
        }
        catch (Exception ex)
        {
            // 异常时也只终止本次创建的进程，不接管任何外部服务。
            lock (_serverProcessLock)
            {
                if (_serverProcess == startedProc) _serverProcess = null;
            }
            await Task.Run(() => StopOwnedProcess(startedProc));
            lock (_serverProcessLock)
            {
                if (_serverGeneration == generation && Volatile.Read(ref _disposeStarted) == 0)
                    SetState(TtsServerState.Error, $"启动异常: {ex.Message}");
            }
            return false;
        }
        finally
        {
            _serverStartLock.Release();
        }
    }

    /// <summary>
    /// 拼出 ``--models "a.pth=a.pth,b.pth=b.pth" --default-model "a.pth"`` 参数串。
    ///
    /// 别名直接就用文件名，省掉一层映射。若当前选中的权重不在目录里（文件被删/改名），
    /// **必须省略 --default-model** —— 服务端会因为别名不在注册表里直接抛异常退出。
    /// </summary>
    private string BuildModelRegistryArgs(string selectedModel, string? serverScriptPath)
    {
        try
        {
            // 拼装规则（含引号与"所选权重不在目录中"的回退）在 TtsCatalog 里，可单独测试。
            return TtsCatalog.BuildModelRegistryArgs(DiscoverModels(serverScriptPath), selectedModel, LogOccurred);
        }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"[Server] 组装权重注册表失败，退回单模型模式: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 只停止本实例启动且持有句柄的 Python 进程；连接到外部服务不代表拥有该服务。
    /// 不按端口、进程名或脚本文件名搜索/终止其它进程。
    /// </summary>
    public void StopManagedServer()
    {
        StopPlayback();
        Process? proc;
        long generation;
        lock (_serverProcessLock)
        {
            generation = ++_serverGeneration; // 使尚在健康检查/启动准备阶段的操作失效。
            proc = _serverProcess;
            _serverProcess = null;
            _registeredModelAliases = null;
        }
        StopOwnedProcess(proc);
        lock (_serverProcessLock)
        {
            if (_serverGeneration == generation)
                SetState(TtsServerState.Stopped, "TTS 引擎已停止（外部独立服务不会被终止）");
        }
    }

    private static void StopOwnedProcess(Process? proc)
    {
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
            }
        }
        catch { }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }
}
