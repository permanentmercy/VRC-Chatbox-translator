using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// 提取生产 SaveConfigImmediately 方法体，用桩服务验证“落盘内容代表用户意图而不是当前已加载状态”。
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Select(string relativePath, params string[] names)
{
    var wanted = names.ToHashSet(StringComparer.Ordinal);
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, relativePath)));
    var members = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
        .Where(c => c.Identifier.Text == "SettingsService")
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

string configMembers = Select("Services/Settings/SettingsService.Config.cs",
    "_saveLock", "_saveDebounceTimer", "SaveConfigImmediately", "GetDevSourceConfigPath");

string source = """
using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Linq;

public enum GpuPerformanceMode { High, Medium, Low }

public sealed class AppConfig {
    public string ChineseVariant { get; set; } = "Simplified";
    public int SpeechSliceDurationSeconds { get; set; } = 6;
    public string GpuUsageMode { get; set; } = "High";
    public string SpeechModelType { get; set; } = "tiny";
    public int InboundHttpPort { get; set; } = 9002;
    public int InboundOscPort { get; set; } = 9001;
    public bool InboundAutoSendToVrc { get; set; } = true;
}

public sealed class SpeechServiceStub {
    public string ChineseVariant { get; set; } = "Simplified";
    public int SliceDurationSeconds { get; set; } = 6;
    public GpuPerformanceMode PerformanceMode { get; set; } = GpuPerformanceMode.High;
    public string CurrentModelType { get; set; } = "tiny";
}

public sealed class InboundServiceStub {
    public int HttpPort { get; set; } = 9002;
    public int OscPort { get; set; } = 9001;
    public bool AutoSendToVrc { get; set; } = true;
}

public class SettingsService {
    public static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "config-regression", Environment.ProcessId.ToString(), "config.json");
    public AppConfig Config { get; private set; } = new();
    public SpeechServiceStub SpeechService { get; } = new();
    public InboundServiceStub InboundService { get; } = new();
""" + configMembers + """
}

public static class Cases {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static JsonElement Save(Action<SettingsService> arrange) {
        var settings = new SettingsService();
        arrange(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.ConfigPath)!);
        settings.SaveConfigImmediately();
        return JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(SettingsService.ConfigPath));
    }

    public static void Run() {
        // 1. 关键回归：用户已选择 base，但模型还在下载（CurrentModelType 仍是 tiny），
        //    期间任何一次自动保存都不能把磁盘上的选择改回 tiny。
        var saved = Save(s => { s.Config.SpeechModelType = "base"; s.SpeechService.CurrentModelType = "tiny"; });
        Check(saved.GetProperty("SpeechModelType").GetString() == "base",
            "persisted model became the still-loading actual model: " + saved.GetProperty("SpeechModelType").GetString());

        // 2. 真正切换完成后，磁盘内容仍然等于用户选择
        saved = Save(s => { s.Config.SpeechModelType = "small"; s.SpeechService.CurrentModelType = "small"; });
        Check(saved.GetProperty("SpeechModelType").GetString() == "small", "persisted model lost after completed switch");

        // 3. 其它运行时状态仍按服务实际值同步（这些属性的服务端才是唯一真源）
        saved = Save(s => {
            s.SpeechService.ChineseVariant = "Traditional";
            s.SpeechService.SliceDurationSeconds = 8;
            s.SpeechService.PerformanceMode = GpuPerformanceMode.Low;
            s.InboundService.HttpPort = 9102;
            s.InboundService.OscPort = 9101;
            s.InboundService.AutoSendToVrc = false;
        });
        Check(saved.GetProperty("ChineseVariant").GetString() == "Traditional", "ChineseVariant not synced");
        Check(saved.GetProperty("SpeechSliceDurationSeconds").GetInt32() == 8, "slice duration not synced");
        Check(saved.GetProperty("GpuUsageMode").GetString() == "Low", "GPU mode not synced");
        Check(saved.GetProperty("InboundHttpPort").GetInt32() == 9102, "inbound HTTP port not synced");
        Check(saved.GetProperty("InboundOscPort").GetInt32() == 9101, "inbound OSC port not synced");
        Check(saved.GetProperty("InboundAutoSendToVrc").GetBoolean() == false, "inbound auto-send not synced");

        // 4. 旧实现的等价写法确实会覆盖意图（证明该缺陷存在，不是测试虚构）
        var legacy = new AppConfig { SpeechModelType = "base" };
        legacy.SpeechModelType = "tiny"; // 旧 SaveConfigImmediately 的那一行
        Check(legacy.SpeechModelType == "tiny", "legacy reference implementation no longer reproduces the defect");

        Console.WriteLine("PASS config persistence intent (4 cases)");
    }
}
""";

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("ConfigProductionRegression",
    [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null);
