using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 现代 Windows 官方 Process-specific Audio Loopback 捕获器 (基于 NAudio 3.1.0 WasapiRecorderBuilder)
/// 核心特性：
/// 1. 指定目标进程 (如 VRChat)，只捕获该进程及其子进程发出的全部音频流；
/// 2. 100% 物理级绝对隔离：后台播放的音乐、B站/网页视频、语音软件等 0% 混入转录；
/// 3. 无需安装任何驱动或虚拟声卡，耳机原声正常播放，零延迟；
/// 4. 目标进程未启动时自动平滑回退至系统默认输出，并在游戏启动后无缝热切换至进程隔离。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
public class ProcessLoopbackCapture : IWaveIn, IDisposable
{
    private readonly string _processName;
    private WasapiRecorder? _recorder;
    private WaveFormat _waveFormat;
    private bool _isRecording;
    private bool _isStopping;
    private bool _isProcessFound;
    private bool _formatMismatch;
    private int _targetPid;
    private CancellationTokenSource? _watcherCts;
    private readonly object _recorderSync = new();
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private bool _disposed;
    private int _captureGeneration;

    public WaveFormat WaveFormat
    {
        get => _recorder?.WaveFormat ?? _waveFormat;
        set => _waveFormat = value;
    }

    public bool IsRecording => _isRecording;
    public bool IsProcessFound => _isProcessFound;
    public string TargetProcessName => _processName;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public event Action<string>? StatusNotice;

    public static event Action<string, bool, int>? ProcessStateChanged;

    private ProcessLoopbackCapture(string processName, WasapiRecorder recorder, bool isProcessFound, int targetPid)
    {
        _processName = processName;
        _recorder = recorder;
        _waveFormat = recorder.WaveFormat;
        _isProcessFound = isProcessFound;
        _targetPid = targetPid;

        AttachRecorderEvents(_recorder);
    }

