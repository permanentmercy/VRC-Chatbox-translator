using VrcChatboxDemo.Services.Speech;

namespace EngineCoordinatorTests;

/// <summary>可编排的假引擎：Start 可以被闸门卡住，用来精确制造"启动中"状态。</summary>
public sealed class FakeEngine : ISpeechEngineControl
{
    public FakeEngine(string name) => Name = name;

    public string Name { get; }
    public bool IsRunning { get; set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }

    /// <summary>Start 的返回值；false 表示启动失败。</summary>
    public bool StartResult { get; set; } = true;

    /// <summary>启动后是否真的在运行（模拟"启动了但立刻挂掉"）。</summary>
    public bool RunningAfterStart { get; set; } = true;

    /// <summary>非空时 Start 会等它完成，用于制造并发窗口。</summary>
    public TaskCompletionSource<bool>? StartGate { get; set; }

    /// <summary>Start 进入时完成，测试可据此等待"已经进到启动里"。</summary>
    public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> StartAsync()
    {
        StartCalls++;
        Entered.TrySetResult(true);
        var gate = StartGate;
        StartGate = null;
        if (gate != null) await gate.Task;

        IsRunning = StartResult && RunningAfterStart;
        return StartResult;
    }

    public Task StopAsync()
    {
        StopCalls++;
        IsRunning = false;
        return Task.CompletedTask;
    }
}

/// <summary>假上下文：把配置事实变成可设置的属性，并记录界面通知次数。</summary>
public sealed class FakeContext : ISpeechEngineContext
{
    public string Engine { get; set; } = "Whisper";
    public bool Enabled { get; set; }
    public int DisplayNotifications { get; private set; }
    public int StateNotifications { get; private set; }
    public List<string> Statuses { get; } = new();

    public void NotifyDisplaySettingsChanged() => DisplayNotifications++;
    public void NotifyRecognitionStateChanged() => StateNotifications++;
    public void SetStatus(string message) => Statuses.Add(message);
}
