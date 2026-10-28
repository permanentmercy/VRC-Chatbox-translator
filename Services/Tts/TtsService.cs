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
using VrcChatboxDemo.Services.Synthesis;

namespace VrcChatboxDemo.Services.Tts;

public sealed partial class TtsService : IDisposable
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

    // 健康探测与权重别名注册表（IsModelRegistered / CheckHealthAsync /
    // CheckHealthCoreAsync / RefreshHealthAsync）已移到 TtsService.Health.cs。

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
    // 设备相关成员（GetPlaybackDevices / IsVirtualAudioDevice / FindFirstVirtualAudioDevice /
    // ResolveDevices / DisposeDevices）已移到 TtsService.Devices.cs。

    // 权重与音色目录扫描已移入 Core：TtsCatalog（无状态、不读单例配置）。
    // 这里保留实例方法做兼容转发，UI 与内部调用无需改动。

    /// <summary>兼容转发：目录定位见 <see cref="TtsCatalog.ResolvePromptsDir"/>。</summary>
    public static string? ResolvePromptsDir(string? serverScriptPath = null) =>
        TtsCatalog.ResolvePromptsDir(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath);

    /// <summary>兼容转发：音色扫描见 <see cref="TtsCatalog.DiscoverPrompts"/>。</summary>
    public List<TtsPromptOption> DiscoverPrompts(string? serverScriptPath = null) =>
        TtsCatalog.DiscoverPrompts(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath, LogOccurred);

    /// <summary>兼容转发：配套关系判断见 <see cref="TtsCatalog.IsPromptPairedWith"/>。</summary>
    public static bool IsPromptPairedWith(string promptFile, string checkpointFile) =>
        TtsCatalog.IsPromptPairedWith(promptFile, checkpointFile);

    /// <summary>兼容转发：目录定位见 <see cref="TtsCatalog.ResolveCheckpointsDir"/>。</summary>
    public static string? ResolveCheckpointsDir(string? serverScriptPath = null) =>
        TtsCatalog.ResolveCheckpointsDir(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath);

    /// <summary>兼容转发：权重扫描见 <see cref="TtsCatalog.DiscoverModels"/>。</summary>
    public List<TtsModelOption> DiscoverModels(string? serverScriptPath = null) =>
        TtsCatalog.DiscoverModels(serverScriptPath ?? SettingsService.Instance.Config.TtsServerScriptPath, LogOccurred);

    // 服务端进程生命周期（StartManagedServerAsync / BuildModelRegistryArgs /
    // StopManagedServer / StopOwnedProcess）已移到 TtsService.Server.cs。

    // 合成请求（ResolveRequestModelAlias / ResolveRequestPrompt / BeginSynthesis /
    // IsCurrentSynthesis / TrySetSynthesisState / ReadStreamBodyAsync /
    // SynthesizeAsync / SynthesizeStreamAsync）已移到 TtsService.Synthesis.cs。

    // 其余播放成员（PlayStreamAsync 重载 / PlayAudioAsync / MatchDeviceChannelCount /
    // PlayToSingleEndpointAsync / BeginPlayback / EndPlayback / StopPlayback）
    // 已移到 TtsService.Playback.cs。


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        StopManagedServer();
        _httpClient.Dispose();
    }
}

// 播放侧的两个 Provider（软限幅 / 无界流式队列）已移入 Core：
// Services/Synthesis/PlaybackProviders.cs

