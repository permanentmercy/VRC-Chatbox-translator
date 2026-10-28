using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Compile unmodified production synthesis/lifecycle members, replacing only settings and HTTP.
// No WinUI, NAudio, Python server, real audio endpoint, or NuGet package is required.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
// TtsService 是分部类：成员分散在 TtsService*.cs，需要跨文件合并后再筛选。
var trees = Directory.GetFiles(Path.Combine(root, "Services/Tts"), "TtsService*.cs")
    .OrderBy(path => path, StringComparer.Ordinal)
    .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)))
    .ToArray();
var selected = new HashSet<string>(StringComparer.Ordinal) {
    "_httpClient", "_serverProcess", "_serverProcessLock", "_serverGeneration", "_disposeStarted",
    "_activePlaybackCts", "_playbackLock", "State", "StatusMessage", "StateChanged",
    "SynthesisCompleted", "LogOccurred", "SetState", "StopManagedServer", "StopOwnedProcess",
    "StopPlayback", "SynthesizeAsync", "SynthesizeStreamAsync", "_registeredModelAliases",
    // Fix-specific members are optional so this same harness runs against the broken baseline.
    "_synthesisSequence", "_streamReadTimeout", "BeginSynthesis", "TrySetSynthesisState",
    "IsCurrentSynthesis", "ReadStreamBodyAsync"
};
var members = trees
    .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
    .Where(c => c.Identifier.Text == "TtsService")
    .SelectMany(c => c.Members)
    .Where(m => m switch {
        ConstructorDeclarationSyntax => true,
        MethodDeclarationSyntax x => selected.Contains(x.Identifier.Text),
        PropertyDeclarationSyntax x => selected.Contains(x.Identifier.Text),
        FieldDeclarationSyntax x => x.Declaration.Variables.Any(v => selected.Contains(v.Identifier.Text)),
        EventFieldDeclarationSyntax x => x.Declaration.Variables.Any(v => selected.Contains(v.Identifier.Text)),
        _ => false
    });
