using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// SpeechService 的模型/语言切换回归。
// 引擎启停状态机已抽成 SpeechEngineCoordinator 并有独立的真实类测试
// （tests/EngineCoordinatorRegression），这里只覆盖仍需从生产源码抽取的
// Whisper 处理器构建逻辑（依赖 Whisper.net 与原生库，无法直接实例化）。
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

string modelMembers = Select("Services/Speech/SpeechService.Model.cs",
    "SwitchLanguageAsync", "EnsureProcessorAsync", "NormalizeLanguageTag", "GetLanguageDisplayName");

string source = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

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

    // A-B-A：语言切换在构建闸门阻塞期间又收到反向请求，最后一次必须生效。
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
        await LanguageRoundTrip(false, false);
        await LanguageRoundTrip(true, false);
        await LanguageRoundTrip(false, true);
        Console.WriteLine("PASS all 3 deterministic speech-model regression cases");
    }
}
""";

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("SpeechModelProductionRegression",
    [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null);
