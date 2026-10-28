using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services.Tts;

/// <summary>
/// TTS 服务健康探测与权重别名注册表（分部类，机械拆分）。
///
/// 从 TtsService.cs 拆出来的原因：这段是「HTTP 探测 + 用 generation/sequence 守卫防止迟到响应
/// 覆盖新状态」的完整逻辑，与进程管理、合成请求、音频播放都没有交集。
/// 所有字段仍在 TtsService.cs 中（partial 共享），本次不改变任何行为。
/// </summary>
public sealed partial class TtsService
{
    /// <summary>
    /// 服务端是否已经注册了指定权重变体（只有注册过，请求里带 model 才是安全的）
    /// </summary>
    public bool IsModelRegistered(string? fileName)
    {
        var aliases = _registeredModelAliases;
        if (aliases == null || string.IsNullOrWhiteSpace(fileName)) return false;
        return aliases.Contains(fileName, StringComparer.OrdinalIgnoreCase);
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
}