string source = """
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace VrcChatboxDemo.Services.Tts;
public sealed class ConfigData {
    public string TtsServerEndpoint = "http://fake.invalid";
}
public sealed class SettingsService {
    public static SettingsService Instance = new();
    public ConfigData Config = new();
}
public sealed class TtsService {
    private string? ResolveRequestModelAlias() => null;
    private static string? ResolveRequestPrompt() => null;
""" + string.Join(Environment.NewLine, members.Select(m => m.ToFullString())) + """
    public static TtsService Create(HttpMessageHandler handler) {
        var service = new TtsService();
        service._httpClient.Dispose();
        typeof(TtsService).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });
        // The production readonly timeout is shortened only in this isolated test instance.
        typeof(TtsService).GetField("_streamReadTimeout", BindingFlags.Instance | BindingFlags.NonPublic)?
            .SetValue(service, TimeSpan.FromMilliseconds(80));
        return service;
    }
    public void CloseTestClient() => _httpClient.Dispose();
}
public sealed class Handler : HttpMessageHandler {
    public Func<CancellationToken, Task<HttpResponseMessage>> Send;
    public Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) => Send = send;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Send(token);
}
public sealed class StalledStream : Stream {
    public TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IgnoreCancellation;
    public bool Disposed;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
        Entered.TrySetResult(true);
        if (IgnoreCancellation) await Release.Task;
        else await Release.Task.WaitAsync(token);
        return 0;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    protected override void Dispose(bool disposing) { Disposed = true; Release.TrySetResult(true); base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
public sealed class PrefixThenStall : Stream {
    public readonly StalledStream Tail = new();
    bool sent;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
        if (sent) return Tail.ReadAsync(buffer, token);
        sent = true;
        buffer.Span[0] = 1; buffer.Span[1] = 2;
        return ValueTask.FromResult(2);
    }
    protected override void Dispose(bool disposing) { Tail.Dispose(); base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
public static class Cases {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static HttpResponseMessage Response(Stream body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StreamContent(body) };
    static async Task Wait(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(2));

    static async Task StopBeforeHeaders(bool failure, bool buffered = false, bool cancel = true) {
        var headers = Gate<HttpResponseMessage>();
        var service = TtsService.Create(new Handler(_ => headers.Task));
        int completions = 0;
        service.SynthesisCompleted += (_, _, _) => completions++;
        using var cts = new CancellationTokenSource();
        if (buffered) {
            var pending = service.SynthesizeAsync("test", cts.Token);
            service.StopManagedServer();
            if (failure) headers.SetException(new HttpRequestException("late failure"));
            else headers.SetResult(Response(new MemoryStream(new byte[] { 1, 2 })));
            await Wait(pending);
        } else {
            await using var enumerator = service.SynthesizeStreamAsync("test", cts.Token).GetAsyncEnumerator();
            var pending = enumerator.MoveNextAsync().AsTask();
            service.StopManagedServer();
            if (failure) headers.SetException(new HttpRequestException("late failure"));
            else if (cancel) { cts.Cancel(); headers.SetCanceled(cts.Token); }
            else headers.SetResult(Response(new MemoryStream(new byte[] { 1, 2 })));
            await Wait(pending);
            Check(!pending.Result, "Stopped request yielded stale PCM");
        }
        Check(service.State == TtsServerState.Stopped, "Late HTTP continuation replaced Stopped with " + service.State);
        Check(completions == 0, "Stopped request emitted synthesis completion");
        service.CloseTestClient();
    }
    static async Task StopDuringBody() {
        var body = new StalledStream { IgnoreCancellation = true };
        var service = TtsService.Create(new Handler(_ => Task.FromResult(Response(body))));
        using var cts = new CancellationTokenSource();
        await using var enumerator = service.SynthesizeStreamAsync("test", cts.Token).GetAsyncEnumerator();
        var pending = enumerator.MoveNextAsync().AsTask();
        await Wait(body.Entered.Task);
        service.StopManagedServer();
        cts.Cancel();
        body.Release.TrySetResult(true);
        await Wait(pending);
        Check(service.State == TtsServerState.Stopped, "Body cancellation resurrected engine: " + service.State);
        service.CloseTestClient();
    }
    static async Task OldEnumeratorCannotFinishNewRequest() {
        var newHeaders = Gate<HttpResponseMessage>();
        int count = 0;
        var service = TtsService.Create(new Handler(_ => ++count == 1
            ? Task.FromResult(Response(new MemoryStream(new byte[] { 1, 2 }))) : newHeaders.Task));
        var oldEnumerator = service.SynthesizeStreamAsync("old").GetAsyncEnumerator();
        Check(await oldEnumerator.MoveNextAsync(), "Expected first audio chunk");
        using var cts = new CancellationTokenSource();
        await using var newEnumerator = service.SynthesizeStreamAsync("new", cts.Token).GetAsyncEnumerator();
        var pending = newEnumerator.MoveNextAsync().AsTask();
        await oldEnumerator.DisposeAsync();
        bool preserved = service.State == TtsServerState.Synthesizing;
        cts.Cancel(); newHeaders.SetCanceled(cts.Token);
        await Wait(pending);
        service.CloseTestClient();
        Check(preserved, "Old iterator overwrote active request state");
    }
    static async Task BodyTimeout(bool prefix, bool ignoreCancellation = false, bool errorResponse = false) {
        var tail = new StalledStream { IgnoreCancellation = ignoreCancellation };
        var withPrefix = new PrefixThenStall();
        Stream body = prefix ? withPrefix : tail;
        var service = TtsService.Create(new Handler(_ => Task.FromResult(Response(body,
            errorResponse ? HttpStatusCode.BadRequest : HttpStatusCode.OK))));
        int completions = 0;
        service.SynthesisCompleted += (_, _, _) => completions++;
        using var cts = new CancellationTokenSource();
        await using var enumerator = service.SynthesizeStreamAsync("timeout", cts.Token).GetAsyncEnumerator();
        if (prefix) Check(await enumerator.MoveNextAsync(), "Expected prefix PCM chunk");
        var pending = enumerator.MoveNextAsync().AsTask();
        try {
            var winner = await Task.WhenAny(pending, Task.Delay(600));
            Check(winner == pending, "Stalled body exceeded injected timeout");
            try { await pending; throw new Exception("Timeout was silently treated as EOF/success"); }
            catch (TimeoutException) { }
            Check(service.State == TtsServerState.Error, "Timeout must leave Error, not " + service.State);
            Check(completions == 0, "Timeout emitted completion");
            Check((prefix ? withPrefix.Tail : tail).Disposed, "Timed out response body was not disposed");
        } finally {
            cts.Cancel(); tail.Release.TrySetResult(true); withPrefix.Tail.Release.TrySetResult(true);
            try { await pending; } catch (OperationCanceledException) { } catch (TimeoutException) { }
            service.CloseTestClient();
        }
    }
    static async Task SuccessAndEarlyDispose() {
        var service = TtsService.Create(new Handler(_ => Task.FromResult(Response(new MemoryStream(new byte[] { 1, 2, 3, 4 })))));
        int bytes = 0, completions = 0;
        service.SynthesisCompleted += (_, _, _) => completions++;
        await foreach (var chunk in service.SynthesizeStreamAsync("normal")) bytes += chunk.Length;
        Check(bytes == 4 && completions == 1 && service.State == TtsServerState.Ready, "Normal synthesis regressed");
        await using (var enumerator = service.SynthesizeStreamAsync("abandoned").GetAsyncEnumerator())
            Check(await enumerator.MoveNextAsync(), "Missing early chunk");
        Check(service.State == TtsServerState.Ready && completions == 1, "Early dispose did not reset state cleanly");
        service.CloseTestClient();
    }
    static async Task CallerCancellation() {
        var body = new StalledStream();
        var service = TtsService.Create(new Handler(_ => Task.FromResult(Response(body))));
        using var cts = new CancellationTokenSource();
        await using var enumerator = service.SynthesizeStreamAsync("cancel", cts.Token).GetAsyncEnumerator();
        var pending = enumerator.MoveNextAsync().AsTask();
        await Wait(body.Entered.Task); cts.Cancel(); await Wait(pending);
        Check(service.State == TtsServerState.Ready, "Caller cancellation misreported as error");
        service.CloseTestClient();
    }
    static async Task PreCancelledRequest() {
        int sends = 0, stateChanges = 0;
        var service = TtsService.Create(new Handler(_ => { sends++; return Task.FromResult(Response(new MemoryStream())); }));
        service.StateChanged += (_, _) => stateChanges++;
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await foreach (var _ in service.SynthesizeStreamAsync("cancelled", cts.Token)) { }
        var buffered = await service.SynthesizeAsync("cancelled", cts.Token);
        Check(!buffered.Success && sends == 0 && stateChanges == 0 && service.State == TtsServerState.Stopped,
            "Already cancelled request changed state or sent HTTP");
        service.CloseTestClient();
    }
    static async Task StopBeforeEarlyDispose() {
        var service = TtsService.Create(new Handler(_ => Task.FromResult(Response(new MemoryStream(new byte[] { 1, 2 })))));
        var enumerator = service.SynthesizeStreamAsync("abandoned").GetAsyncEnumerator();
        Check(await enumerator.MoveNextAsync(), "Missing PCM chunk");
        service.StopManagedServer();
        await enumerator.DisposeAsync();
        Check(service.State == TtsServerState.Stopped, "Dispose restored stopped synthesis");
        service.CloseTestClient();
    }
    public static async Task<int> Run() {
        var cases = new (string Name, Func<Task> Run)[] {
            ("stop before stream headers / cancellation", () => StopBeforeHeaders(false)),
            ("stop before stream headers / failure", () => StopBeforeHeaders(true)),
            ("stop before late successful stream headers", () => StopBeforeHeaders(false, false, false)),
            ("stop before buffered completion", () => StopBeforeHeaders(false, true)),
            ("stop before buffered failure", () => StopBeforeHeaders(true, true)),
            ("stop during stream body", StopDuringBody),
            ("old iterator cannot reset new request", OldEnumeratorCannotFinishNewRequest),
            ("stalled first PCM read times out", () => BodyTimeout(false)),
            ("stalled read after PCM prefix times out", () => BodyTimeout(true)),
            ("uncooperative stream still times out", () => BodyTimeout(false, true)),
            ("stalled HTTP error body times out", () => BodyTimeout(false, false, true)),
            ("normal synthesis and early dispose", SuccessAndEarlyDispose),
            ("caller cancellation is not timeout", CallerCancellation),
            ("pre-cancelled request never resurrects stopped service", PreCancelledRequest),
            ("stop before early iterator disposal", StopBeforeEarlyDispose)
        };
        int failures = 0;
        foreach (var test in cases) {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
        }
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} TTS regressions passed");
        return failures == 0 ? 0 : 1;
    }
}
""";
var models = "using System.Collections.Generic;\n" + File.ReadAllText(Path.Combine(root, "Services/Tts/TtsModels.cs"));
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("TtsRegression.Dynamic", new[] {
    CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(models)
}, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) {
    foreach (var diagnostic in result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)) Console.Error.WriteLine(diagnostic);
    return 2;
}
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("VrcChatboxDemo.Services.Tts.Cases")!.GetMethod("Run")!.Invoke(null, null)!;
