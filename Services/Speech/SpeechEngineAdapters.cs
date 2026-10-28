using System;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services.Speech;

/// <summary>把 Whisper 引擎适配成 <see cref="ISpeechEngineControl"/>（启动参数从配置读取）。</summary>
public sealed class WhisperEngineControl : ISpeechEngineControl
{
    private readonly SpeechService _service;
    private readonly Func<(string LanguageTag, string AudioDeviceId)> _startupArgs;

    public WhisperEngineControl(SpeechService service, Func<(string LanguageTag, string AudioDeviceId)> startupArgs)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _startupArgs = startupArgs ?? throw new ArgumentNullException(nameof(startupArgs));
    }

    public string Name => "Whisper";
    public bool IsRunning => _service.IsListening;

    public Task<bool> StartAsync()
    {
        var (languageTag, audioDeviceId) = _startupArgs();
        return _service.StartAsync(languageTag, audioDeviceId);
    }

    public Task StopAsync() => _service.StopAsync();
}

/// <summary>把 LiveCaptions 引擎适配成 <see cref="ISpeechEngineControl"/>。</summary>
public sealed class LiveCaptionsEngineControl : ISpeechEngineControl
{
    private readonly LiveCaptionsService _service;
    private readonly Func<bool> _hideNativeWindow;

    public LiveCaptionsEngineControl(LiveCaptionsService service, Func<bool> hideNativeWindow)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _hideNativeWindow = hideNativeWindow ?? throw new ArgumentNullException(nameof(hideNativeWindow));
    }

    public string Name => "LiveCaptions";
    public bool IsRunning => _service.IsRunning;

    public Task<bool> StartAsync() => _service.StartAsync(_hideNativeWindow());
    public Task StopAsync() => _service.StopAsync();
}