    /// <summary>
    /// 异步创建进程隔离音频捕获器
    /// </summary>
    public static async Task<ProcessLoopbackCapture> CreateAsync(string processName = "VRChat", Action<string>? statusCallback = null)
    {
        var procs = Process.GetProcessesByName(processName);
        int targetPid = procs.Length > 0 ? procs[0].Id : 0;
        foreach (var p in procs) p.Dispose();

        if (targetPid != 0)
        {
            try
            {
                var builder = new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)targetPid, ProcessLoopbackMode.IncludeTargetProcessTree);
                var recorder = await builder.BuildAsync();

                statusCallback?.Invoke($"已锁定进程: {processName} (PID: {targetPid})，开启纯净隔离音频捕获");
                ProcessStateChanged?.Invoke(processName, true, targetPid);
                return new ProcessLoopbackCapture(processName, recorder, true, targetPid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLoopbackCapture] Failed to attach process {processName}: {ex.Message}");
                statusCallback?.Invoke($"绑定进程 {processName} 异常 ({ex.Message})，降级为系统扬声器");
            }
        }
        else
        {
            statusCallback?.Invoke($"未检测到 {processName} 运行，隔离监听待机中 (检测到启动后将自动连接)...");
        }

        // 回退至系统默认输出回路待机（目标进程启动前保持静音过滤）
        var fallbackBuilder = new WasapiRecorderBuilder().WithLoopbackCapture();
        var fallbackRecorder = await fallbackBuilder.BuildAsync();
        ProcessStateChanged?.Invoke(processName, false, 0);
        return new ProcessLoopbackCapture(processName, fallbackRecorder, false, 0);
    }

    private static bool AreWaveFormatsCompatible(WaveFormat a, WaveFormat b)
    {
        if (a.SampleRate != b.SampleRate || a.Channels != b.Channels ||
            a.BitsPerSample != b.BitsPerSample || a.BlockAlign != b.BlockAlign || a.Encoding != b.Encoding)
        {
            return false;
        }
        // Extensible 的容器位深/Encoding 相同不代表样本可互换：还要比较真正的
        // PCM/float 子格式、有效位及声道布局，否则已构建的解码链会把音频解错。
        if (a is WaveFormatExtensible || b is WaveFormatExtensible)
        {
            return a is WaveFormatExtensible ea && b is WaveFormatExtensible eb &&
                   ea.SubFormat == eb.SubFormat && ea.ValidBitsPerSample == eb.ValidBitsPerSample &&
                   ea.ChannelMask == eb.ChannelMask;
        }
        return true;
    }

    private void AttachRecorderEvents(WasapiRecorder recorder)
    {
        recorder.DataAvailable += OnRecorderDataAvailable;
        recorder.RecordingStopped += OnRecorderRecordingStopped;
    }

    private void DetachRecorderEvents(WasapiRecorder recorder)
    {
        recorder.DataAvailable -= OnRecorderDataAvailable;
        recorder.RecordingStopped -= OnRecorderRecordingStopped;
    }

    private void OnRecorderDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (!_isRecording) return;
        // 若目标隔离进程未启动，静音待机，绝不混入任何系统背景音乐或杂音
        if (!_isProcessFound) return;

        byte[] array = buffer.ToArray();
        DataAvailable?.Invoke(this, new WaveInEventArgs(array, array.Length));
    }

    private void OnRecorderRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || _isStopping) return;

        // 录制期间收到停止事件 = 目标进程退出/会话中断等异常情况。
        // 旧逻辑写成了 if (!_isRecording)，反而把录制中的异常完全吞掉，
        // 导致 SpeechService 永远收不到错误、UI 仍显示“已连接”，实际永久静音。
        _isRecording = false;
        _isProcessFound = false;
        ProcessStateChanged?.Invoke(_processName, false, 0);

        RecordingStopped?.Invoke(this, e.Exception != null
            ? e
            : new StoppedEventArgs(new InvalidOperationException("进程隔离音频捕获已意外停止")));
    }

    public void StartRecording()
    {
        lock (_recorderSync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProcessLoopbackCapture));
            if (_isRecording) return;
            _captureGeneration++;
            _isRecording = true;
            try { _recorder?.StartRecording(); }
            catch { _isRecording = false; throw; }

            if (!_isProcessFound)
            {
                StartProcessWatcher();
            }
        }
    }

    public void StopRecording()
    {
        lock (_recorderSync)
        {
            // 即使尚未开始录制，Stop 也要使已经在 BuildAsync 中的候选失效。
            _captureGeneration++;
            StopProcessWatcher();
            if (!_isRecording) return;
            _isRecording = false;
            _isStopping = true;
            try { _recorder?.StopRecording(); }
            catch { }
            finally { _isStopping = false; }
        }

        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    public async Task<bool> TrySwitchToProcessAsync()
    {
        // 后台 watcher 与设置页的手动重连可同时进入，只允许一个 native build/publish。
        // 在排队前记录代次，Stop 也必须让尚未开始 build 的旧请求失效。
        int generation;
        lock (_recorderSync)
        {
            if (_disposed) return false;
            generation = _captureGeneration;
        }
        await _switchLock.WaitAsync();
        WasapiRecorder? candidate = null;
        try
        {
            lock (_recorderSync)
            {
                if (_disposed || generation != _captureGeneration) return false;
                if (_isProcessFound) return true;
                if (_formatMismatch) return false;
            }

            var procs = Process.GetProcessesByName(_processName);
            int targetPid;
            try
            {
                if (procs.Length == 0) return false;
                targetPid = procs[0].Id;
            }
            finally { foreach (var p in procs) p.Dispose(); }

            StatusNotice?.Invoke($"检测到 {_processName} 已启动 (PID: {targetPid})，正在无缝切换至隔离音频流...");
            var builder = new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)targetPid, ProcessLoopbackMode.IncludeTargetProcessTree);
            candidate = await builder.BuildAsync();

            lock (_recorderSync)
            {
                // Stop/Dispose 不等待可能缓慢的 native build；迟到候选由 finally 回收，绝不安装。
                if (_disposed || generation != _captureGeneration) return false;
                if (!AreWaveFormatsCompatible(_waveFormat, candidate.WaveFormat))
                {
                    _formatMismatch = true;
                    StopProcessWatcher();
                    StatusNotice?.Invoke("新进程音频格式与当前监听不一致，已保持原音频流；请重启语音识别以启用进程隔离。");
                    return false;
                }

                // 先验证候选可启动，再拆掉旧流；失败时仍保有原 recorder，候选不会泄漏。
                if (_isRecording) candidate.StartRecording();
                var oldRecorder = _recorder;
                if (oldRecorder != null)
                {
                    DetachRecorderEvents(oldRecorder);
                    try { oldRecorder.StopRecording(); } catch { }
                    try { oldRecorder.Dispose(); } catch { }
                }

                _recorder = candidate;
                candidate = null; // 所有权转移给实例，finally 不再释放已安装的流。
                _targetPid = targetPid;
                _isProcessFound = true;
                _waveFormat = _recorder.WaveFormat;
                AttachRecorderEvents(_recorder);

                StatusNotice?.Invoke($"已成功锁定 {_processName}，现已过滤全部音乐与外部杂音");
                ProcessStateChanged?.Invoke(_processName, true, targetPid);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessLoopbackCapture] TrySwitchToProcess error: {ex.Message}");
            return false;
        }
        finally
        {
            try { candidate?.Dispose(); } catch { }
            _switchLock.Release();
        }
    }

    private void StartProcessWatcher()
    {
        StopProcessWatcher();
        _watcherCts = new CancellationTokenSource();
        var token = _watcherCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _isRecording && !_isProcessFound && !_formatMismatch)
            {
                try
                {
                    await Task.Delay(3000, token);
                    if (await TrySwitchToProcessAsync())
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessLoopbackCapture] Watcher error: {ex.Message}");
                }
            }
        }, token);
    }

    private void StopProcessWatcher()
    {
        try
        {
            _watcherCts?.Cancel();
            _watcherCts?.Dispose();
            _watcherCts = null;
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_recorderSync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopRecording();

        lock (_recorderSync)
        {
            _isProcessFound = false;
            if (_recorder != null)
            {
                DetachRecorderEvents(_recorder);
                try { _recorder.Dispose(); }
                finally { _recorder = null; }
            }
        }

        // 在途/排队的切换仍需 Release，不 Dispose _switchLock。
        GC.SuppressFinalize(this);
    }
}
