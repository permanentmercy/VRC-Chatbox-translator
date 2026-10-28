using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Production members are compiled unchanged against fake NAudio and HTTP. All
// process-start/kill members are excluded and replaced by a rejecting safety stub.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string tests = Path.Combine(root, "tests/TtsDeepRegression");
var serviceTrees = Directory.GetFiles(Path.Combine(root, "Services/Tts"), "TtsService*.cs")
    .OrderBy(path => path, StringComparer.Ordinal)
    .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)))
    .ToArray();
var methods = new HashSet<string> {
    "SetState", "IsModelRegistered", "GetPlaybackDevices", "IsVirtualAudioDevice",
    "CheckHealthAsync", "CheckHealthCoreAsync", "RefreshHealthAsync", "StopManagedServer", "BeginSynthesis",
    "IsCurrentSynthesis", "TrySetSynthesisState", "ReadStreamBodyAsync", "SynthesizeStreamAsync",
    "FindFirstVirtualAudioDevice", "ResolveDevices", "DisposeDevices", "PlayStreamAsync",
    "MatchDeviceChannelCount", "BeginPlayback", "EndPlayback", "StopPlayback", "Dispose"
};
// TtsService 现在是分部类：设备部分在 TtsService.Devices.cs，成员需要跨文件合并。
var members = serviceTrees
    .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
    .Where(c => c.Identifier.Text == "TtsService")
    .SelectMany(c => c.Members)
    .Where(m => m switch {
        FieldDeclarationSyntax or EventFieldDeclarationSyntax or PropertyDeclarationSyntax or ConstructorDeclarationSyntax => true,
        MethodDeclarationSyntax x => methods.Contains(x.Identifier.Text) &&
            (x.Identifier.Text != "PlayStreamAsync" || x.ParameterList.Parameters[0].Type!.ToString() == "string"),
        _ => false
    });
string production = """
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace VrcChatboxDemo.Services.Tts;
public sealed class TtsService {
    private static void StopOwnedProcess(Process? process) {
        if (process != null) throw new Exception("SAFETY: process operations forbidden in regression");
    }
    private string? ResolveRequestModelAlias() => null;
    private static string? ResolveRequestPrompt() => null;
""" + string.Join(Environment.NewLine, members.Select(m => m.ToFullString())) + """
    public static TtsService Create(HttpMessageHandler handler) {
        var instance = new TtsService();
        instance._httpClient.Dispose();
        typeof(TtsService).GetField("_httpClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(instance, new HttpClient(handler));
        return instance;
    }
    public static (MMDevice? Mic, MMDevice? Monitor, bool Same) Resolve(string? mic, string? monitor, bool enableMonitor) {
        using var enumerator = new MMDeviceEnumerator();
        return ResolveDevices(enumerator, mic, monitor, enableMonitor);
    }
    public static void Release(MMDevice? mic, MMDevice? monitor) => DisposeDevices(mic, monitor);
    public void BeginFakeSynthesis() => BeginSynthesis("fake active synthesis", default);
    public void CloseClient() => _httpClient.Dispose();
    public int QueueCount => _playbackQueueLock.CurrentCount;
}
""" + string.Join(Environment.NewLine, CSharpSyntaxTree
    .ParseText(File.ReadAllText(Path.Combine(root, "Services/Synthesis/PlaybackProviders.cs")))
    .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
    .Where(x => x.Identifier.Text is "SoftLimiterSampleProvider" or "SpeechQueueWaveProvider")
    .Select(x => x.ToFullString()));
var trees = new[] {
    CSharpSyntaxTree.ParseText(production),
    CSharpSyntaxTree.ParseText("using System.Collections.Generic;\n" + File.ReadAllText(Path.Combine(root, "Services/Tts/TtsModels.cs"))),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Services/Translation/OllamaService.cs"))),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(tests, "Fakes.cs"))),
    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(tests, "Cases.cs")))
};
foreach (var tree in trees) {
    if (tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Any(x =>
        x.Expression.ToString().Contains(".Kill") || x.Expression.ToString().Contains("Environment.Exit") ||
        x.Expression.ToString() is "proc.Start" or "Process.Start" or "System.Diagnostics.Process.Start"))
        throw new Exception("SAFETY: real process invocation in dynamic regression source");
}
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("TtsDeepRegression.Dynamic", trees, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) {
    foreach (var error in result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)) Console.Error.WriteLine(error);
    return 2;
}
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("VrcChatboxDemo.Services.Tts.Cases")!.GetMethod("Run")!.Invoke(null, null)!;
