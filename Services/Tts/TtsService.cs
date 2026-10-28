#pragma warning disable CA1416

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VrcChatboxDemo.Services.Tts;

public sealed class TtsService : IDisposable
{
    private static readonly Lazy<TtsService> _instance = new(() => new TtsService());
    public static TtsService Instance => _instance.Value;

    private readonly HttpClient _httpClient;
    private Process? _serverProcess;
    private readonly object _serverProcessLock = new();
    private long _serverGeneration;
    private long _synthesisSequence;
    private long _healthCheckSequence;
    // ResponseHeadersRead 之后 HttpClient.Timeout 不再覆盖正文；逐次读取单独限制空闲时间。
    private readonly TimeSpan _streamReadTimeout = TimeSpan.FromMinutes(5);
    private int _disposeStarted;
    private CancellationTokenSource? _activePlaybackCts;
    private readonly object _playbackLock = new();
    private readonly SemaphoreSlim _playbackQueueLock = new(1, 1);
    private readonly SemaphoreSlim _serverStartLock = new(1, 1);

    /// <summary>当前服务端注册过的权重别名；null 表示服务端不支持变体切换</summary>
    private volatile List<string>? _registeredModelAliases;

    public TtsServerState State { get; private set; } = TtsServerState.Stopped;
    public string StatusMessage { get; private set; } = "TTS 引擎已停止";

