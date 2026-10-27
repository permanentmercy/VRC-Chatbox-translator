using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Compile selected, unmodified production members with deterministic service doubles.
// No WinUI app, audio device, model download, native inference, or NuGet package is used.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Select(string relativePath, params string[] names)
{
    var wanted = names.ToHashSet(StringComparer.Ordinal);
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, relativePath)));
    var members = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
        .Where(c => c.Identifier.Text is "SettingsService" or "SpeechService")
        .SelectMany(c => c.Members).Where(m => m switch
        {
            MethodDeclarationSyntax method => wanted.Contains(method.Identifier.Text),
            PropertyDeclarationSyntax property => wanted.Contains(property.Identifier.Text),
            FieldDeclarationSyntax field => field.Declaration.Variables.Any(v => wanted.Contains(v.Identifier.Text)),
            _ => false
        }).ToArray();
    if (members.Length != wanted.Count)
        throw new InvalidOperationException($"Production extraction failed: {relativePath}: expected {wanted.Count}, got {members.Length}");
    return string.Join(Environment.NewLine, members.Select(m => m.ToFullString()));
}

string settingsMembers = Select("Services/Settings/SettingsService.cs", "IsSpeechRecognitionEnabled", "SpeechEngine") +
    Select("Services/Settings/SettingsService.Speech.cs", "_speechStateLock", "_speechStateVersion", "_speechStateApplying",
        "OnSpeechRunningStateChanged", "NotifySpeechRecognitionState", "ApplySpeechStateAsync");
