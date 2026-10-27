using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// SAFETY: real termination is never compiled or invoked. The baseline's two terminal
// calls are redirected to a recorder. The fixed version uses its Action injection seam.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var lifecycle = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Services/Settings/SettingsService.Lifecycle.cs")));
var names = new HashSet<string> {
    "QuitApplicationAsync", "ShutdownAndKillProcess", "CleanupModelServicesForExitAsync",
    "CleanupModelServicesForExitCoreAsync", "_exitModelCleanupLock", "_exitModelCleanupTask",
    "_exitOllamaUnloadTimeout"
};
var selected = lifecycle.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
    .Single(c => c.Identifier.Text == "SettingsService").Members.Where(m => m switch {
        MethodDeclarationSyntax x => names.Contains(x.Identifier.Text),
        FieldDeclarationSyntax x => x.Declaration.Variables.Any(v => names.Contains(v.Identifier.Text)),
        _ => false
    }).ToArray();
var safeMembers = selected.Select(m => new SafeTermination().Visit(m)!).ToArray();
if (safeMembers.SelectMany(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>()).Any(x =>
    x.Expression.ToString().Contains(".Kill") || x.Expression.ToString().Contains("Environment.Exit")))
    throw new Exception("Safety check rejected executable process termination");
bool hasCleanup = selected.OfType<MethodDeclarationSyntax>().Any(x => x.Identifier.Text == "CleanupModelServicesForExitAsync");
bool hasInjectedShutdown = selected.OfType<MethodDeclarationSyntax>().Any(x => x.Identifier.Text == "ShutdownAndKillProcess" && x.ParameterList.Parameters.Count == 1);
var closed = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs")))
    .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.Text == "MainWindow_Closed")
    .WithReturnType(SyntaxFactory.ParseTypeName("Task"))
    .WithIdentifier(SyntaxFactory.Identifier("ClosedAsync"))
    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.AsyncKeyword)));