    /// <summary>
    /// 服务端是否已经注册了指定权重变体（只有注册过，请求里带 model 才是安全的）
    /// </summary>
    public bool IsModelRegistered(string? fileName)
    {
        var aliases = _registeredModelAliases;
        if (aliases == null || string.IsNullOrWhiteSpace(fileName)) return false;
        return aliases.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    public event Action<TtsServerState, string>? StateChanged;
    public event Action<string, double, long>? SynthesisCompleted;
    public event Action<string>? LogOccurred;
    public event Action<bool>? PlaybackActiveStateChanged;
    public event Action<int, int>? StreamProgressChanged;

    private TtsService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    private void SetState(TtsServerState state, string message)
    {
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(state, message);
        LogOccurred?.Invoke($"[{state}] {message}");
    }

    /// <summary>
    /// 获取当前系统可用的音频播放设备（包括虚拟麦克风跳线与物理耳机）
    /// </summary>
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

    /// <summary>
    /// 定位音色特征目录：与 checkpoints 同级的 prompts/
    /// </summary>
    public static string? ResolvePromptsDir(string? serverScriptPath = null)
    {
        string? checkpoints = ResolveCheckpointsDir(serverScriptPath);
        if (checkpoints == null) return null;

        string? dir = Path.GetDirectoryName(checkpoints);
        if (string.IsNullOrEmpty(dir)) return null;

        return Path.Combine(dir, "prompts");
    }

    /// <summary>
    /// 扫描 prompts 目录里所有可用的音色特征（cond_mel）。
    ///
    /// 音色与权重是**解耦**的：权重决定咬字/韵律，音色决定声线，
    /// 两者可以自由组合（比如用阿也的 LoRA 配 me 的音色）。
    /// </summary>
    public List<TtsPromptOption> DiscoverPrompts(string? serverScriptPath = null)
    {
        var list = new List<TtsPromptOption>();
        string? dir = ResolvePromptsDir(serverScriptPath);

        if (dir == null || !Directory.Exists(dir))
        {
            LogOccurred?.Invoke($"未找到音色目录: {dir ?? "(路径无效)"}");
            return list;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.pt"))
            {
                string name = Path.GetFileName(path);
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }

                string stem = Path.GetFileNameWithoutExtension(name);
                // X_prompt.pt 是「某个权重 X.pth 的配套音色」，去掉后缀就是它属于谁
                bool isPaired = stem.EndsWith("_prompt", StringComparison.OrdinalIgnoreCase);
                string owner = isPaired ? stem[..^"_prompt".Length] : "";

                string desc = isPaired
                    ? $"{size / 1024.0:F0} KB · 配套权重 {owner}.pth"
                    : $"{size / 1024.0:F0} KB · 独立音色";

                list.Add(new TtsPromptOption(
                    FileName: name,
                    DisplayName: $"{name}    ({desc})",
                    Description: desc,
                    SizeBytes: size));
            }

            // 配套音色在前（按权重名排），独立的排后面
            list.Sort((a, b) =>
            {
                bool pa = a.FileName.EndsWith("_prompt.pt", StringComparison.OrdinalIgnoreCase);
                bool pb = b.FileName.EndsWith("_prompt.pt", StringComparison.OrdinalIgnoreCase);
                if (pa != pb) return pa ? -1 : 1;
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"扫描音色目录异常: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// 判断某个音色文件是不是某个权重的配套音色（X.pth ↔ X_prompt.pt）
    /// </summary>
    public static bool IsPromptPairedWith(string promptFile, string checkpointFile)
    {
        if (string.IsNullOrWhiteSpace(promptFile) || string.IsNullOrWhiteSpace(checkpointFile)) return false;
        string pStem = Path.GetFileNameWithoutExtension(promptFile);
        string cStem = Path.GetFileNameWithoutExtension(checkpointFile);
        return pStem.Equals(cStem + "_prompt", StringComparison.OrdinalIgnoreCase);
    }

    // ======================================================================
    // 推理权重发现
    // ======================================================================

    /// <summary>
    /// 定位 checkpoints 目录：与 tts_server.py 同级（IndexTTS 仓库的权重目录）
    /// </summary>
    public static string? ResolveCheckpointsDir(string? serverScriptPath = null)
    {
        string script = serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath;
        if (string.IsNullOrWhiteSpace(script)) return null;

        string? dir = Path.GetDirectoryName(script);
        if (string.IsNullOrEmpty(dir)) return null;

        return Path.Combine(dir, "checkpoints");
    }

    /// <summary>
    /// 扫描 checkpoints 目录里所有可用的 **GPT 主干权重**。
    ///
    /// 排除项：``bigvgan_*`` / ``dvae*`` 是声码器与 DVAE 权重，不是可切换的推理权重。
    /// 是否 FP8 量化由同名的 ``.json`` 边车文件（``tools/quantize_gpt.py`` 产出）判断。
    /// </summary>
    public List<TtsModelOption> DiscoverModels(string? serverScriptPath = null)
    {
        var list = new List<TtsModelOption>();
        string? dir = ResolveCheckpointsDir(serverScriptPath);

        if (dir == null || !Directory.Exists(dir))
        {
            LogOccurred?.Invoke($"未找到 checkpoints 目录: {dir ?? "(路径无效)"}");
            return list;
        }

        // 音色特征目录：服务端按 "prompts/<权重名>_prompt.pt" 的约定去找配套音色。
        // 没有配套文件的话，切换权重后声线不会变（会回退到默认音色），这里要在 UI 上说清楚。
        string promptDir = Path.Combine(Path.GetDirectoryName(dir) ?? "", "prompts");

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.pth"))
            {
                string name = Path.GetFileName(path);
                string lower = name.ToLowerInvariant();

                // 声码器 / DVAE 权重不是 GPT 推理权重，跳过
                if (lower.StartsWith("bigvgan") || lower.StartsWith("dvae")) continue;

                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }

                bool quantized = IsQuantizedCheckpoint(path);

                // 是否配套了音色特征文件
                string stem = Path.GetFileNameWithoutExtension(name);
                bool hasPrompt = !string.IsNullOrEmpty(stem) &&
                                 File.Exists(Path.Combine(promptDir, stem + "_prompt.pt"));

                // gpt.pth 之类没带 lora 后缀的，视为未微调的原始底模
                bool isBase = !lower.Contains("lora");

                string sizeText = size > 0 ? $"{size / 1024.0 / 1024 / 1024:F2} GB" : "大小未知";
                string tag = quantized
                    ? "FP8 量化 · 省显存"
                    : size >= 1_500_000_000L
                        ? "fp32 原始 · 加载后转 fp16"
                        : "fp16";

                string desc = $"{sizeText} · {tag}";
                if (isBase)
                {
                    desc += isBase && !hasPrompt
                        ? " · 原始底模，未学过任何特定音色"
                        : " · 原始底模";
                }
                else if (!hasPrompt)
                {
                    desc += " · ⚠️ 缺配套音色文件，切换后声线不变";
                }

                list.Add(new TtsModelOption(
                    FileName: name,
                    DisplayName: $"{name}    ({desc})",
                    Description: desc,
                    SizeBytes: size,
                    IsQuantized: quantized,
                    HasMatchingPrompt: hasPrompt,
                    IsBaseModel: isBase));
            }

            // 排序：有配套音色的微调权重优先 > 量化 > 名称；缺音色的和底模垫底
            list.Sort((a, b) =>
            {
                if (a.HasMatchingPrompt != b.HasMatchingPrompt) return a.HasMatchingPrompt ? -1 : 1;
                if (a.IsBaseModel != b.IsBaseModel) return a.IsBaseModel ? 1 : -1;
                if (a.IsQuantized != b.IsQuantized) return a.IsQuantized ? -1 : 1;
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"扫描权重目录异常: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// 判断某个 checkpoint 是否是 FP8 量化版（看同名 .json 边车里的 format 字段）
    /// </summary>
    private static bool IsQuantizedCheckpoint(string checkpointPath)
    {
        try
        {
            string metaPath = Path.ChangeExtension(checkpointPath, ".json");
            if (!File.Exists(metaPath)) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            return doc.RootElement.TryGetProperty("format", out var fmt) &&
                   (fmt.GetString() ?? "").Contains("fp8", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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

    /// <summary>
    /// 检查指定 HTTP 服务端是否已处于健康可用状态
    /// </summary>
    public Task<TtsHealthStatus?> CheckHealthAsync(string? endpoint = null)
    {
        long generation;
        long sequence;
        lock (_serverProcessLock)
        {
            generation = _serverGeneration;
            sequence = ++_healthCheckSequence;
        }
        return CheckHealthCoreAsync(endpoint, generation, sequence);
    }

    private async Task<TtsHealthStatus?> CheckHealthCoreAsync(string? endpoint, long generation, long sequence)
    {
        endpoint ??= SettingsService.Instance.Config.TtsServerEndpoint;
        string healthUrl = endpoint.TrimEnd('/') + "/health";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = await _httpClient.GetAsync(healthUrl, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool ready = root.TryGetProperty("ready", out var r) && r.GetBoolean();
                string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                string model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                bool lowVram = root.TryGetProperty("low_vram", out var lv) && lv.GetBoolean();
                string? err = root.TryGetProperty("error", out var e) ? e.GetString() : null;

                // 服务端通过 --models 注册的权重别名列表。老版本脚本没有这一段，
                // 此时保持 null，调用方就不会给请求带 model 字段。
                List<string>? aliases = null;
                if (root.TryGetProperty("models", out var mreg) && mreg.ValueKind == JsonValueKind.Object &&
                    mreg.TryGetProperty("models", out var mlist) && mlist.ValueKind == JsonValueKind.Array)
                {
                    aliases = new List<string>();
                    foreach (var a in mlist.EnumerateArray())
                    {
                        string? v = a.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) aliases.Add(v);
                    }
                }
                lock (_serverProcessLock)
                {
                    // 停止/重启后、或更新的探测已经开始时，旧 HTTP 响应不再拥有注册表。
                    if (_serverGeneration != generation || _healthCheckSequence != sequence ||
                        Volatile.Read(ref _disposeStarted) != 0) return null;
                    _registeredModelAliases = aliases;
                }

                return new TtsHealthStatus(status, ready, model, lowVram, err, aliases);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 主动探测 TTS 服务健康状态并自动同步全局 State 与提示信息
    /// </summary>
    public async Task<TtsHealthStatus?> RefreshHealthAsync(string? endpoint = null)
    {
        long generation;
        long synthesisSequence;
        long healthSequence;
        lock (_serverProcessLock)
        {
            generation = _serverGeneration;
            synthesisSequence = _synthesisSequence;
            healthSequence = ++_healthCheckSequence;
        }

        var health = await CheckHealthCoreAsync(endpoint, generation, healthSequence);
        lock (_serverProcessLock)
        {
            // 健康查询只是旁路探测，不能覆盖停止/新合成或更新刷新已经确立的状态。
            if (_serverGeneration != generation || _synthesisSequence != synthesisSequence ||
                _healthCheckSequence != healthSequence || Volatile.Read(ref _disposeStarted) != 0)
                return health;
            if (State == TtsServerState.Starting || State == TtsServerState.Synthesizing)
                return health;

            if (health != null && health.Ready)
            {
                string modeDesc = health.LowVram ? "低显存模式" : "流式全显存模式";
                SetState(TtsServerState.Ready, $"服务就绪 (模型: {health.Model}, {modeDesc})");
            }
            else if (health != null)
            {
                SetState(TtsServerState.Error, $"服务启动失败: {health.Error ?? "未知错误"}");
            }
            else
            {
                SetState(TtsServerState.Stopped, "TTS 引擎已停止 (点击右侧启动引擎)");
            }
        }
        return health;
    }

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
            var models = DiscoverModels(serverScriptPath);
            if (models.Count == 0) return string.Empty;

            string registry = string.Join(",", models.Select(m => $"{m.FileName}={m.FileName}"));
            string args = $" --models \"{registry}\"";

            bool selectedExists = models.Any(
                m => string.Equals(m.FileName, selectedModel, StringComparison.OrdinalIgnoreCase));
            if (selectedExists)
            {
                args += $" --default-model \"{selectedModel}\"";
            }
            else
            {
                LogOccurred?.Invoke(
                    $"[Server] 配置里的权重 {selectedModel} 不在 checkpoints 目录中，" +
                    $"本次以第一个可用权重作为默认值。");
            }
            return args;
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

    /// <summary>
    /// 本次请求要用的权重别名。
    ///
    /// **只有服务端明确注册过该别名才返回它**，否则返回 null（让服务端用它自己的默认权重）：
    /// 手工启动的旧版服务端拿到未注册的别名会直接报错，宁可少带一个字段也不能让合成失败。
    /// </summary>
    private string? ResolveRequestModelAlias()
    {
        string selected = SettingsService.Instance.Config.TtsModelName;
        return IsModelRegistered(selected) ? selected : null;
    }

    /// <summary>
    /// 本次请求指定的音色特征。
    ///
    /// 只发**文件名**（不含路径），服务端在它自己的 prompts/ 目录内解析，
    /// 这样客户端不需要知道服务端装在哪，也不存在路径穿越问题。
    /// </summary>
    private static string? ResolveRequestPrompt()
    {
        string prompt = SettingsService.Instance.Config.TtsPromptFile;
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        return Path.GetFileName(prompt);
    }

    private (long Generation, long Sequence) BeginSynthesis(string message, CancellationToken ct)
    {
        lock (_serverProcessLock)
        {
            // 停止时已取消、但尚未运行到此处的播放任务，也不能重新开启一次合成状态。
            if (ct.IsCancellationRequested || Volatile.Read(ref _disposeStarted) != 0)
                return (-1, -1);
            var operation = (_serverGeneration, ++_synthesisSequence);
            SetState(TtsServerState.Synthesizing, message);
            return operation;
        }
    }

    private bool IsCurrentSynthesis((long Generation, long Sequence) operation)
    {
        lock (_serverProcessLock)
        {
            return operation.Generation == _serverGeneration && operation.Sequence == _synthesisSequence &&
                   Volatile.Read(ref _disposeStarted) == 0;
        }
    }

    private bool TrySetSynthesisState((long Generation, long Sequence) operation,
        TtsServerState state, string message, TtsServerState? expectedState = null)
    {
        lock (_serverProcessLock)
        {
            // 检查与写入必须和 Stop 的 generation 更新共用锁，避免检查通过后才被停止。
            if (!IsCurrentSynthesis(operation) || (expectedState.HasValue && State != expectedState.Value))
                return false;
            SetState(state, message);
            return true;
        }
    }

    private async Task<T> ReadStreamBodyAsync<T>(Func<CancellationToken, Task<T>> read,
        (long Generation, long Sequence) operation, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(_streamReadTimeout);
        try
        {
            // WaitAsync 同时约束不响应取消的内容流，超时后由枚举器释放 HTTP 响应和正文。
            return await read(readCts.Token).WaitAsync(readCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            const string message = "流式音频读取超时";
            TrySetSynthesisState(operation, TtsServerState.Error, message);
            // 不能把超时当作 EOF：已播放过部分音频时 PlayStreamAsync 会误报成功。
            throw new TimeoutException(message);
        }
    }

    /// <summary>
    /// 请求 IndexTTS 服务合成语音（免参考音频，纯模型推理）
    /// </summary>
    public async Task<TtsSynthesisResult> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        string rawText = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new TtsSynthesisResult(false, null, 0, 0, 24000, "输入文本为空");
        }

        var cfg = SettingsService.Instance.Config;
        string ttsUrl = cfg.TtsServerEndpoint.TrimEnd('/') + "/tts";

        var operation = BeginSynthesis($"正在合成语音: \"{rawText}\"...", ct);
        if (!IsCurrentSynthesis(operation))
            return new TtsSynthesisResult(false, null, 0, 0, 24000, "合成请求已失效");

        var sw = Stopwatch.StartNew();
        try
        {
            var reqObj = new
            {
                text = rawText,
                audio_prompt = ResolveRequestPrompt(), // 指定音色特征（prompts/ 下的文件名）
                model = ResolveRequestModelAlias()
            };
            string reqJson = JsonSerializer.Serialize(reqObj);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(ttsUrl, content, ct);
            using (resp)
            {
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                {
                    string errDetail = await resp.Content.ReadAsStringAsync(ct);
                    TrySetSynthesisState(operation, TtsServerState.Error, $"TTS 合成失败: HTTP {(int)resp.StatusCode}");
                    return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, errDetail);
                }

                byte[] wavBytes = await resp.Content.ReadAsByteArrayAsync(ct);

                double duration = 0;
                if (resp.Headers.TryGetValues("X-Audio-Duration", out var durValues))
                {
                    double.TryParse(durValues.FirstOrDefault(), out duration);
                }

                int sr = 24000;
                if (resp.Headers.TryGetValues("X-Sample-Rate", out var srValues) &&
                    int.TryParse(srValues.FirstOrDefault(), out int parsedSampleRate) &&
                    parsedSampleRate > 0)
                {
                    // TryParse 失败会把 out 参数写成 0；必须保留默认 24000，不能直接用失败结果覆盖。
                    sr = parsedSampleRate;
                }

                if (!TrySetSynthesisState(operation, TtsServerState.Ready, $"合成成功 (耗时: {sw.ElapsedMilliseconds}ms, 时长: {duration:F2}s)"))
                    return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, sr, "合成请求已失效");
                SynthesisCompleted?.Invoke(rawText, duration, sw.ElapsedMilliseconds);

                return new TtsSynthesisResult(true, wavBytes, duration, sw.ElapsedMilliseconds, sr, null);
            }
        }
        catch (OperationCanceledException)
        {
            TrySetSynthesisState(operation, TtsServerState.Ready, "合成已取消");
            return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, "用户已取消");
        }
        catch (Exception ex)
        {
            TrySetSynthesisState(operation, TtsServerState.Error, $"合成异常: {ex.Message}");
            return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, ex.Message);
        }
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

    /// <summary>
    /// 请求 IndexTTS 服务进行真正低延迟流式语音合成（POST /tts/stream?format=pcm）
    /// 逐块产出 24kHz 16-bit 单声道 PCM 音频流，首包延迟 ~550ms，全程内存无磁盘 IO
    /// </summary>
    public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string rawText = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            yield break;
        }

        var cfg = SettingsService.Instance.Config;
        string streamUrl = $"{cfg.TtsServerEndpoint.TrimEnd('/')}/tts/stream?format=pcm&first_chunk_frames=16&chunk_frames=16";

        var operation = BeginSynthesis($"正在流式合成语音: \"{rawText}\"...", ct);
        if (!IsCurrentSynthesis(operation)) yield break;

        var reqObj = new
        {
            text = rawText,
            audio_prompt = ResolveRequestPrompt(), // 指定音色特征（prompts/ 下的文件名）
            do_sample = false,           // 贪心解码：2.5倍提速，消除首字出声漫长等待
            model = ResolveRequestModelAlias()
        };
        string reqJson = JsonSerializer.Serialize(reqObj);
        using var request = new HttpRequestMessage(HttpMethod.Post, streamUrl)
        {
            Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp;
        var sw = Stopwatch.StartNew();
        try
        {
            resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException)
        {
            TrySetSynthesisState(operation, TtsServerState.Ready, "流式合成已取消");
            yield break;
        }
        catch (Exception ex)
        {
            TrySetSynthesisState(operation, TtsServerState.Error, $"流式连接异常: {ex.Message}");
            LogOccurred?.Invoke($"[TTS Stream] 请求连接异常: {ex.Message}");
            yield break;
        }

        bool finished = false;
        using (resp)
        {
          try
          {
            if (!resp.IsSuccessStatusCode)
            {
                string errDetail = await ReadStreamBodyAsync(token => resp.Content.ReadAsStringAsync(token), operation, ct);
                TrySetSynthesisState(operation, TtsServerState.Error, $"流式合成失败: HTTP {(int)resp.StatusCode}");
                LogOccurred?.Invoke($"[TTS Stream] 合成失败 (HTTP {(int)resp.StatusCode}): {errDetail}");
                yield break;
            }

            using var stream = await ReadStreamBodyAsync(token => resp.Content.ReadAsStreamAsync(token), operation, ct);
            byte[] readBuffer = new byte[32768]; // 32KB 读取缓冲，减少网络分包与读取碎片
            byte[]? leftover = null; // 保证按 2 字节（int16 sample）对齐
            long totalBytes = 0;
            int chunkCount = 0;
            bool firstChunk = true;
            bool canceled = false;

            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await ReadStreamBodyAsync(
                        token => stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), token).AsTask(), operation, ct);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }

                if (!IsCurrentSynthesis(operation)) yield break;
                if (bytesRead <= 0) break;

                chunkCount++;
                if (firstChunk)
                {
                    firstChunk = false;
                    LogOccurred?.Invoke($"[TTS Stream] 收到首块音频流 (首包耗时: {sw.ElapsedMilliseconds}ms, 大小: {bytesRead}B)");
                }

                // 处理 16-bit PCM 字节对齐（2 字节/样本）
                byte[] toProcess;
                if (leftover != null && leftover.Length > 0)
                {
                    toProcess = new byte[leftover.Length + bytesRead];
                    Buffer.BlockCopy(leftover, 0, toProcess, 0, leftover.Length);
                    Buffer.BlockCopy(readBuffer, 0, toProcess, leftover.Length, bytesRead);
                    leftover = null;
                }
                else
                {
                    toProcess = new byte[bytesRead];
                    Buffer.BlockCopy(readBuffer, 0, toProcess, 0, bytesRead);
                }

                int usableBytes = toProcess.Length - (toProcess.Length % 2);
                if (usableBytes < toProcess.Length)
                {
                    leftover = new byte[toProcess.Length - usableBytes];
                    Buffer.BlockCopy(toProcess, usableBytes, leftover, 0, leftover.Length);
                }

                if (usableBytes > 0)
                {
                    totalBytes += usableBytes;
                    byte[] chunk = (usableBytes == toProcess.Length) ? toProcess : toProcess[..usableBytes];
                    yield return chunk;
                }
            }

            sw.Stop();

            if (canceled || ct.IsCancellationRequested)
            {
                TrySetSynthesisState(operation, TtsServerState.Ready, "流式合成已取消");
                yield break;
            }

            double duration = (double)totalBytes / (24000 * 2);
            if (!TrySetSynthesisState(operation, TtsServerState.Ready, $"流式合成完成 (耗时: {sw.ElapsedMilliseconds}ms, 音频时长: {duration:F2}s, 块数: {chunkCount})"))
                yield break;
            LogOccurred?.Invoke($"[TTS Stream] 流式传输结束: 共 {chunkCount} 块, 总字节: {totalBytes} ({duration:F2}s), 耗时: {sw.ElapsedMilliseconds}ms");
            SynthesisCompleted?.Invoke(rawText, duration, sw.ElapsedMilliseconds);
            finished = true;
          }
          finally
          {
            // 调用方提前放弃枚举（例如 PlayStreamAsync 被取消后不再 MoveNext）时，
            // 枚举器的 finally 仍会执行；这里把状态从 Synthesizing 复位，
            // 避免界面永久停在“正在合成语音”。
            if (!finished)
            {
                TrySetSynthesisState(operation, TtsServerState.Ready, "流式合成已结束", TtsServerState.Synthesizing);
            }
          }
        }
    }

    /// <summary>
    /// 流式推流管线：直接发起 IndexTTS /tts/stream 流式请求，
    /// 收到首个 PCM 音频块立刻唤醒声卡开始推流，后续音频流在后台边生成边播放，
    /// 实现毫秒级（约 500ms）首字出声，全程在内存中完成，不落磁盘。
    /// </summary>
    public async Task<bool> PlayStreamAsync(
        string text,
        string? virtualMicDeviceId = null,
        string? monitorDeviceId = null,
        bool enableMonitor = true,
        float micVolume = 1.0f,
        float monitorVolume = 0.8f,
        Action<int>? onChunkReceived = null,
        CancellationToken ct = default)
    {
        string rawText = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawText)) return false;

