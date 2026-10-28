using ExitRegressionTests;
using VrcChatboxDemo.Services.Lifecycle;

// ShutdownCoordinator 回归：退出编排的顺序、幂等与时间预算。
// 这些用例原先需要把生产方法用 Roslyn 抽出来、再把 Kill/Environment.Exit 语法重写成记录器；
// 现在编排是真实类，终止动作直接由构造参数注入。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task Case(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

async Task Wait(Task task, int timeoutMs = 8000) => await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

(FakeShutdownContext Context, int[] Terminations, ShutdownCoordinator Coordinator) Build()
{
    var context = new FakeShutdownContext();
    var terminations = new int[1];
    var coordinator = new ShutdownCoordinator(context, () =>
    {
        terminations[0]++;
        context.Calls.Add("terminate");
    });
    return (context, terminations, coordinator);
}

await Case("tray exit saves, releases the lock, then terminates", async () =>
{
    var (context, terminations, coordinator) = Build();
    await Wait(coordinator.QuitAsync());

    Check(context.SaveCount == 1, "configuration was not saved");
    Check(context.ReleaseLockCount == 1, "single-instance lock was not released");
    Check(terminations[0] == 1, "terminate called " + terminations[0] + " times");
    Check(context.Calls.IndexOf("save") < context.Calls.IndexOf("release-lock"), "save must happen before releasing the lock");
    Check(context.Calls.IndexOf("release-lock") < context.Calls.IndexOf("terminate"), "lock must be released before termination");
});

await Case("models are unloaded before the process is terminated", async () =>
{
    var (context, _, coordinator) = Build();
    await Wait(coordinator.QuitAsync());

    Check(context.Calls.Contains("unload-ollama"), "Ollama models were never unloaded");
    Check(context.Calls.IndexOf("dispose-tts") < context.Calls.IndexOf("unload-ollama"), "TTS must be released before unloading models");
    Check(context.Calls.IndexOf("unload-ollama") < context.Calls.IndexOf("terminate"), "unload must finish before termination");
});

await Case("window close path cleans up without forcing a quit", async () =>
{
    var (context, terminations, coordinator) = Build();
    await Wait(coordinator.CleanupModelServicesAsync());

    Check(context.DisposeTtsCount == 1, "TTS not released");
    Check(context.Calls.Contains("unload-ollama"), "models not unloaded");
    Check(terminations[0] == 0, "cleanup alone must not terminate the process");
    Check(context.DisposeServicesCount == 0, "cleanup alone must not dispose services");
});

await Case("cleanup runs at most once across both exit entry points", async () =>
{
    var (context, _, coordinator) = Build();
    var gate = context.OllamaGate = Gate();

    var first = coordinator.CleanupModelServicesAsync();
    var second = coordinator.CleanupModelServicesAsync();
    Check(ReferenceEquals(first, second), "both callers must share the same task");

    gate.SetResult(true);
    await Wait(Task.WhenAll(first, second));
    await Wait(coordinator.CleanupModelServicesAsync());

    Check(context.DisposeTtsCount == 1, "TTS released " + context.DisposeTtsCount + " times");
    Check(context.Calls.Count(c => c == "unload-ollama") == 1, "models unloaded more than once");
});

await Case("quit waits for an in-flight cleanup", async () =>
{
    var (context, terminations, coordinator) = Build();
    var gate = context.OllamaGate = Gate();

    var cleanup = coordinator.CleanupModelServicesAsync();
    var quit = coordinator.QuitAsync();
    await Task.Delay(120);
    Check(terminations[0] == 0, "process terminated before cleanup finished");

    gate.SetResult(true);
    await Wait(Task.WhenAll(cleanup, quit));
    Check(terminations[0] == 1, "quit did not terminate after cleanup");
});

await Case("hanging Ollama unload cannot block exit", async () =>
{
    var (context, terminations, coordinator) = Build();
    context.OllamaGate = Gate();   // 永不完成

    var started = DateTime.UtcNow;
    await Wait(coordinator.ShutdownAsync(), 10000);
    var elapsed = DateTime.UtcNow - started;

    Check(terminations[0] == 1, "process was not terminated");
    Check(elapsed < ShutdownCoordinator.OllamaUnloadBudget + TimeSpan.FromSeconds(2),
        "exit took " + elapsed.TotalSeconds.ToString("F1") + "s, budget was not enforced");
});

await Case("hanging background release cannot block exit", async () =>
{
    var (context, terminations, coordinator) = Build();
    context.BackgroundGate = Gate();   // 永不完成

    var started = DateTime.UtcNow;
    await Wait(coordinator.QuitAsync(), 10000);
    var elapsed = DateTime.UtcNow - started;

    Check(terminations[0] == 1, "process was not terminated");
    Check(elapsed < ShutdownCoordinator.BackgroundReleaseBudget + TimeSpan.FromSeconds(2),
        "quit took " + elapsed.TotalSeconds.ToString("F1") + "s, budget was not enforced");
});

await Case("offline Ollama still allows a prompt exit", async () =>
{
    var (context, terminations, coordinator) = Build();
    context.OllamaError = new HttpRequestException("connection refused");

    var started = DateTime.UtcNow;
    await Wait(coordinator.ShutdownAsync());
    var elapsed = DateTime.UtcNow - started;

    Check(terminations[0] == 1, "offline Ollama blocked termination");
    Check(elapsed < TimeSpan.FromSeconds(2), "offline exit took " + elapsed.TotalSeconds.ToString("F1") + "s");
});

await Case("TTS disposal failure still unloads models", async () =>
{
    var (context, terminations, coordinator) = Build();
    context.DisposeTtsError = new InvalidOperationException("tts dispose exploded");

    await Wait(coordinator.ShutdownAsync());

    Check(context.Calls.Contains("unload-ollama"), "model unload was skipped after a TTS failure");
    Check(terminations[0] == 1, "process was not terminated");
});

await Case("services are disposed and typing stopped before termination", async () =>
{
    var (context, _, coordinator) = Build();
    await Wait(coordinator.ShutdownAsync());

    Check(context.StopTypingCount == 1, "typing animation was not stopped");
    Check(context.DisposeServicesCount == 1, "services were not disposed");
    Check(context.Calls.IndexOf("stop-typing") < context.Calls.IndexOf("dispose-services"), "typing must stop before disposing services");
    Check(context.Calls.IndexOf("dispose-services") < context.Calls.IndexOf("terminate"), "services must be disposed before termination");
});

await Case("the unload budget is passed to the context", async () =>
{
    var (context, _, coordinator) = Build();
    await Wait(coordinator.CleanupModelServicesAsync());
    Check(context.LastOllamaBudget == ShutdownCoordinator.OllamaUnloadBudget,
        "budget mismatch: " + context.LastOllamaBudget);
});

await Case("termination delegate is injectable and invoked once per shutdown", async () =>
{
    var context = new FakeShutdownContext();
    int calls = 0;
    var coordinator = new ShutdownCoordinator(context, () => calls++);

    await Wait(coordinator.ShutdownAsync());
    await Wait(coordinator.ShutdownAsync());

    Check(calls == 2, "each shutdown must invoke the injected delegate once, got " + calls);
});

Console.WriteLine($"exit regressions: {passed} passed, {failures.Count} failed; real process termination disabled");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
