using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NAudio.Wave;

// Compile whole production classes unchanged. Only the native recorder/builder and
// Whisper handles are replaced by deterministic doubles; WaveFormat types are real NAudio.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Class(string file, string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)))
    .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == name).ToFullString();
string source = """
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
public enum AudioClientBufferFlags { None }
public enum ProcessLoopbackMode { IncludeTargetProcessTree }
public delegate void CaptureData(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long position, long qpc);
public sealed class WasapiRecorder : IDisposable {
    public WaveFormat WaveFormat {get; set;} = new(48000,16,2);
    public event CaptureData? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public bool Disposed;
    public bool Started;
    public bool ThrowOnStart;
    public int Starts;
    public void StartRecording() {
        if (Disposed) throw new ObjectDisposedException("recorder");
        if (ThrowOnStart) throw new InvalidOperationException("injected start failure");
        Started = true; Starts++;
    }
    public void StopRecording() { Started = false; }
    public void Dispose() { Disposed = true; Started = false; }
}
public sealed class WasapiRecorderBuilder {
    public static Queue<Task<WasapiRecorder>> Builds = new();
    public static int BuildCalls;
    public WasapiRecorderBuilder WithProcessLoopback(uint pid, ProcessLoopbackMode mode) => this;
    public WasapiRecorderBuilder WithLoopbackCapture() => this;
    public Task<WasapiRecorder> BuildAsync() { Interlocked.Increment(ref BuildCalls); return Builds.Dequeue(); }
    public static void Reset(params Task<WasapiRecorder>[] tasks) { Builds = new(tasks); BuildCalls = 0; }
}
public static class File { public static bool Exists(string path) => true; } // no model files or disk writes
public static class SpeechService { public static void EnsureCudaEnvironment() { } }
public record EarLanguageResult(string Code, string DisplayName, float Confidence);
public sealed class WhisperFactoryOptions { public bool UseGpu {get;set;} }
public sealed class WhisperFactory : IDisposable {
    public static List<WhisperFactory> Created = new();
    public static ManualResetEventSlim? BuildGate;
    public static TaskCompletionSource<bool> BuildEntered = Gate();
    public static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed;
    public WhisperProcessor? Processor;
    public static WhisperFactory FromPath(string path, WhisperFactoryOptions? options = null) {
        var f = new WhisperFactory(); Created.Add(f); return f;
    }
    public Builder CreateBuilder() => new(this);
    public sealed class Builder(WhisperFactory owner) {
        public Builder WithLanguageDetection() => this;
        public WhisperProcessor Build() {
            BuildEntered.TrySetResult(true);
            if (BuildGate != null && !BuildGate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("build gate");
            return owner.Processor = new WhisperProcessor();
        }
    }
    public void Dispose() { Disposed = true; }
}
public sealed class WhisperProcessor : IDisposable {
    public ManualResetEventSlim? DetectGate;
    public TaskCompletionSource<bool> DetectEntered = WhisperFactory.Gate();
    public TaskCompletionSource<bool> DisposedSignal = WhisperFactory.Gate();
    public bool Disposed, Active, DisposedWhileActive;
    public (string,float) DetectLanguageWithProbability(float[] samples) {
        Active = true;
        DetectEntered.TrySetResult(true);
        try {
            if (DetectGate != null && !DetectGate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("detect gate");
            if (Disposed) throw new ObjectDisposedException("processor");
            return ("en", 0.99f);
        } finally { Active = false; }
    }
    public void Dispose() { DisposedWhileActive |= Active; Disposed = true; DisposedSignal.TrySetResult(true); }
}
""" + Class("Services/Speech/ProcessLoopbackCapture.cs", "ProcessLoopbackCapture") +
    Class("Services/Speech/EarLanguageDetector.cs", "EarLanguageDetector") + """
public static class Cases {
    static int total, failures;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
    static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    static ProcessLoopbackCapture Capture(WasapiRecorder recorder) => (ProcessLoopbackCapture)typeof(ProcessLoopbackCapture)
        .GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single().Invoke([Process.GetCurrentProcess().ProcessName, recorder, false, 0]);
    static bool Compatible(WaveFormat a, WaveFormat b) => (bool)typeof(ProcessLoopbackCapture)
        .GetMethod("AreWaveFormatsCompatible",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[a,b])!;
    static WaveFormat Ext(bool floating, int valid = 32, int mask = 0) => new WaveFormatExtensible(48000,32,2,
        floating ? AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT : AudioMediaSubtypes.MEDIASUBTYPE_PCM,valid,mask);
    static async Task Case(string name, Func<Task> run) {
        total++;
        try { await run(); Console.WriteLine("PASS " + name); }
        catch(Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static Task FormatCase(WaveFormat a, WaveFormat b, bool expected) {
        Check(Compatible(a,b) == expected, "incompatible native interpretation accepted (or valid format rejected)");
        return Task.CompletedTask;
    }
    static async Task PendingSwitch(bool dispose) {
        var old = new WasapiRecorder(); var next = new WasapiRecorder();
        var gate = new TaskCompletionSource<WasapiRecorder>(TaskCreationOptions.RunContinuationsAsynchronously);
        WasapiRecorderBuilder.Reset(gate.Task);
        var capture = Capture(old); capture.StartRecording();
        var switching = capture.TrySwitchToProcessAsync();
        Check(WasapiRecorderBuilder.BuildCalls == 1,"switch never entered builder");
        if(dispose) capture.Dispose(); else capture.StopRecording();
        gate.SetResult(next);
        bool result = await switching.WaitAsync(Timeout);
        try {
            Check(!result, "late native recorder was installed after " + (dispose ? "Dispose" : "Stop"));
            Check(next.Disposed,"unpublished native recorder leaked");
            Check(!next.Started,"late recorder started after stop/dispose");
        } finally { capture.Dispose(); }
    }
    static async Task ConcurrentSwitch() {
        var first = new WasapiRecorder(); var second = new WasapiRecorder();
        var gate = new TaskCompletionSource<WasapiRecorder>(TaskCreationOptions.RunContinuationsAsynchronously);
        WasapiRecorderBuilder.Reset(gate.Task,Task.FromResult(second));
        using var capture = Capture(new WasapiRecorder()); capture.StartRecording();
        var a = capture.TrySwitchToProcessAsync(); var b = capture.TrySwitchToProcessAsync();
        int enteredBeforeRelease = WasapiRecorderBuilder.BuildCalls;
        gate.SetResult(first);
        await Task.WhenAll(a,b).WaitAsync(Timeout);
        Check(enteredBeforeRelease == 1 && WasapiRecorderBuilder.BuildCalls == 1,
            "watcher and UI reconnect built overlapping native recorders");
        Check(first.Starts == 1 && !second.Started,"recorder was started more than once");
    }
    static async Task QueuedSwitchStopped() {
        var late = new WasapiRecorder(); var unused = new WasapiRecorder();
        var gate = new TaskCompletionSource<WasapiRecorder>(TaskCreationOptions.RunContinuationsAsynchronously);
        WasapiRecorderBuilder.Reset(gate.Task,Task.FromResult(unused));
        using var capture = Capture(new WasapiRecorder()); capture.StartRecording();
        var first = capture.TrySwitchToProcessAsync(); var queued = capture.TrySwitchToProcessAsync();
        capture.StopRecording(); gate.SetResult(late);
        var results = await Task.WhenAll(first,queued).WaitAsync(Timeout);
        Check(results.All(r => !r) && WasapiRecorderBuilder.BuildCalls == 1,
            "queued request survived Stop and built another recorder");
        Check(late.Disposed && !unused.Started,"stopped recorder candidate was not cleaned up");
    }
    static async Task SuccessfulSwitch() {
        var old = new WasapiRecorder(); var next = new WasapiRecorder();
        WasapiRecorderBuilder.Reset(Task.FromResult(next));
        var capture = Capture(old); capture.StartRecording();
        Check(await capture.TrySwitchToProcessAsync(),"compatible candidate did not connect");
        Check(old.Disposed && next.Started && capture.IsProcessFound,"ownership was not transferred");
        capture.Dispose(); capture.Dispose();
        Check(next.Disposed && !capture.IsProcessFound,"published recorder was not disposed");
        Check(!await capture.TrySwitchToProcessAsync(),"disposed capture accepted reconnect");
        try { capture.StartRecording(); throw new Exception("disposed capture restarted"); }
        catch(ObjectDisposedException) { }
    }
    static async Task IncompatibleSwitch() {
        var old = new WasapiRecorder {WaveFormat=Ext(false)};
        var next = new WasapiRecorder {WaveFormat=Ext(true)};
        WasapiRecorderBuilder.Reset(Task.FromResult(next));
        using var capture = Capture(old); capture.StartRecording();
        Check(!await capture.TrySwitchToProcessAsync(),"float recorder published into PCM chain");
        Check(next.Disposed && !old.Disposed && old.Started,"mismatched candidate damaged fallback or leaked");
    }
    static async Task StartFailureRollback() {
        var old = new WasapiRecorder(); var bad = new WasapiRecorder {ThrowOnStart=true};
        WasapiRecorderBuilder.Reset(Task.FromResult(bad));
        using var capture = Capture(old); capture.StartRecording();
        Check(!await capture.TrySwitchToProcessAsync(),"start failure incorrectly reported success");
        Check(bad.Disposed,"failed candidate leaked");
        Check(!old.Disposed && old.Started,"working fallback destroyed before replacement could start");
        Check(!capture.IsProcessFound,"failed process binding published as connected");
    }
    static async Task DisposeDuringDetection() {
        var proc = new WhisperProcessor {DetectGate=new(false)};
        var factory = new WhisperFactory();
        var detector = new EarLanguageDetector();
        Set(detector,"_isInitialized",true); Set(detector,"_processor",proc); Set(detector,"_factory",factory);
        var samples = Enumerable.Repeat(0.5f,16000).ToArray();
        var detection = Task.Run(() => detector.DetectFromAudioAsync(samples));
        await proc.DetectEntered.Task.WaitAsync(Timeout);
        detector.Dispose();
        bool freedWhileActive = proc.Disposed || factory.Disposed;
        proc.DetectGate.Set();
        await detection.WaitAsync(Timeout);
        await proc.DisposedSignal.Task.WaitAsync(Timeout);
        Check(!freedWhileActive && !proc.DisposedWhileActive,"native handles freed during active inference");
        Check(!detector.IsInitialized,"disposed detector stayed initialized");
    }
    static async Task DisposeDuringInitialization() {
        WhisperFactory.Created.Clear(); WhisperFactory.BuildEntered=WhisperFactory.Gate(); WhisperFactory.BuildGate=new(false);
        var detector = new EarLanguageDetector();
        var initializing = detector.InitializeAsync();
        await WhisperFactory.BuildEntered.Task.WaitAsync(Timeout);
        detector.Dispose(); WhisperFactory.BuildGate.Set();
        bool initialized = await initializing.WaitAsync(Timeout);
        try {
            Check(!initialized && !detector.IsInitialized,"model loading resurrected a disposed detector");
            Check(WhisperFactory.Created.All(f => f.Disposed && f.Processor?.Disposed == true),"late-built model handles leaked");
        } finally { detector.Dispose(); WhisperFactory.BuildGate=null; }
    }
    static async Task InitializeAfterDispose() {
        WhisperFactory.Created.Clear();
        var detector = new EarLanguageDetector(); detector.Dispose();
        bool initialized = await detector.InitializeAsync();
        try { Check(!initialized && WhisperFactory.Created.Count == 0,"disposed instance loaded a new native model"); }
        finally { detector.Dispose(); }
    }
    public static async Task<int> Run() {
        await Case("Extensible PCM32 versus float32",()=>FormatCase(Ext(false),Ext(true),false));
        await Case("Extensible valid-bit mismatch",()=>FormatCase(Ext(false,24),Ext(false,32),false));
        await Case("Extensible channel layout mismatch",()=>FormatCase(Ext(false,32,3),Ext(false,32,12),false));
        await Case("identical Extensible formats",()=>FormatCase(Ext(false,24,3),Ext(false,24,3),true));
        await Case("ordinary identical PCM",()=>FormatCase(new WaveFormat(48000,16,2),new WaveFormat(48000,16,2),true));
        await Case("sample rate mismatch",()=>FormatCase(new WaveFormat(48000,16,2),new WaveFormat(44100,16,2),false));
        await Case("Dispose invalidates in-flight recorder build",()=>PendingSwitch(true));
        await Case("Stop invalidates in-flight recorder build",()=>PendingSwitch(false));
        await Case("watcher and manual switch are serialized",ConcurrentSwitch);
        await Case("Stop invalidates queued switches too",QueuedSwitchStopped);
        await Case("successful recorder ownership and repeated Dispose",SuccessfulSwitch);
        await Case("incompatible Extensible candidate is discarded",IncompatibleSwitch);
        await Case("candidate start failure preserves working fallback",StartFailureRollback);
        await Case("Ear Dispose never frees active inference",DisposeDuringDetection);
        await Case("Ear loading cannot resurrect after Dispose",DisposeDuringInitialization);
        await Case("Ear Initialize after Dispose is rejected",InitializeAfterDispose);
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Append(typeof(WaveFormat).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("SpeechDeepProductionRegression",[CSharpSyntaxTree.ParseText(source)],references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine,result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null,null)!;
