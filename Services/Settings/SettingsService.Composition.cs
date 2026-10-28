using System;
using VrcChatboxDemo.Services.Speech;

namespace VrcChatboxDemo.Services;

/// <summary>
/// 组合根：把所有服务与事件的接线集中在这里，构造函数只留一份「装配清单」。
///
/// 从 SettingsService.cs 拆出来的原因：约 200 行的事件接线混在属性与状态之间，
/// 既看不出启动顺序，也很难判断某个服务是否被初始化过。现在每个 Compose* 方法
/// 对应一个子系统，顺序与原来的构造顺序完全一致（无行为变化）。
/// </summary>
public partial class SettingsService
{
    /// <summary>字幕管线的显示事件转发给界面订阅者（UI 只认 SettingsService 的公开事件）。</summary>
    private void ComposeSubtitleEvents()
    {
        _subtitle.RecognizedUpdated += text => SpeechRecognizedUpdated?.Invoke(text);
        _subtitle.HypothesisUpdated += text => SpeechHypothesisUpdated?.Invoke(text);
        _subtitle.TranslationUpdated += (text, latency) => TranslationUpdated?.Invoke(text, latency);
        _subtitle.DisplayChanged += () => DisplaySettingsChanged?.Invoke();
    }

    /// <summary>自动语种切换器：构造并按配置决定是否立刻启用。</summary>
    private void ComposeLiveCaptionsAutoSwitcher()
    {
        LiveCaptionsAutoSwitcher = new LiveCaptionsLanguageAutoSwitcher(LiveCaptionsService, this);
        if (Config.LiveCaptionsAutoDetectLanguage)
        {
            _ = LiveCaptionsAutoSwitcher.SetEnabledAsync(true, Config.LiveCaptionsAutoDetectIntervalSeconds);
        }
    }

    /// <summary>OSC 发送日志。</summary>
    private void ComposeOscLogging()
    {
        OscService.MessageSent += OnMessageSent;
    }

    /// <summary>外部接口需要的服务引用与变量联动。</summary>
    private void ComposeInboundServices()
    {
        InboundService.VariableService = VariableService;
        InboundService.PersistentTextService = PersistentTextService;

        VariableService.VariableUpdated += (varName, val) =>
        {
            _ = PersistentTextService.OnVariableUpdatedAsync(varName, VariableService, OscService);
        };
    }

    /// <summary>游戏内按键守卫：避让联动与启动。</summary>
    private void ComposeInGameGuard()
    {
        VrcInGameGuard.InGameMessageSent += () =>
        {
            PersistentTextService.NotifyInGameMessageSent();
        };
        VrcInGameGuard.InGameTyping += () =>
        {
            PersistentTextService.NotifyInGameTyping();
        };
        VrcInGameGuard.IsEnabled = Config.InGameAvoidanceEnabled;
        PersistentTextService.EnableInGameAvoidance = Config.InGameAvoidanceEnabled;
        PersistentTextService.AvoidanceSeconds = Config.InGameAvoidanceSeconds;
        PersistentTextService.TransientDurationSeconds = Config.TransientTextDurationSeconds;
        VrcInGameGuard.Start();
    }

    /// <summary>外部接口的消息入口与监听启动。</summary>
    private void ComposeInboundMessaging()
    {
        InboundService.MessageReceived += (text, source) =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.StartsWith("/avatar/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/tracking/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/input/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddLog($"API Inbound ({source})", text, true);
            if (InboundService.AutoSendToVrc)
            {
                _ = FinalizeSendAsync(text);
            }
            else
            {
                TriggerAutoFill(text);
            }
        };

        try
        {
            if (Config.InboundEnabled)
            {
                InboundService.Start(Config.InboundHttpPort, Config.InboundOscPort);
            }
        }
        catch { }
    }

    /// <summary>沉浸模式热键。</summary>
    private void ComposeHotkeys()
    {
        HotkeyService.ImmersiveHotkeyPressed += () =>
        {
            if (App.DispatcherQueueInstance != null)
            {
                App.DispatcherQueueInstance.TryEnqueue(ToggleImmersiveMode);
            }
            else
            {
                ToggleImmersiveMode();
            }
        };
    }

    /// <summary>Whisper 引擎事件 → 字幕管线（统一派发到 UI 线程）。</summary>
    private void ComposeSpeechEvents()
    {
        SpeechService.SpeechRecognized += text =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechRecognized(text));
            }
            else
            {
                OnSpeechRecognized(text);
            }
        };

        SpeechService.SpeechHypothesis += interimText =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechHypothesis(interimText));
            }
            else
            {
                OnSpeechHypothesis(interimText);
            }
        };

        SpeechService.StatusChanged += status =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = status;
                    SpeechStatusUpdated?.Invoke(status);
                });
            }
            else
            {
                SpeechStatus = status;
                SpeechStatusUpdated?.Invoke(status);
            }
        };

        SpeechService.ErrorOccurred += err =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = err;
                    AddLog("Speech", err, false);
                    SpeechStatusUpdated?.Invoke(err);
                });
            }
            else
            {
                SpeechStatus = err;
                AddLog("Speech", err, false);
                SpeechStatusUpdated?.Invoke(err);
            }
        };

        SpeechService.ListeningStateChanged += listening => SpeechEngines.OnEngineRunningChanged("Whisper", listening);
    }

    /// <summary>LiveCaptions 引擎事件 → 字幕管线（同样派发到 UI 线程）。</summary>
    private void ComposeLiveCaptionsEvents()
    {
        LiveCaptionsService.SpeechHypothesis += interimText =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechHypothesis(interimText));
            }
            else
            {
                OnSpeechHypothesis(interimText);
            }
        };

        LiveCaptionsService.SpeechRecognized += text =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() => OnSpeechRecognized(text));
            }
            else
            {
                OnSpeechRecognized(text);
            }
        };

        LiveCaptionsService.StatusChanged += status =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = status;
                    SpeechStatusUpdated?.Invoke(status);
                });
            }
            else
            {
                SpeechStatus = status;
                SpeechStatusUpdated?.Invoke(status);
            }
        };

        LiveCaptionsService.ErrorOccurred += err =>
        {
            if (App.DispatcherQueueInstance != null && !App.DispatcherQueueInstance.HasThreadAccess)
            {
                App.DispatcherQueueInstance.TryEnqueue(() =>
                {
                    SpeechStatus = err;
                    AddLog("LiveCaptions", err, false);
                    SpeechStatusUpdated?.Invoke(err);
                });
            }
            else
            {
                SpeechStatus = err;
                AddLog("LiveCaptions", err, false);
                SpeechStatusUpdated?.Invoke(err);
            }
        };

        LiveCaptionsService.RunningStateChanged += running => SpeechEngines.OnEngineRunningChanged("LiveCaptions", running);
    }

    /// <summary>常驻文本：按配置恢复启用状态与模板，必要时拉起保活循环。</summary>
    private void ComposePersistentText()
    {
        PersistentTextService.IsEnabled = Config.IsPersistentTextEnabled;
        PersistentTextService.CustomText = Config.PersistentCustomText;
        if (Config.IsPersistentTextEnabled && !string.IsNullOrWhiteSpace(Config.PersistentCustomText))
        {
            PersistentTextService.RestartLoop(VariableService, OscService);
        }
    }
}