string source = """
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace VrcChatboxDemo.Services;
public static class TestTermination {
    public static List<string> Events = new();
    public static void Record() { lock (Events) Events.Add("terminate"); }
}
public static class App { public static void ReleaseSingleInstanceMutex() { } }
public class FakeService {
    public int Disposals;
    public bool ThrowOnDispose;
    public event Action? HotkeyPressed;
    public void Dispose() { Interlocked.Increment(ref Disposals); if (ThrowOnDispose) throw new Exception("dispose failure"); }
    public Task StopAsync() => Task.CompletedTask;
    public void StopLoop() { }
    public Task SendTypingAsync(bool value, bool recordLog) => Task.CompletedTask;
    public Task ClearChatboxAsync() => Task.CompletedTask;
}
public partial class SettingsService {
    public static SettingsService Instance = null!;
    public FakeService TtsService = new();
    public FakeService PersistentTextService = new();
    public FakeService SpeechService = new();
    public FakeService LiveCaptionsService = new();
    public FakeService InboundService = new();
    public FakeService HotkeyService = new();
    public FakeService VrcInGameGuard = new();
    public FakeService OscService = new();
    public OllamaService OllamaService = new();
    public string OllamaEndpoint = "http://fake.invalid";
    public void SaveConfigImmediately() { }
    public void SuppressTypingAnimation() { }
    private static void TerminateCurrentProcess() => TestTermination.Record();
""" + string.Join(Environment.NewLine, safeMembers.Select(m => m.ToFullString())) +
    (hasCleanup ? "" : "public Task CleanupModelServicesForExitAsync() => Task.CompletedTask;\n") +
    (hasInjectedShutdown ? "" : "internal Task ShutdownAndKillProcess(Action terminate) => Task.FromException(new Exception(\"Missing injectable termination seam\"));\n") + """
    public static SettingsService Create(Handler handler) {
        var settings = Instance = new SettingsService();
        TestTermination.Events = new();
        var client = typeof(OllamaService).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)client.GetValue(settings.OllamaService)!).Dispose();
        client.SetValue(settings.OllamaService, new HttpClient(handler));
        typeof(SettingsService).GetField("_exitOllamaUnloadTimeout", BindingFlags.Instance | BindingFlags.NonPublic)?
            .SetValue(settings, TimeSpan.FromMilliseconds(80));
        return settings;
    }
}
public sealed class WindowEventArgs { }
public sealed class Tray { public void RemoveTrayIcon() { } }
public sealed class WindowDouble {
    readonly Tray _trayIconService = new();
    private void OnHotkeyPressed() { }
""" + closed.NormalizeWhitespace().ToFullString() + """
}
public sealed class Handler : HttpMessageHandler {
    public int PsCalls, GenerateCalls;
    public bool Unreachable, Stall, StallUnload, IgnoreCancellation;
    public CancellationToken RequestToken;
    public TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        if (request.RequestUri!.AbsolutePath == "/api/ps") {
            Interlocked.Increment(ref PsCalls);
            TestTermination.Events.Add("ps");
            if (Unreachable) throw new HttpRequestException("offline");
            RequestToken = token;
            Entered.TrySetResult(true);
            if (Stall) {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(token);
            }
            return Json("{\"models\":[{\"name\":\"one\"},{\"name\":\"two\"}]}");
        }
        if (request.RequestUri.AbsolutePath == "/api/generate") {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (doc.RootElement.GetProperty("keep_alive").GetInt32() != 0) throw new Exception("Unload missing keep_alive=0");
            string name = doc.RootElement.GetProperty("model").GetString()!;
            Interlocked.Increment(ref GenerateCalls);
            TestTermination.Events.Add("unload:" + name);
            if (StallUnload) {
                RequestToken = token;
                await Release.Task.WaitAsync(token);
            }
            return Json("{}");
        }
        throw new Exception("Unexpected URL " + request.RequestUri);
    }
    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
public static class Cases {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Wait(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(2));
    static void AssertUnloaded(Handler handler) {
        Check(handler.PsCalls == 1 && handler.GenerateCalls == 2,
            $"Expected one ps/two unloads, got {handler.PsCalls}/{handler.GenerateCalls}");
    }
    static async Task TrayExit() {
        using var handler = new Handler(); var settings = SettingsService.Create(handler);
        await Wait(settings.QuitApplicationAsync()); AssertUnloaded(handler);
        Check(TestTermination.Events.SequenceEqual(new[] { "ps", "unload:one", "unload:two", "terminate" }),
            "Termination did not follow completed unloads");
    }
    static async Task WindowCloseOnly() {
        using var handler = new Handler(); var settings = SettingsService.Create(handler);
        await Wait(new WindowDouble().ClosedAsync(new object(), new WindowEventArgs()));
        AssertUnloaded(handler);
        Check(!TestTermination.Events.Contains("terminate"), "Window close unexpectedly terminated process");
    }
    static async Task SharedCleanup() {
        using var handler = new Handler(); var settings = SettingsService.Create(handler);
        var window = new WindowDouble().ClosedAsync(new object(), new WindowEventArgs());
        var tray = settings.QuitApplicationAsync();
        await Wait(Task.WhenAll(window, tray)); AssertUnloaded(handler);
        Check(settings.TtsService.Disposals == 1, "Window/tray duplicated TTS cleanup");
        await Wait(settings.CleanupModelServicesForExitAsync()); AssertUnloaded(handler);
    }
    static async Task RepeatedCleanup() {
        using var handler = new Handler(); var settings = SettingsService.Create(handler);
        var first = settings.CleanupModelServicesForExitAsync();
        var second = settings.CleanupModelServicesForExitAsync();
        Check(ReferenceEquals(first, second), "Concurrent cleanup did not share the same task");
        await Wait(Task.WhenAll(first, second)); AssertUnloaded(handler);
        Check(settings.TtsService.Disposals == 1, "Cleanup was not idempotent");
    }
    static async Task AwaitUnloadBeforeTermination() {
        using var handler = new Handler { Stall = true }; var settings = SettingsService.Create(handler);
        var exit = settings.ShutdownAndKillProcess();
        try {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromMilliseconds(400));
            Check(!exit.IsCompleted && !TestTermination.Events.Contains("terminate"), "Exit did not await pending unload");
            handler.Release.TrySetResult(true); await Wait(exit); AssertUnloaded(handler);
            Check(TestTermination.Events.Last() == "terminate", "Terminated before unload");
        } finally { handler.Release.TrySetResult(true); await Wait(exit); }
    }
    static async Task BoundedExit(bool ignoreCancellation) {
        using var handler = new Handler { Stall = true, IgnoreCancellation = ignoreCancellation };
        var settings = SettingsService.Create(handler); var watch = Stopwatch.StartNew();
        var exit = settings.ShutdownAndKillProcess();
        try {
            await exit.WaitAsync(TimeSpan.FromMilliseconds(700));
            Check(handler.PsCalls == 1, "Exit never attempted Ollama cleanup");
            Check(handler.RequestToken.IsCancellationRequested, "Exit budget did not cancel Ollama operation");
            Check(TestTermination.Events.Last() == "terminate", "Bounded cleanup prevented termination");
            Check(watch.ElapsedMilliseconds < 650, "Exit was not bounded");
        } finally { handler.Release.TrySetResult(true); await Wait(exit); }
    }
    static async Task UnloadSharesTotalBudget() {
        using var handler = new Handler { StallUnload = true }; var settings = SettingsService.Create(handler);
        var exit = settings.ShutdownAndKillProcess();
        try {
            await exit.WaitAsync(TimeSpan.FromMilliseconds(700));
            Check(handler.PsCalls == 1 && handler.GenerateCalls == 1, "Total budget did not stop remaining model unloads");
            Check(handler.RequestToken.IsCancellationRequested, "Total budget did not cancel model unload");
            Check(TestTermination.Events.Last() == "terminate", "Stalled model unload prevented termination");
        } finally { handler.Release.TrySetResult(true); await Wait(exit); }
    }
    static async Task OfflineExit() {
        using var handler = new Handler { Unreachable = true }; var settings = SettingsService.Create(handler);
        await Wait(settings.ShutdownAndKillProcess());
        Check(handler.PsCalls == 1 && handler.GenerateCalls == 0, "Offline cleanup was skipped or fabricated unload");
        Check(TestTermination.Events.Last() == "terminate", "Offline service blocked termination");
    }
    static async Task FailingTtsCleanup() {
        using var handler = new Handler(); var settings = SettingsService.Create(handler);
        settings.TtsService.ThrowOnDispose = true;
        await Wait(settings.ShutdownAndKillProcess()); AssertUnloaded(handler);
        Check(TestTermination.Events.Last() == "terminate", "TTS cleanup failure blocked termination");
    }
    static async Task InjectableTerminator() {
        using var handler = new Handler(); var settings = SettingsService.Create(handler);
        int calls = 0;
        await Wait(settings.ShutdownAndKillProcess(() => { calls++; TestTermination.Record(); }));
        AssertUnloaded(handler); Check(calls == 1, "Injected terminator was not invoked exactly once");
    }
    public static async Task<int> Run() {
        var tests = new (string Name, Func<Task> Run)[] {
            ("tray unloads resident models before termination", TrayExit),
            ("window close unloads without force termination", WindowCloseOnly),
            ("window and tray share idempotent cleanup", SharedCleanup),
            ("repeated cleanup returns shared task", RepeatedCleanup),
            ("termination waits for pending Ollama request", AwaitUnloadBeforeTermination),
            ("stalled Ollama exit is bounded and cancelled", () => BoundedExit(false)),
            ("uncooperative Ollama exit is still bounded", () => BoundedExit(true)),
            ("query and model unload share total budget", UnloadSharesTotalBudget),
            ("offline Ollama does not block exit", OfflineExit),
            ("TTS disposal failure still unloads Ollama", FailingTtsCleanup),
            ("termination delegate is injectable", InjectableTerminator)
        };
        int failures = 0;
        foreach (var test in tests) {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} exit regressions passed; real process termination disabled");
        return failures == 0 ? 0 : 1;
    }
}
""";
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("ExitRegression.Dynamic", new[] {
    CSharpSyntaxTree.ParseText(source),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Services/Translation/OllamaService.cs")))
}, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
using var output = new MemoryStream();
var emitted = compilation.Emit(output);
if (!emitted.Success) {
    foreach (var error in emitted.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)) Console.Error.WriteLine(error);
    return 2;
}
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("VrcChatboxDemo.Services.Cases")!.GetMethod("Run")!.Invoke(null, null)!;

sealed class SafeTermination : CSharpSyntaxRewriter {
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
        string expression = node.Expression.ToString();
        if (expression is "System.Diagnostics.Process.GetCurrentProcess().Kill" or "Environment.Exit")
            return SyntaxFactory.ParseExpression("TestTermination.Record()").WithTriviaFrom(node);
        return base.VisitInvocationExpression(node);
    }
}
