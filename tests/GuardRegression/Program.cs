using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// 只读提取生产源码里的前台判定状态机，配 Win32 无关的确定性驱动。
// 回归目标：中文输入法组合窗口接管前台期间，避让不能被提前判定为“离开 VRChat”。
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string sourcePath = Path.Combine(root, "Services/Template/VrcInGameGuard.cs");
var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath));
var tracker = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
    .SingleOrDefault(c => c.Identifier.Text == "VrcForegroundTracker")
    ?? throw new InvalidOperationException("Production extraction failed: VrcForegroundTracker not found");

string source = """
using System;
using VrcChatboxDemo.Services;
public static class Cases {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run() {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 1. VRChat 直接在前台
        var tracker = new VrcForegroundTracker();
        Check(tracker.Observe("VRChat", t0), "VRChat foreground not recognised");

        // 2. 输入法组合窗口紧随其后接管前台
        Check(tracker.Observe("TextInputHost", t0.AddSeconds(1)), "IME host right after VRChat not associated");
        Check(tracker.Observe("ctfmon", t0.AddSeconds(2)), "ctfmon right after VRChat not associated");

        // 3. 关键回归：连续打中文 20 秒（每 0.5 秒一次键），避让必须全程有效。
        var typing = new VrcForegroundTracker();
        Check(typing.Observe("VRChat", t0), "VRChat foreground not recognised");
        for (int i = 1; i <= 40; i++) {
            var now = t0.AddMilliseconds(500 * i);
            Check(typing.Observe("TextInputHost", now), "IME typing lost VRChat association after " + (0.5 * i) + "s");
        }

        // 4. VRChat 早已退到后台，输入法窗口不应再被算作游戏内输入
        var stale = new VrcForegroundTracker();
        Check(stale.Observe("VRChat", t0), "VRChat foreground not recognised");
        Check(!stale.Observe("TextInputHost", t0.AddSeconds(10)), "stale IME host still treated as VRChat");

        // 5. 无关程序前台
        var other = new VrcForegroundTracker();
        Check(!other.Observe("chrome", t0), "unrelated process treated as VRChat");
        Check(!other.Observe(null!, t0), "null process name treated as VRChat");

        // 6. 进程名大小写不敏感
        var lower = new VrcForegroundTracker();
        Check(lower.Observe("vrchat", t0), "case-insensitive VRChat match broken");
        Check(lower.Observe("textinputhost", t0.AddSeconds(1)), "case-insensitive IME match broken");

        // 7. 文档化旧缺陷：旧实现只在“VRChat 直接前台”时刷新时间戳，
        //    因此同一串中文输入到第 4 秒就掉出 3 秒关联窗口。
        var legacy = new LegacyTracker();
        Check(legacy.Observe("VRChat", t0), "legacy: VRChat foreground not recognised");
        bool survived = true;
        for (int i = 1; i <= 40; i++) {
            if (!legacy.Observe("TextInputHost", t0.AddMilliseconds(500 * i))) { survived = false; break; }
        }
        Check(!survived, "legacy reference implementation unexpectedly passed; regression premise is stale");

        Console.WriteLine("PASS foreground association matrix (7 cases)");
    }
}

// 修复前的等价实现，仅用于证明该缺陷确实存在（不参与生产编译）。
public sealed class LegacyTracker {
    private DateTime _lastVrcForegroundTime = DateTime.MinValue;
    public bool Observe(string procName, DateTime now) {
        if (procName != null && procName.Contains("VRChat", StringComparison.OrdinalIgnoreCase)) {
            _lastVrcForegroundTime = now;
            return true;
        }
        if ((now - _lastVrcForegroundTime).TotalSeconds < 3.0 &&
            procName != null &&
            (procName.Contains("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
             procName.Contains("ctfmon", StringComparison.OrdinalIgnoreCase))) {
            return true;
        }
        return false;
    }
}
""";

string trackerSource = tracker.ToFullString()
    .Replace("internal sealed class VrcForegroundTracker", "public sealed class VrcForegroundTracker");

string host = """
using System;
namespace VrcChatboxDemo.Services {
""" + string.Join(Environment.NewLine, trackerSource.Split('\n').Select(l => "    " + l)) + """
}
""";

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("GuardProductionRegression",
    [CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null);