string modelMembers = Select("Services/Speech/SpeechService.Model.cs", "SwitchLanguageAsync", "EnsureProcessorAsync", "NormalizeLanguageTag", "GetLanguageDisplayName");
string source = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
public static class App { public static Dispatcher? DispatcherQueueInstance; }
public class Dispatcher {
    public bool HasThreadAccess {get; set;}
    public List<Action> Pending = new();
    public void TryEnqueue(Action action) => Pending.Add(action);
    public void Drain() { foreach (var action in Pending.ToArray()) action(); Pending.Clear(); }
}
public class ConfigData {
    public bool IsSpeechRecognitionEnabled;
    public string SpeechEngine = "Whisper";
    public bool LiveCaptionsHideNativeWindow = true;
}
public class FakeEngine {
    public bool IsRunning {get; private set;}
    public bool IsListening => IsRunning;
    public int StartCalls;
    public TaskCompletionSource<bool>? StartGate;
    public TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<bool>? Changed;
    public Task<bool> StartAsync(bool hide) => StartCore();
    public Task<bool> StartAsync(string language, string device) => StartCore();
    private async Task<bool> StartCore() {
        StartCalls++;
        var gate = StartGate;
        StartGate = null;
        Entered.TrySetResult(true);
        bool result = gate == null || await gate.Task;
        IsRunning = result;
        Changed?.Invoke(result);
        return result;
    }
    public Task StopAsync() { IsRunning = false; Changed?.Invoke(false); return Task.CompletedTask; }
    public void FailInBackground() { IsRunning = false; Changed?.Invoke(false); }
}
public class SettingsService {
    public ConfigData Config = new();
    public FakeEngine SpeechService = new();
    public FakeEngine LiveCaptionsService = new();
    public string SpeechLanguageTag = "en";
    public string AudioInputDeviceId = "test";
    public string SpeechStatus = "";
    public event Action<bool>? SpeechRecognitionStateChanged;
    public event Action? DisplaySettingsChanged;
    public event Action<string>? SpeechStatusUpdated;
    public SettingsService() {
        SpeechService.Changed += b => OnSpeechRunningStateChanged("Whisper", b);
        LiveCaptionsService.Changed += b => OnSpeechRunningStateChanged("LiveCaptions", b);
    }
    private void SaveConfigDebounced() { }
    private void UpdateLanguageVariable() { }
""" + settingsMembers + """
}
public class WhisperFactoryOptions { public bool UseGpu {get; set;} }
public static class RuntimeOptions { public static string LoadedLibrary => "fake"; }
public class WhisperProcessor : IDisposable {
    public string Language;
    public WhisperProcessor(string lang) { Language = lang; }
    public void Dispose() { }
}
public class WhisperFactory : IDisposable {
    public static string BlockLanguage = "ja";
    public static TaskCompletionSource<bool> BuildEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static ManualResetEventSlim BuildRelease = new(false);
    public static WhisperFactory FromPath(string path, WhisperFactoryOptions? options = null) => new();
    public Builder CreateBuilder() => new();
    public void Dispose() { }
    public class Builder {
        string language = "auto";
        public Builder WithLanguage(string lang) { language = lang; return this; }
        public WhisperProcessor Build() {
            if (language == BlockLanguage) {
                BuildEntered.TrySetResult(true);
                if (!BuildRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Build gate timed out");
            }
            return new(language);
        }
    }
}
public class SpeechService {
    private WhisperFactory? _factory = new();
    private WhisperProcessor? _processor = new("en");
    private string _currentLanguageTag = "en";
    private string _currentModelType = "tiny";
    private SemaphoreSlim _modelSwitchLock = new(1,1);
    private SemaphoreSlim _transcribeLock = new(1,1);
    public string CurrentLanguageName {get; private set;} = "English";
    public string Language => _currentLanguageTag;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    private void EnsureCudaEnvironment() { }
    private Task<string> EnsureModelPathAsync(string model) => Task.FromResult("fake");
    public void Cold() { _factory = null; _processor = null; }
    public Task Ensure(string lang) => EnsureProcessorAsync(lang);
""" + modelMembers + """
}
public static class Cases {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task CloseDuringStartup(string engineName) {
        var settings = new SettingsService();
        settings.Config.SpeechEngine = engineName;
        var engine = engineName == "Whisper" ? settings.SpeechService : settings.LiveCaptionsService;
        var gate = engine.StartGate = Gate();
        settings.IsSpeechRecognitionEnabled = true;
        var start = settings.ApplySpeechStateAsync();
        await engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.IsSpeechRecognitionEnabled = false;
        var stop = settings.ApplySpeechStateAsync();
        gate.SetResult(true);
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Check(!settings.IsSpeechRecognitionEnabled && !engine.IsRunning, engineName + " resurrected after close");
        Check(engine.StartCalls == 1, "Close request started an engine again");
        Console.WriteLine("PASS close during " + engineName + " startup");
    }
    static async Task FailedStartup(string engineName) {
        var settings = new SettingsService();
        settings.Config.SpeechEngine = engineName;
        var engine = engineName == "Whisper" ? settings.SpeechService : settings.LiveCaptionsService;
        var gate = engine.StartGate = Gate();
        settings.IsSpeechRecognitionEnabled = true;
        var apply = settings.ApplySpeechStateAsync();
        gate.SetResult(false);
        await apply;
        Check(!settings.IsSpeechRecognitionEnabled && !engine.IsRunning, "Failed startup left enabled");
        Console.WriteLine("PASS " + engineName + " startup failure rollback");
    }
    static async Task NewEnableSurvivesOldFailure() {
        var settings = new SettingsService();
        var gate = settings.SpeechService.StartGate = Gate();
        settings.IsSpeechRecognitionEnabled = true;
        var apply = settings.ApplySpeechStateAsync();
        await settings.SpeechService.Entered.Task;
        settings.IsSpeechRecognitionEnabled = false;
        settings.IsSpeechRecognitionEnabled = true;
        gate.SetResult(false);
        await apply;
        Check(settings.IsSpeechRecognitionEnabled && settings.SpeechService.IsRunning, "Stale failure overwrote new enable");
        Console.WriteLine("PASS stale startup failure preserves new enable");
    }
    static async Task SwitchEngineDuringStartup() {
        var settings = new SettingsService();
        var gate = settings.SpeechService.StartGate = Gate();
        settings.IsSpeechRecognitionEnabled = true;
        var first = settings.ApplySpeechStateAsync();
        await settings.SpeechService.Entered.Task;
        settings.SpeechEngine = "LiveCaptions";
        gate.SetResult(true);
        await first;
        await settings.ApplySpeechStateAsync();
        Check(settings.IsSpeechRecognitionEnabled && settings.LiveCaptionsService.IsRunning && !settings.SpeechService.IsRunning,
            "Stale engine remained running");
        settings.LiveCaptionsService.FailInBackground();
        Check(!settings.IsSpeechRecognitionEnabled, "Background failure left enabled");
        Console.WriteLine("PASS engine switch during startup and background failure");
    }
    static async Task DelayedUiNotification() {
        var dispatcher = App.DispatcherQueueInstance = new Dispatcher();
        var settings = new SettingsService();
        var notifications = new List<bool>();
        settings.SpeechRecognitionStateChanged += notifications.Add;
        settings.IsSpeechRecognitionEnabled = true;
        await settings.ApplySpeechStateAsync();
        settings.IsSpeechRecognitionEnabled = false;
        await settings.ApplySpeechStateAsync();
        dispatcher.Drain();
        Check(notifications.Count >= 2 && notifications.All(value => !value), "Stale queued UI notification reopened switch");
        App.DispatcherQueueInstance = null;
        Console.WriteLine("PASS delayed UI notifications use latest intent");
    }
    static async Task LanguageRoundTrip(bool cold, bool ensure) {
        WhisperFactory.BuildEntered = Gate();
        WhisperFactory.BuildRelease = new(false);
        var speech = new SpeechService();
        if (cold) speech.Cold();
        var first = ensure ? speech.Ensure("ja") : speech.SwitchLanguageAsync("ja");
        await WhisperFactory.BuildEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = ensure ? speech.Ensure("en") : speech.SwitchLanguageAsync("en");
        bool queued = !second.IsCompleted;
        WhisperFactory.BuildRelease.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Check(queued && speech.Language == "en", "A-B-A last request was dropped");
        Console.WriteLine("PASS language A-B-A " + (cold ? "cold" : "warm") + (ensure ? " EnsureProcessor" : " SwitchLanguage"));
    }
    public static async Task Run() {
        await CloseDuringStartup("Whisper");
        await CloseDuringStartup("LiveCaptions");
        await FailedStartup("Whisper");
        await FailedStartup("LiveCaptions");
        await NewEnableSurvivesOldFailure();
        await SwitchEngineDuringStartup();
        await LanguageRoundTrip(false, false);
        await LanguageRoundTrip(true, false);
        await LanguageRoundTrip(false, true);
    }
}
""";
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("SpeechProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
await (Task)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
Console.WriteLine("PASS all 9 deterministic production-member regression cases");