        // 等待队列前就登记本请求，停止和后来的请求也能取消尚未开播的任务。
        using var playbackCts = BeginPlayback(ct);
        var token = playbackCts.Token;
        bool acquired = false;
        try
        {
            await _playbackQueueLock.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            PlaybackActiveStateChanged?.Invoke(true);
            return await Task.Run(async () =>
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? micDevice = null;
                MMDevice? monDevice = null;
                var resolved = ResolveDevices(enumerator, virtualMicDeviceId, monitorDeviceId, enableMonitor);
                micDevice = resolved.micDevice;
                monDevice = resolved.monDevice;
                bool isSameDevice = resolved.isSameDevice;

                var inputFormat = new WaveFormat(24000, 16, 1);
                SpeechQueueWaveProvider? bufferSame = null;
                SpeechQueueWaveProvider? bufferMic = null;
                SpeechQueueWaveProvider? bufferMon = null;

                WasapiOut? playerSame = null;
                WasapiOut? playerMic = null;
                WasapiOut? playerMon = null;

                try
                {
                    if (isSameDevice)
                    {
                        float maxVol = Math.Max(micVolume, monitorVolume);
                        if (maxVol > 0.001f && micDevice != null)
                        {
                            bufferSame = new SpeechQueueWaveProvider(inputFormat);
                            using var audioClient = micDevice.CreateAudioClient();
                            var mixFormat = audioClient.MixFormat;
                            ISampleProvider sampleProvider = bufferSame.ToSampleProvider();
                            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);
                            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                            {
                                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
                            }
                            var volProvider = new SoftLimiterSampleProvider(sampleProvider, maxVol);
                            playerSame = new WasapiOut(micDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
                            playerSame.Init(volProvider);
                        }
                    }
                    else
                    {
                        if (micDevice != null && micVolume > 0.001f)
                        {
                            bufferMic = new SpeechQueueWaveProvider(inputFormat);
                            using var audioClient = micDevice.CreateAudioClient();
                            var mixFormat = audioClient.MixFormat;
                            ISampleProvider sampleProvider = bufferMic.ToSampleProvider();
                            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);
                            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                            {
                                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
                            }
                            var volProvider = new SoftLimiterSampleProvider(sampleProvider, micVolume);
                            playerMic = new WasapiOut(micDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
                            playerMic.Init(volProvider);
                        }

                        if (enableMonitor && monDevice != null && monitorVolume > 0.001f)
                        {
                            bufferMon = new SpeechQueueWaveProvider(inputFormat);
                            using var audioClient = monDevice.CreateAudioClient();
                            var mixFormat = audioClient.MixFormat;
                            ISampleProvider sampleProvider = bufferMon.ToSampleProvider();
                            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);
                            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                            {
                                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
                            }
                            var volProvider = new SoftLimiterSampleProvider(sampleProvider, monitorVolume);
                            playerMon = new WasapiOut(monDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
                            playerMon.Init(volProvider);
                        }
                    }

                    // 没有任何可用输出端点时，音频块会被静默丢弃，但旧逻辑仍会
                    // 根据 totalBufferedBytes 把 startedPlayback 置 true，向 UI 谎报“推流完成”。
                    bool hasOutputEndpoint = playerSame != null || playerMic != null || playerMon != null;
                    if (!hasOutputEndpoint)
                    {
                        LogOccurred?.Invoke("未找到可用输出设备，或推流/耳返音量均为 0，已取消本次推流。");
                        return false;
                    }

                    bool startedPlayback = false;
                    bool playbackInterrupted = false;
                    int chunkCount = 0;
                    long totalBufferedBytes = 0;
                    // 高响应防欠载预缓冲阈值：
                    // 服务端已配置首块 16 帧 (32,768 字节，约 683ms 音频) + 贪心解码。
                    // 后续块为 16 帧 (约 683ms 音频)，在 GPU 上约需 ~520ms 生成完毕。
                    // 首块的 683ms 音频能够充裕覆盖次块的 520ms 计算耗时（保留 +160ms 安全余量）。
                    // 将预防水位设为 20,000 字节（约 0.42 秒音频）：首块一到达立即唤醒声卡推流，
                    // 实现极速首字出声的同时彻底杜绝开头播完首包后的卡顿/断音。
                    const int PRE_BUFFER_BYTES = 20000;

                    await foreach (var chunk in SynthesizeStreamAsync(rawText, token))
                    {
                        if (token.IsCancellationRequested) break;
                        chunkCount++;
                        totalBufferedBytes += chunk.Length;

                        // 送入音频缓冲区
                        if (isSameDevice && bufferSame != null)
                        {
                            bufferSame.AddSamples(chunk, 0, chunk.Length);
                        }
                        else
                        {
                            if (bufferMic != null) bufferMic.AddSamples(chunk, 0, chunk.Length);
                            if (bufferMon != null) bufferMon.AddSamples(chunk, 0, chunk.Length);
                        }

                        // 收到首块（达到预防水位）立即唤醒声卡推流，实现 ~450ms 极速首字出声
                        if (!startedPlayback && totalBufferedBytes >= PRE_BUFFER_BYTES)
                        {
                            startedPlayback = true;
                            if (isSameDevice)
                            {
                                playerSame?.Play();
                            }
                            else
                            {
                                playerMic?.Play();
                                playerMon?.Play();
                            }
                        }

                        onChunkReceived?.Invoke(chunkCount);
                        StreamProgressChanged?.Invoke(chunkCount, 0);
                    }

                    // 短文本兜底：若整段文本总音频较短（如短语/单字，全句总长不足 0.4 秒），在流传输结束 (EOF) 时立即唤醒声卡播完
                    if (!startedPlayback && totalBufferedBytes > 0 && !token.IsCancellationRequested)
                    {
                        startedPlayback = true;
                        if (isSameDevice)
                        {
                            playerSame?.Play();
                        }
                        else
                        {
                            playerMic?.Play();
                            playerMon?.Play();
                        }
                    }

                    // 全部流式音频接收完毕，等待缓冲区全部音频彻底播放完毕
                    if (startedPlayback)
                    {
                        // 声卡被独占/设备被拔出时 WasapiOut 会自行停止，缓冲区里的 PCM 再也不会被排空。
                        // 旧实现只等 BufferedBytes 归零：单设备还好，双设备（推流+耳返）里只要有一个
                        // 播放器还活着就永远不会判定失败，于是死循环并一直占着播放队列。
                        while (!token.IsCancellationRequested)
                        {
                            bool micPending = bufferMic != null && bufferMic.BufferedBytes > 0;
                            bool monPending = bufferMon != null && bufferMon.BufferedBytes > 0;
                            bool samePending = bufferSame != null && bufferSame.BufferedBytes > 0;

                            if (isSameDevice)
                            {
                                if (!samePending) break;
                                if (playerSame?.PlaybackState != PlaybackState.Playing)
                                {
                                    playbackInterrupted = true;
                                    break;
                                }
                            }
                            else
                            {
                                if (!micPending && !monPending) break;

                                bool micStalled = micPending && playerMic?.PlaybackState != PlaybackState.Playing;
                                bool monStalled = monPending && playerMon?.PlaybackState != PlaybackState.Playing;
                                if (micStalled || monStalled)
                                {
                                    playbackInterrupted = true;
                                    break;
                                }
                            }

                            await Task.Delay(30, token);
                        }

                        if (playbackInterrupted)
                        {
                            LogOccurred?.Invoke("输出设备已停止播放（被独占或已拔出），已中止本次推流。");
                        }

                        await Task.Delay(150, token); // 硬件延迟余音淡出
                    }

                    // 用户点了“停止播放”后，合成流会被取消，但已播放的块数可能 >0。
                    // 不能把这种情况当成成功返回，否则 UI 会误报“推流完成”。
                    if (token.IsCancellationRequested) return false;
                    if (playbackInterrupted) return false;

                    return startedPlayback;
                }
                finally
                {
                    try { playerSame?.Stop(); playerSame?.Dispose(); bufferSame?.ClearBuffer(); } catch { }
                    try { playerMic?.Stop(); playerMic?.Dispose(); bufferMic?.ClearBuffer(); } catch { }
                    try { playerMon?.Stop(); playerMon?.Dispose(); bufferMon?.ClearBuffer(); } catch { }
                    DisposeDevices(micDevice, monDevice);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"流式推流异常: {ex.Message}");
            return false;
        }
        finally
        {
            EndPlayback(playbackCts);
            if (acquired)
            {
                try { PlaybackActiveStateChanged?.Invoke(false); }
                finally { _playbackQueueLock.Release(); }
            }
        }
    }

