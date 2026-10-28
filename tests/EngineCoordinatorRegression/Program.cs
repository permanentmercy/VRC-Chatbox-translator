using EngineCoordinatorTests;
using VrcChatboxDemo.Services.Speech;

// SpeechEngineCoordinator 回归：引擎启停 / 切换 / 回滚的并发状态机。
// 这些用例原先靠 Roslyn 把生产方法抽进替身类才能测，现在协调器是真实类，直接用假引擎驱动。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task Case(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

(FakeContext Context, FakeEngine Whisper, FakeEngine Live, SpeechEngineCoordinator Coordinator) Build(string engine = "Whisper", bool enabled = false)
{
    var context = new FakeContext { Engine = engine, Enabled = enabled };
    var whisper = new FakeEngine("Whisper");
    var live = new FakeEngine("LiveCaptions");
    return (context, whisper, live, new SpeechEngineCoordinator(context, whisper, live));
}

await Case("disabling during Whisper startup stops it instead of resurrecting", async () =>
{
    var (context, whisper, live, coordinator) = Build();
    var gate = whisper.StartGate = Gate();
    context.Enabled = true;

    var apply = coordinator.ApplyAsync();
    await whisper.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    context.Enabled = false;
    coordinator.InvalidateIntent();
    var stop = coordinator.ApplyAsync();

    gate.SetResult(true);
    await Task.WhenAll(apply, stop).WaitAsync(TimeSpan.FromSeconds(5));

    Check(!context.Enabled, "switch stayed on after the user turned it off");
    Check(!whisper.IsRunning, "Whisper resurrected after being disabled");
    Check(whisper.StartCalls == 1, "closing started the engine again: " + whisper.StartCalls);
});

await Case("disabling during LiveCaptions startup stops it", async () =>
{
    var (context, whisper, live, coordinator) = Build("LiveCaptions");
    var gate = live.StartGate = Gate();
    context.Enabled = true;

    var apply = coordinator.ApplyAsync();
    await live.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    context.Enabled = false;
    coordinator.InvalidateIntent();
    var stop = coordinator.ApplyAsync();
    gate.SetResult(true);
    await Task.WhenAll(apply, stop).WaitAsync(TimeSpan.FromSeconds(5));

    Check(!context.Enabled && !live.IsRunning, "LiveCaptions resurrected after close");
    Check(live.StartCalls == 1, "close request started the engine again");
});

await Case("startup failure rolls the switch back", async () =>
{
    foreach (string engine in new[] { "Whisper", "LiveCaptions" })
    {
        var (context, whisper, live, coordinator) = Build(engine);
        var target = engine == "Whisper" ? whisper : live;
        target.StartResult = false;
        context.Enabled = true;

        await coordinator.ApplyAsync();

        Check(!context.Enabled, engine + " startup failure left the switch on");
        Check(!target.IsRunning, engine + " reported running after failure");
    }
});

await Case("started-but-not-running also rolls back", async () =>
{
    var (context, whisper, _, coordinator) = Build();
    whisper.RunningAfterStart = false;
    context.Enabled = true;

    await coordinator.ApplyAsync();

    Check(!context.Enabled, "engine that started but is not running left the switch on");
});

await Case("stale failure cannot overwrite a newer enable", async () =>
{
    var (context, whisper, _, coordinator) = Build();
    var gate = whisper.StartGate = Gate();
    context.Enabled = true;

    var apply = coordinator.ApplyAsync();
    await whisper.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    context.Enabled = false;
    coordinator.InvalidateIntent();
    context.Enabled = true;
    coordinator.InvalidateIntent();

    gate.SetResult(false);   // 旧请求失败
    await apply.WaitAsync(TimeSpan.FromSeconds(5));

    Check(context.Enabled, "stale failure turned the new enable off");
    Check(whisper.IsRunning, "new enable was not started");
});

await Case("engine switch during startup stops the old engine", async () =>
{
    var (context, whisper, live, coordinator) = Build();
    var gate = whisper.StartGate = Gate();
    context.Enabled = true;

    var first = coordinator.ApplyAsync();
    await whisper.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    context.Engine = "LiveCaptions";
    coordinator.InvalidateIntent();
    gate.SetResult(true);
    await first.WaitAsync(TimeSpan.FromSeconds(5));
    await coordinator.ApplyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    Check(context.Enabled && live.IsRunning, "LiveCaptions did not end up running");
    Check(!whisper.IsRunning, "stale Whisper engine remained running");
});

await Case("background failure while idle turns the switch off", async () =>
{
    var (context, whisper, _, coordinator) = Build();
    context.Enabled = true;
    await coordinator.ApplyAsync();
    int before = context.StateNotifications;

    coordinator.OnEngineRunningChanged("Whisper", false);

    Check(!context.Enabled, "background failure did not turn the switch off");
    Check(context.StateNotifications > before, "UI was not notified about the failure");
});

await Case("background event from a non-selected engine is ignored", async () =>
{
    var (context, whisper, live, coordinator) = Build("Whisper");
    context.Enabled = true;
    await coordinator.ApplyAsync();

    coordinator.OnEngineRunningChanged("LiveCaptions", false);

    Check(context.Enabled, "event from the idle engine turned the switch off");
    Check(whisper.IsRunning, "idle engine event disturbed the active engine");
});

await Case("engine events during apply are treated as transitions", async () =>
{
    var (context, whisper, _, coordinator) = Build();
    var gate = whisper.StartGate = Gate();
    context.Enabled = true;

    var apply = coordinator.ApplyAsync();
    await whisper.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Apply 期间引擎上报 false 属于过渡态，不能被当成"用户关闭"
    coordinator.OnEngineRunningChanged("Whisper", false);
    Check(context.Enabled, "transition event was treated as a user close");

    gate.SetResult(true);
    await apply.WaitAsync(TimeSpan.FromSeconds(5));
    Check(context.Enabled && whisper.IsRunning, "engine did not finish starting");
});

await Case("exception during apply rolls back and reports status", async () =>
{
    var context = new FakeContext { Engine = "Whisper", Enabled = true };
    var whisper = new ThrowingEngine("Whisper");
    var live = new FakeEngine("LiveCaptions");
    var coordinator = new SpeechEngineCoordinator(context, whisper, live);

    await coordinator.ApplyAsync();

    Check(!context.Enabled, "exception left the switch on");
    Check(context.Statuses.Count > 0, "exception was not reported to the status line");
});

await Case("concurrent applies are serialized", async () =>
{
    var (context, whisper, _, coordinator) = Build();
    context.Enabled = true;

    var first = coordinator.ApplyAsync();
    var second = coordinator.ApplyAsync();
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

    Check(whisper.IsRunning, "engine not running after concurrent applies");
    Check(context.Enabled, "switch turned off by concurrent applies");
});

await Case("every apply ends by notifying the UI", async () =>
{
    var (context, _, _, coordinator) = Build();
    context.Enabled = false;
    await coordinator.ApplyAsync();
    Check(context.StateNotifications >= 1, "no state notification after apply");
    Check(context.DisplayNotifications >= 1, "no display notification after apply");
});

Console.WriteLine($"engine coordinator regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));

sealed class ThrowingEngine : ISpeechEngineControl
{
    public ThrowingEngine(string name) => Name = name;
    public string Name { get; }
    public bool IsRunning => false;
    public Task<bool> StartAsync() => throw new InvalidOperationException("engine exploded");
    public Task StopAsync() => Task.CompletedTask;
}
