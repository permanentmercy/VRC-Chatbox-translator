using System;

namespace VrcChatboxDemo.Services.Tts;

public enum TtsServerState
{
    Stopped,
    Starting,
    Ready,
    Synthesizing,
    Error
}

public record TtsDeviceInfo(
    string Id,
    string Name,
    bool IsVirtualRecommendation,
    bool IsDefault
);

public record TtsHealthStatus(
    string Status,
    bool Ready,
    string Model,
    bool LowVram,
    string? Error,
    /// <summary>服务端通过 --models 注册过的权重别名；空表示服务端未注册任何变体
    /// （老版本脚本或手工启动），此时不能给请求带 model 字段。</summary>
    IReadOnlyList<string>? RegisteredModels = null
);

/// <summary>
/// 一个可选的推理权重（从 checkpoints 目录扫描得到）
/// </summary>
public record TtsModelOption(
    string FileName,
    string DisplayName,
    string Description,
    long SizeBytes,
    bool IsQuantized
);

public record TtsSynthesisResult(
    bool Success,
    byte[]? AudioBytes,
    double DurationSeconds,
    long CostMs,
    int SampleRate,
    string? ErrorMessage
);