    /// <summary>
    /// 兼容重载：支持接收分句列表，拼接后以单一流式请求完成高效推理
    /// </summary>
    public Task<bool> PlayStreamAsync(
        IReadOnlyList<string> clauses,
        string? virtualMicDeviceId = null,
        string? monitorDeviceId = null,
        bool enableMonitor = true,
        float micVolume = 1.0f,
        float monitorVolume = 0.8f,
        Action<int, int>? onClauseSynthesized = null,
        Action<int, int>? onClausePlaying = null,
        CancellationToken ct = default)
    {
        if (clauses == null || clauses.Count == 0) return Task.FromResult(false);
        string combined = string.Join(" ", clauses);
        return PlayStreamAsync(
            combined,
            virtualMicDeviceId,
            monitorDeviceId,
            enableMonitor,
            micVolume,
            monitorVolume,
            onChunkReceived: onClauseSynthesized != null ? (c) => onClauseSynthesized(c, 0) : null,
            ct);
    }

    /// <summary>
    /// 播放音频字节流到指定的虚拟麦克风设备与耳返监听设备
    /// </summary>
    public async Task PlayAudioAsync(
        byte[] wavBytes,
        string? virtualMicDeviceId = null,
        string? monitorDeviceId = null,
        bool enableMonitor = true,
        float micVolume = 1.0f,
        float monitorVolume = 0.8f,
        CancellationToken ct = default)
    {
        if (wavBytes == null || wavBytes.Length == 0) return;

        using var playbackCts = BeginPlayback(ct);
        var token = playbackCts.Token;

        try
        {
        await Task.Run(async () =>
        {
            MMDevice? micDevice = null;
            MMDevice? monDevice = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var resolved = ResolveDevices(enumerator, virtualMicDeviceId, monitorDeviceId, enableMonitor);
                micDevice = resolved.micDevice;
                monDevice = resolved.monDevice;
                bool isSameDevice = resolved.isSameDevice;

                var tasks = new List<Task>();

                // 【核心防电音防干涉保护】：已由 ResolveDevices 统一计算 isSameDevice
                if (isSameDevice)
                {
                    float maxVol = Math.Max(micVolume, monitorVolume);
                    if (maxVol > 0.001f && micDevice != null)
                    {
                        tasks.Add(PlayToSingleEndpointAsync(wavBytes, micDevice, maxVol, token));
                    }
                }
                else
                {
                    if (micDevice != null && micVolume > 0.001f)
                    {
                        tasks.Add(PlayToSingleEndpointAsync(wavBytes, micDevice, micVolume, token));
                    }

                    if (enableMonitor && monDevice != null && monitorVolume > 0.001f)
                    {
                        tasks.Add(PlayToSingleEndpointAsync(wavBytes, monDevice, monitorVolume, token));
                    }
                }

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogOccurred?.Invoke($"音频推流播放异常: {ex.Message}");
            }
            finally
            {
                DisposeDevices(micDevice, monDevice);
            }
        }, token);
        }
        catch (OperationCanceledException) { }
        finally { EndPlayback(playbackCts); }
    }

    /// <summary>
    /// 把单声道 TTS 音频对齐到目标设备的原生声道数。
    /// 只处理 2 声道时，5.1/7.1 设备（HDMI、虚拟 7.1 耳机）会保留单声道格式，
    /// 共享模式 WasapiOut 初始化失败，整段 TTS 静音；这里用 MultiplexingSampleProvider
    /// 把单声道复制到前 L/R，其余声道静音。
    /// </summary>
    private static ISampleProvider MatchDeviceChannelCount(ISampleProvider source, WaveFormat mixFormat)
    {
        if (mixFormat.Channels <= 0 || source.WaveFormat.Channels == mixFormat.Channels)
        {
            return source;
        }

        if (source.WaveFormat.Channels == 1 && mixFormat.Channels == 2)
        {
            return new MonoToStereoSampleProvider(source);
        }

        if (source.WaveFormat.Channels == 1 && mixFormat.Channels > 2)
        {
            var multiplex = new MultiplexingSampleProvider(new[] { source }, mixFormat.Channels);
            multiplex.ConnectInputToOutput(0, 0);
            multiplex.ConnectInputToOutput(0, 1);
            return multiplex;
        }

        return source;
    }

    private async Task PlayToSingleEndpointAsync(
        byte[] wavBytes,
        MMDevice targetDevice,
        float volume,
        CancellationToken token)
    {
        try
        {
            if (targetDevice == null) return;

            using var ms = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(ms);

            // 获取目标声卡的原生混音格式（通常为 48000Hz 2声道 IEEE Float）
            using var audioClient = targetDevice.CreateAudioClient();
            var mixFormat = audioClient.MixFormat;
            ISampleProvider sampleProvider = reader.ToSampleProvider();

            // 1. 声道对齐：若目标为双声道（立体声）而源音频为单声道，平滑复制到左右声道，防止驱动单声道升采样抖动
            sampleProvider = MatchDeviceChannelCount(sampleProvider, mixFormat);

            // 2. 采样率平滑重采样：若目标采样率与 24000Hz 不一致（如 48000Hz 或 44100Hz）
            // 使用高质量多相带通重采样器（WdlResamplingSampleProvider），杜绝驱动粗暴线性插值产生的高频数码电音
            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
            {
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
            }

            // 3. 防爆音软削顶音量调节（Soft Limiter），防止音量过大产生硬截断方波杂音
            var volumeProvider = new SoftLimiterSampleProvider(sampleProvider, volume);

            using var wasapiOut = new WasapiOut(targetDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
            wasapiOut.Init(volumeProvider);
            wasapiOut.Play();

            while (wasapiOut.PlaybackState == PlaybackState.Playing && !token.IsCancellationRequested)
            {
                await Task.Delay(30, token);
            }

            wasapiOut.Stop();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogOccurred?.Invoke($"播放端点输出异常 ({targetDevice.FriendlyName}): {ex.Message}");
        }
    }

    private CancellationTokenSource BeginPlayback(CancellationToken ct)
    {
        lock (_playbackLock)
        {
            _activePlaybackCts?.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (Volatile.Read(ref _disposeStarted) != 0) request.Cancel();
            _activePlaybackCts = request;
            return request;
        }
    }

    private void EndPlayback(CancellationTokenSource request)
    {
        lock (_playbackLock)
        {
            if (ReferenceEquals(_activePlaybackCts, request)) _activePlaybackCts = null;
        }
        // CTS 由发起请求的 using 释放，不能在其仍等待/使用 Token 时由 Stop 释放。
    }

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            _activePlaybackCts?.Cancel();
            _activePlaybackCts = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        StopManagedServer();
        _httpClient.Dispose();
    }
}

