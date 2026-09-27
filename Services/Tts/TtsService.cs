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
    private CancellationTokenSource? _activePlaybackCts;
    private readonly object _playbackLock = new();
    private readonly SemaphoreSlim _playbackQueueLock = new(1, 1);

    public TtsServerState State { get; private set; } = TtsServerState.Stopped;
    public string StatusMessage { get; private set; } = "TTS 引擎已停止";

    public event Action<TtsServerState, string>? StateChanged;
    public event Action<string, double, long>? SynthesisCompleted;
    public event Action<string>? LogOccurred;
    public event Action<bool>? PlaybackActiveStateChanged;
    public event Action<int, int>? StreamProgressChanged;

    private TtsService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
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
                var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultRenderId = def.ID;
            }
            catch { }

            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
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
        return lower.Contains("cable") ||
               lower.Contains("virtual") ||
               lower.Contains("voicemeeter") ||
               lower.Contains("vb-audio") ||
               lower.Contains("line ");
    }

    /// <summary>
    /// 检查指定 HTTP 服务端是否已处于健康可用状态
    /// </summary>
    public async Task<TtsHealthStatus?> CheckHealthAsync(string? endpoint = null)
    {
        endpoint ??= SettingsService.Instance.Config.TtsServerEndpoint;
        string healthUrl = endpoint.TrimEnd('/') + "/health";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = await _httpClient.GetAsync(healthUrl, cts.Token);
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

                return new TtsHealthStatus(status, ready, model, lowVram, err);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 托管启动后台 IndexTTS 1.5 Python 服务
    /// </summary>
    public async Task<bool> StartManagedServerAsync()
    {
        var cfg = SettingsService.Instance.Config;

        // 1. 先探测是否已经有运行中的服务（例如用户此前已启动或独立启动）
        var health = await CheckHealthAsync(cfg.TtsServerEndpoint);
        if (health != null && health.Ready)
        {
            SetState(TtsServerState.Ready, $"已成功连接到运行中的 IndexTTS 服务 (模型: {health.Model})");
            return true;
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

        // 3. 启动后台静默进程
        StopManagedServer();

        SetState(TtsServerState.Starting, "正在启动 IndexTTS 后台微服务并载入专属微调模型 (耗时约 5~8 秒)...");

        try
        {
            string workDir = Path.GetDirectoryName(cfg.TtsServerScriptPath) ?? "";
            var psi = new ProcessStartInfo
            {
                FileName = cfg.TtsPythonExePath,
                Arguments = $"\"{cfg.TtsServerScriptPath}\" --port {cfg.TtsServerPort} --model \"{cfg.TtsModelName}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

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

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _serverProcess = proc;

            // 4. 异步轮询等待就绪状态 (最多等待 35 秒)
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 35000)
            {
                await Task.Delay(1000);

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
                    SetState(TtsServerState.Error, $"TTS 引擎进程意外退出，退出码: {exitCode}");
                    return false;
                }

                health = await CheckHealthAsync(cfg.TtsServerEndpoint);
                if (health != null && health.Ready)
                {
                    SetState(TtsServerState.Ready, $"IndexTTS 引擎加载就绪！(专属微调模型: {health.Model}, 显存 <1.3GB)");
                    return true;
                }
            }

            SetState(TtsServerState.Error, "启动超时：35 秒内未能完成权重加载");
            return false;
        }
        catch (Exception ex)
        {
            SetState(TtsServerState.Error, $"启动异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 停止托管的 Python 后台进程，并清理占用 TTS 端口的残留进程
    /// </summary>
    public void StopManagedServer()
    {
        // 1. 先杀托管进程（如果有）
        var proc = _serverProcess;
        _serverProcess = null;

        if (proc != null)
        {
            try
            {
                bool hasExited = false;
                try { hasExited = proc.HasExited; } catch { }
                if (!hasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    try { proc.WaitForExit(2000); } catch { }
                }
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        // 2. 兜底：通过端口号查找并杀死占用 TTS 服务端口的所有残留进程
        // 解决以下场景：StartManagedServerAsync 探测到已有服务直接复用但未保存 _serverProcess
        try
        {
            int port = SettingsService.Instance.Config.TtsServerPort;
            KillProcessesByPort(port);
        }
        catch { }

        SetState(TtsServerState.Stopped, "TTS 引擎已停止");
    }

    /// <summary>
    /// 通过 netstat 查找占用指定端口的进程并终止
    /// </summary>
    private static void KillProcessesByPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c netstat -ano | findstr :{port} | findstr LISTENING",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var netstat = Process.Start(psi);
            if (netstat == null) return;

            string output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(3000);

            // 从 netstat 输出中提取 PID（每行最后一个数字字段）
            var pids = new HashSet<int>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                // netstat -ano 格式: TCP 127.0.0.1:9880 0.0.0.0:0 LISTENING 12345
                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[^1], out int pid) && pid > 0)
                {
                    pids.Add(pid);
                }
            }

            int currentPid = Environment.ProcessId;
            foreach (int pid in pids)
            {
                if (pid == currentPid) continue; // 绝不杀自身
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(2000);
                    }
                }
                catch { }
            }
        }
        catch { }
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

        var prevState = State;
        SetState(TtsServerState.Synthesizing, $"正在合成语音: \"{rawText}\"...");

        var sw = Stopwatch.StartNew();
        try
        {
            var reqObj = new
            {
                text = rawText,
                audio_prompt = (string?)null // 强制传 null，指示服务端使用模型内置专属微调音色纯推理，不读任何参考音频
            };
            string reqJson = JsonSerializer.Serialize(reqObj);
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(ttsUrl, content, ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                string errDetail = await resp.Content.ReadAsStringAsync(ct);
                SetState(prevState, $"TTS 合成失败: HTTP {(int)resp.StatusCode}");
                return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, errDetail);
            }

            byte[] wavBytes = await resp.Content.ReadAsByteArrayAsync(ct);

            double duration = 0;
            if (resp.Headers.TryGetValues("X-Audio-Duration", out var durValues))
            {
                double.TryParse(durValues.FirstOrDefault(), out duration);
            }

            int sr = 24000;
            if (resp.Headers.TryGetValues("X-Sample-Rate", out var srValues))
            {
                int.TryParse(srValues.FirstOrDefault(), out sr);
            }

            SetState(prevState, $"合成成功 (耗时: {sw.ElapsedMilliseconds}ms, 时长: {duration:F2}s)");
            SynthesisCompleted?.Invoke(rawText, duration, sw.ElapsedMilliseconds);

            return new TtsSynthesisResult(true, wavBytes, duration, sw.ElapsedMilliseconds, sr, null);
        }
        catch (OperationCanceledException)
        {
            SetState(prevState, "合成已取消");
            return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, "用户已取消");
        }
        catch (Exception ex)
        {
            SetState(prevState, $"合成异常: {ex.Message}");
            return new TtsSynthesisResult(false, null, 0, sw.ElapsedMilliseconds, 24000, ex.Message);
        }
    }

    public static MMDevice? FindFirstVirtualAudioDevice(MMDeviceEnumerator enumerator)
    {
        try
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (IsVirtualAudioDevice(d.FriendlyName))
                {
                    return d;
                }
            }
        }
        catch { }
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

        return (micDevice, monDevice, isSameDevice);
    }

    /// <summary>
    /// 流式分句推流管线：第 1 个分句合成完毕立刻唤醒声卡开始推流，后续分句后台并发合成并无缝追加进缓冲队列
    /// 实现从“整段长等待”到“毫秒级首字出声”的飞跃
    /// </summary>
    public async Task<bool> PlayStreamAsync(
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
        if (clauses == null || clauses.Count == 0) return false;

        try
        {
            await _playbackQueueLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        CancellationTokenSource playbackCts;
        lock (_playbackLock)
        {
            _activePlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            playbackCts = _activePlaybackCts;
        }

        var token = playbackCts.Token;
        PlaybackActiveStateChanged?.Invoke(true);

        try
        {
            return await Task.Run(async () =>
            {
                using var enumerator = new MMDeviceEnumerator();
                var (micDevice, monDevice, isSameDevice) = ResolveDevices(enumerator, virtualMicDeviceId, monitorDeviceId, enableMonitor);

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
                            if (mixFormat.Channels == 2 && sampleProvider.WaveFormat.Channels == 1)
                            {
                                sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
                            }
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
                            if (mixFormat.Channels == 2 && sampleProvider.WaveFormat.Channels == 1)
                            {
                                sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
                            }
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
                            if (mixFormat.Channels == 2 && sampleProvider.WaveFormat.Channels == 1)
                            {
                                sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
                            }
                            if (sampleProvider.WaveFormat.SampleRate != mixFormat.SampleRate)
                            {
                                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, mixFormat.SampleRate);
                            }
                            var volProvider = new SoftLimiterSampleProvider(sampleProvider, monitorVolume);
                            playerMon = new WasapiOut(monDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
                            playerMon.Init(volProvider);
                        }
                    }

                    bool startedPlayback = false;
                    int total = clauses.Count;
                    var audioQueue = System.Threading.Channels.Channel.CreateUnbounded<(int index, byte[] pcm, double duration)>();
                    byte[] silencePad = new byte[2400]; // 50ms 句间自然呼吸停顿 (24000 * 2 * 0.05 = 2400 bytes)

                    // ── 生产者任务：GPU 连续高速推理，不受音频播放速度拖累 ──
                    var producerTask = Task.Run(async () =>
                    {
                        try
                        {
                            for (int i = 0; i < total; i++)
                            {
                                if (token.IsCancellationRequested) break;
                                onClauseSynthesized?.Invoke(i, total);

                                var res = await SynthesizeAsync(clauses[i], token);
                                if (token.IsCancellationRequested) break;

                                if (res.Success && res.AudioBytes != null && res.AudioBytes.Length > 0)
                                {
                                    using var ms = new MemoryStream(res.AudioBytes);
                                    using var reader = new WaveFileReader(ms);
                                    byte[] pcm = new byte[reader.Length];
                                    int read = reader.Read(pcm, 0, pcm.Length);
                                    if (read > 0)
                                    {
                                        byte[] actual = (read == pcm.Length) ? pcm : pcm[..read];
                                        await audioQueue.Writer.WriteAsync((i, actual, res.DurationSeconds), token);
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            LogOccurred?.Invoke($"分句合成生产者异常: {ex.Message}");
                        }
                        finally
                        {
                            audioQueue.Writer.TryComplete();
                        }
                    }, token);

                    // ── 消费者任务：从队列按顺序取音频，无缝追加到 10 分钟超大缓冲区播放 ──
                    try
                    {
                        while (await audioQueue.Reader.WaitToReadAsync(token))
                        {
                            while (audioQueue.Reader.TryRead(out var item))
                            {
                                if (token.IsCancellationRequested) break;

                                // 句间平滑微停顿（首句之后追加，防音素硬粘连）
                                if (startedPlayback)
                                {
                                    if (isSameDevice && bufferSame != null) bufferSame.AddSamples(silencePad, 0, silencePad.Length);
                                    else
                                    {
                                        if (bufferMic != null) bufferMic.AddSamples(silencePad, 0, silencePad.Length);
                                        if (bufferMon != null) bufferMon.AddSamples(silencePad, 0, silencePad.Length);
                                    }
                                }

                                // 送入无界音频队列（支持任意长句，绝不截断、绝不溢出、绝不死锁）
                                if (isSameDevice && bufferSame != null)
                                {
                                    bufferSame.AddSamples(item.pcm, 0, item.pcm.Length);
                                }
                                else
                                {
                                    if (bufferMic != null) bufferMic.AddSamples(item.pcm, 0, item.pcm.Length);
                                    if (bufferMon != null) bufferMon.AddSamples(item.pcm, 0, item.pcm.Length);
                                }

                                // 首句即刻唤醒声卡推流开播
                                if (!startedPlayback)
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

                                onClausePlaying?.Invoke(item.index + 1, total);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }

                    await producerTask;

                    // 句子全部合成送入后，等待缓冲区全部音频彻底播放完毕
                    if (startedPlayback)
                    {
                        while (!token.IsCancellationRequested)
                        {
                            int rem = isSameDevice
                                ? (bufferSame?.BufferedBytes ?? 0)
                                : Math.Max(bufferMic?.BufferedBytes ?? 0, bufferMon?.BufferedBytes ?? 0);
                            if (rem <= 0) break;
                            await Task.Delay(30, token);
                        }
                        await Task.Delay(150, token); // 硬件延迟余音淡出
                    }

                    return startedPlayback;
                }
                finally
                {
                    try { playerSame?.Stop(); playerSame?.Dispose(); bufferSame?.ClearBuffer(); } catch { }
                    try { playerMic?.Stop(); playerMic?.Dispose(); bufferMic?.ClearBuffer(); } catch { }
                    try { playerMon?.Stop(); playerMon?.Dispose(); bufferMon?.ClearBuffer(); } catch { }
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
            PlaybackActiveStateChanged?.Invoke(false);
            _playbackQueueLock.Release();
        }
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

        // 停止上一次未完成的推流
        StopPlayback();

        CancellationTokenSource playbackCts;
        lock (_playbackLock)
        {
            _activePlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            playbackCts = _activePlaybackCts;
        }

        var token = playbackCts.Token;

        await Task.Run(async () =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var (micDevice, monDevice, isSameDevice) = ResolveDevices(enumerator, virtualMicDeviceId, monitorDeviceId, enableMonitor);

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
        }, token);
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
            if (mixFormat.Channels == 2 && sampleProvider.WaveFormat.Channels == 1)
            {
                sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
            }

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

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            _activePlaybackCts?.Cancel();
            _activePlaybackCts?.Dispose();
            _activePlaybackCts = null;
        }
    }

    public void Dispose()
    {
        StopPlayback();
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


