using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Extracts the production WindowSizePolicy verbatim so the first-launch size decision can be
// asserted without WinUI. Regression target: a saved size that happens to equal the built-in
// default must still be honoured on a non-100% DPI display.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string sourcePath = Path.Combine(root, "MainWindow.xaml.cs");
var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath));
var policy = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
    .SingleOrDefault(c => c.Identifier.Text == "WindowSizePolicy")
    ?? throw new InvalidOperationException("Production extraction failed: WindowSizePolicy not found");

string host = """
using System;
""" + policy.ToFullString().Replace("internal static class WindowSizePolicy", "public static class WindowSizePolicy")
    .Replace("internal static (int Width, int Height) Resolve", "public static (int Width, int Height) Resolve");

string cases = """
using System;
public static class Cases {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run() {
        // 1. 首次运行（没有保存过）：按 DPI 把 DIP 默认值换算成物理像素
        var first = WindowSizePolicy.Resolve(false, 820, 560, 1.5, 820, 560);
        Check(first.Width == 1230 && first.Height == 840, "first launch at 150% -> " + first.Width + "x" + first.Height);

        // 2. 关键回归：用户保存的尺寸恰好等于默认值 820x560，仍必须原样使用。
        //    旧实现用 "尺寸 == 默认值" 判断“从未保存”，会把它重新换算成 1230x840。
        var savedDefault = WindowSizePolicy.Resolve(true, 820, 560, 1.5, 820, 560);
        Check(savedDefault.Width == 820 && savedDefault.Height == 560,
            "saved default size was rescaled -> " + savedDefault.Width + "x" + savedDefault.Height);

        // 3. 普通保存值原样使用（物理像素，不再乘 DPI）
        var saved = WindowSizePolicy.Resolve(true, 1000, 700, 2.0, 820, 560);
        Check(saved.Width == 1000 && saved.Height == 700, "saved size altered -> " + saved.Width + "x" + saved.Height);

        // 4. 保存值过小（分辨率变更/配置损坏）回退到 DPI 默认值
        var tiny = WindowSizePolicy.Resolve(true, 100, 100, 1.25, 820, 560);
        Check(tiny.Width == 1025 && tiny.Height == 700, "tiny saved size -> " + tiny.Width + "x" + tiny.Height);

        // 5. 只有一边过小时，另一边仍沿用保存值
        var mixed = WindowSizePolicy.Resolve(true, 1200, 100, 1.0, 820, 560);
        Check(mixed.Width == 1200 && mixed.Height == 560, "mixed -> " + mixed.Width + "x" + mixed.Height);

        // 6. DPI 查询失败（0）时按 100% 处理，不能除零或算出 0 尺寸
        var noDpi = WindowSizePolicy.Resolve(false, 820, 560, 0, 820, 560);
        Check(noDpi.Width == 820 && noDpi.Height == 560, "zero dpi -> " + noDpi.Width + "x" + noDpi.Height);

        Console.WriteLine("PASS window size policy (6 cases)");
    }
}
""";

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("WindowSizeProductionRegression",
    [CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(cases)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null);
