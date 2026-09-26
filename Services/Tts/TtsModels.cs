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
    string? Error
);

public record TtsSynthesisResult(
    bool Success,
    byte[]? AudioBytes,
    double DurationSeconds,
    long CostMs,
    int SampleRate,
    string? ErrorMessage
);
