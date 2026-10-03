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
/// 一个可选的音色特征（从 prompts 目录扫描得到）
/// </summary>
public record TtsPromptOption(
    string FileName,
    string DisplayName,
    string Description,
    long SizeBytes
);

/// <summary>
/// 一个可选的推理权重（从 checkpoints 目录扫描得到）
/// </summary>
public record TtsModelOption(
    string FileName,
    string DisplayName,
    string Description,
    long SizeBytes,
    bool IsQuantized,
    /// <summary>
    /// 是否有配套音色文件（prompts/&lt;权重名&gt;_prompt.pt）。
    /// **没有的话切过去声线不会变** —— IndexTTS 的音色由 GPT 权重 + cond_mel 音色特征
    /// 共同决定，服务端在没有配套 prompt 时会回退到默认音色。
    /// </summary>
    bool HasMatchingPrompt = false,
    /// <summary>是否是未微调的原始底模（如 gpt.pth），没有学过任何特定音色</summary>
    bool IsBaseModel = false
);

public record TtsSynthesisResult(
    bool Success,
    byte[]? AudioBytes,
    double DurationSeconds,
    long CostMs,
    int SampleRate,
    string? ErrorMessage
);