/// <summary>
/// 带有软削顶（Soft Limiter / Tanh 限幅）的平滑音量控制，杜绝音量过大时产生硬方波杂音与刺耳电音
/// </summary>
internal sealed class SoftLimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _volume;

    public SoftLimiterSampleProvider(ISampleProvider source, float volume)
    {
        _source = source;
        _volume = Math.Clamp(volume, 0.0f, 2.0f);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (Math.Abs(_volume - 1.0f) < 0.001f)
        {
            // 音量为 100% 时，做轻微柔和防削顶保底
            for (int i = 0; i < samplesRead; i++)
            {
                float s = buffer[i];
                if (s > 0.98f) buffer[i] = 0.98f;
                else if (s < -0.98f) buffer[i] = -0.98f;
            }
            return samplesRead;
        }

        for (int i = 0; i < samplesRead; i++)
        {
            float s = buffer[i] * _volume;
            // 超过 0.95 时平滑应用 Tanh 软限幅，避免硬截断产生电音毛刺
            if (s > 0.95f)
            {
                s = 0.95f + 0.049f * (float)Math.Tanh((s - 0.95f) / 0.05f);
            }
            else if (s < -0.95f)
            {
                s = -0.95f - 0.049f * (float)Math.Tanh((-s - 0.95f) / 0.05f);
            }
            buffer[i] = s;
        }

        return samplesRead;
    }
}

