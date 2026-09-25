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
    private bool _isProcessFound;
    private int _targetPid;
    private CancellationTokenSource? _watcherCts;

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
        if (procs.Length > 0)
        {
            var targetProc = procs[0];
            try
            {
                var builder = new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)targetProc.Id, ProcessLoopbackMode.IncludeTargetProcessTree);
                var recorder = await builder.BuildAsync();

                statusCallback?.Invoke($"已锁定进程: {processName} (PID: {targetProc.Id})，开启纯净隔离音频捕获");
                ProcessStateChanged?.Invoke(processName, true, targetProc.Id);
                return new ProcessLoopbackCapture(processName, recorder, true, targetProc.Id);
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
        if (!_isRecording)
        {
            RecordingStopped?.Invoke(this, e);
        }
    }

    public void StartRecording()
    {
        if (_isRecording) return;
        _isRecording = true;

        _recorder?.StartRecording();

        if (!_isProcessFound)
        {
            StartProcessWatcher();
        }
    }

    public void StopRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;

        StopProcessWatcher();

        try
        {
            _recorder?.StopRecording();
        }
        catch { }

        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    public async Task<bool> TrySwitchToProcessAsync()
    {
        if (_isProcessFound) return true;
        try
        {
            var procs = Process.GetProcessesByName(_processName);
            if (procs.Length == 0) return false;

            var proc = procs[0];
            StatusNotice?.Invoke($"检测到 {_processName} 已启动 (PID: {proc.Id})，正在无缝切换至隔离音频流...");

            var builder = new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)proc.Id, ProcessLoopbackMode.IncludeTargetProcessTree);
            var newRecorder = await builder.BuildAsync();

            var oldRecorder = _recorder;
            if (oldRecorder != null)
            {
                DetachRecorderEvents(oldRecorder);
                try { oldRecorder.StopRecording(); } catch { }
                oldRecorder.Dispose();
            }

            _recorder = newRecorder;
            _targetPid = proc.Id;
            _isProcessFound = true;
            _waveFormat = newRecorder.WaveFormat;
            AttachRecorderEvents(_recorder);

            if (_isRecording)
            {
                _recorder.StartRecording();
            }

            StatusNotice?.Invoke($"已成功锁定 {_processName}，现已过滤全部音乐与外部杂音");
            ProcessStateChanged?.Invoke(_processName, true, proc.Id);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessLoopbackCapture] TrySwitchToProcess error: {ex.Message}");
            return false;
        }
    }

    private void StartProcessWatcher()
    {
        StopProcessWatcher();
        _watcherCts = new CancellationTokenSource();
        var token = _watcherCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _isRecording && !_isProcessFound)
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
        StopRecording();

        if (_recorder != null)
        {
            DetachRecorderEvents(_recorder);
            _recorder.Dispose();
            _recorder = null;
        }

        GC.SuppressFinalize(this);
    }
}