/// <summary>
/// 高性能无界流式音频队列 Provider，彻底解除 NAudio 5 秒缓冲限制，支持任意超长子句与多句连续无缝播放
/// </summary>
internal sealed class SpeechQueueWaveProvider : IWaveProvider
{
    private readonly WaveFormat _waveFormat;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private byte[]? _currentChunk;
    private int _currentOffset;
    private readonly object _lock = new();

    public SpeechQueueWaveProvider(WaveFormat waveFormat)
    {
        _waveFormat = waveFormat;
    }

    public WaveFormat WaveFormat => _waveFormat;

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        byte[] copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        _queue.Enqueue(copy);
    }

    public int BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                int total = (_currentChunk != null) ? (_currentChunk.Length - _currentOffset) : 0;
                foreach (var chunk in _queue)
                {
                    total += chunk.Length;
                }
                return total;
            }
        }
    }

    public void ClearBuffer()
    {
        lock (_lock)
        {
            _currentChunk = null;
            _currentOffset = 0;
            while (_queue.TryDequeue(out _)) { }
        }
    }

    public int Read(Span<byte> buffer)
    {
        int bytesRead = 0;
        int count = buffer.Length;
        lock (_lock)
        {
            while (bytesRead < count)
            {
                if (_currentChunk == null || _currentOffset >= _currentChunk.Length)
                {
                    if (!_queue.TryDequeue(out _currentChunk))
                    {
                        _currentChunk = null;
                        _currentOffset = 0;
                        break;
                    }
                    _currentOffset = 0;
                }

                int toCopy = Math.Min(count - bytesRead, _currentChunk.Length - _currentOffset);
                _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Slice(bytesRead, toCopy));
                _currentOffset += toCopy;
                bytesRead += toCopy;
            }
        }

        // 保持输出填满（静音补齐），防止 WASAPI 出现硬件欠载（Underrun）爆音或断流
        if (bytesRead < count)
        {
            buffer.Slice(bytesRead).Clear();
            bytesRead = count;
        }

        return bytesRead;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }
}


