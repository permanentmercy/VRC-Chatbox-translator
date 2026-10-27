using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Compile current production text-consumption members in memory. UI Automation and the
// Windows process lifecycle are excluded, not reimplemented; no native captions are started.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string path = Path.Combine(root, "Services/Speech/LiveCaptionsService.cs");
var production = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
    .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "LiveCaptionsService");
string[] platformFields = ["_hWnd", "_isRunning", "_isRestarting", "_workerCts", "_sessionLock"];
string[] platformMethods = ["StartAsync", "StopAsync", "StopCore", "RestartAsync", "PollingLoopAsync", "Dispose"];
var members = production.Members.Where(m => m switch
{
    FieldDeclarationSyntax field => !field.Declaration.Variables.Any(v => platformFields.Contains(v.Identifier.Text)),
    MethodDeclarationSyntax method => !platformMethods.Contains(method.Identifier.Text),
    _ => false
});
if (!members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "ExtractNewText"))
    throw new InvalidOperationException("Production extraction failed");
string source = """
using System;
using System.Collections.Generic;
using System.Text;
public sealed class LiveCaptionsService {
    public event Action<string>? SpeechRecognized;
    public event Action<string>? SpeechHypothesis;
    public event Action<string>? StatusChanged;
""" + string.Join(Environment.NewLine, members.Select(m => m.ToFullString())) + """
    public string Frame(string text) {
        text = CleanCaptionText(text);
        _lastFullText = text; // Same order as the polling loop: UI frame first, extraction second.
        return ExtractNewText(text).Trim();
    }
    public void Commit(string sentence) => RecordCommittedSentence(sentence);
    public void Baseline(string text) {
        text = CleanCaptionText(text);
        _lastFullText = text;
        AlignInitialBaseline(text);
    }
}
public static class Cases {
    static int failures, total;
    static void Equal(string expected, string actual) {
        if (actual != expected) throw new Exception("expected [" + expected + "] actual [" + actual + "]");
    }
    static void Case(string name, Action run) {
        total++;
        try { run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static LiveCaptionsService Committed(string sentence) {
        var service = new LiveCaptionsService();
        Equal(sentence, service.Frame(sentence));
        service.Commit(sentence);
        return service;
    }
    public static int Run() {
        Case("CJK repeated sentence is new speech", () => {
            var s = Committed("你好。");
            Equal("你好。", s.Frame("你好。你好。"));
        });
        Case("English repeated sentence is new speech", () => {
            var s = Committed("Yes.");
            Equal("Yes.", s.Frame("Yes. Yes."));
        });
        Case("three identical sentences advance once each within one frame", () => {
            var s = new LiveCaptionsService();
            const string frame = "你好。你好。你好。";
            Equal(frame, s.Frame(frame));
            s.Commit("你好。"); Equal("你好。你好。", s.Frame(frame));
            s.Commit("你好。"); Equal("你好。", s.Frame(frame));
            s.Commit("你好。"); Equal("", s.Frame(frame));
        });
        Case("repeated English text does not hide intervening new sentence", () => {
            var s = Committed("Yes.");
            Equal("No. Yes.", s.Frame("Yes. No. Yes."));
        });
        Case("CJK scrolling relocates consumed occurrence not newest repeat", () => {
            var s = new LiveCaptionsService();
            s.Frame("历史。你好。"); s.Commit("历史。"); s.Commit("你好。");
            Equal("你好。", s.Frame("你好。你好。"));
        });
        Case("English scrolling relocates consumed occurrence not newest repeat", () => {
            var s = new LiveCaptionsService();
            s.Frame("History. Hello."); s.Commit("History."); s.Commit("Hello.");
            Equal("Hello.", s.Frame("Hello. Hello."));
        });
        Case("scrolling keeps uncommitted hypothesis", () => {
            var s = new LiveCaptionsService();
            s.Frame("历史。你好。下一"); s.Commit("历史。"); s.Commit("你好。");
            Equal("下一句。", s.Frame("你好。下一句。"));
        });
        Case("scroll with no surviving anchor resumes instead of remaining silent", () => {
            var s = new LiveCaptionsService();
            s.Frame("历史。你好。"); s.Commit("历史。"); s.Commit("你好。");
            Equal("新句。再来。", s.Frame("新句。再来。"));
        });
        Case("case and terminal punctuation correction does not replay committed speech", () => {
            var s = Committed("Hello.");
            Equal("Next.", s.Frame("HELLO! Next."));
        });
        Case("English ASR middle correction retains trailing anchor", () => {
            var s = Committed("I really like cats.");
            Equal("New sentence.", s.Frame("I truly like cats. New sentence."));
        });
        Case("CJK ASR insertion retains trailing anchor", () => {
            var s = Committed("我喜欢猫。");
            Equal("下一句。", s.Frame("我特别喜欢猫。下一句。"));
        });
        Case("unchanged polls never replay an already consumed repeat", () => {
            var s = new LiveCaptionsService();
            s.Frame("你好。你好。"); s.Commit("你好。"); s.Commit("你好。");
            for (int i = 0; i < 5; i++) Equal("", s.Frame("你好。你好。"));
        });
        Case("growing hypothesis is retained until explicitly committed", () => {
            var s = Committed("Hello.");
            Equal("I", s.Frame("Hello. I"));
            Equal("I like", s.Frame("Hello. I like"));
            Equal("I like cats.", s.Frame("Hello. I like cats."));
            s.Commit("I like cats."); Equal("", s.Frame("Hello. I like cats."));
        });
        Case("clear history consumes displayed hypothesis but allows later identical speech", () => {
            var s = new LiveCaptionsService();
            s.Frame("你好。"); s.ResetHistory();
            Equal("", s.Frame("你好。"));
            Equal("你好。", s.Frame("你好。你好。"));
        });
        Case("initial baseline emits only its last completed sentence and keeps partial tail", () => {
            var s = new LiveCaptionsService();
            var recognized = new List<string>(); s.SpeechRecognized += recognized.Add;
            s.Baseline("旧句。你好。尾巴");
            Equal("你好。", string.Join("|", recognized));
            Equal("尾巴", s.Frame("旧句。你好。尾巴"));
            Equal("尾巴你好。", s.Frame("旧句。你好。尾巴你好。"));
        });
        Case("initial baseline containing repeated history consumes each position", () => {
            var s = new LiveCaptionsService();
            var recognized = new List<string>(); s.SpeechRecognized += recognized.Add;
            s.Baseline("你好。你好。");
            Equal("你好。", string.Join("|", recognized));
            Equal("", s.Frame("你好。你好。"));
            Equal("你好。", s.Frame("你好。你好。你好。"));
        });
        Case("English initial incomplete baseline remains a hypothesis", () => {
            var s = new LiveCaptionsService();
            var recognized = new List<string>(); s.SpeechRecognized += recognized.Add;
            s.Baseline("Hello there");
            Equal("", string.Join("|", recognized));
            Equal("Hello there", s.Frame("Hello there"));
        });
        Case("punctuation length changes retain consumed position before repeat", () => {
            var s = Committed("你好。");
            Equal("你好！", s.Frame("你好？！ 你好！"));
        });
        Case("retracted hypothesis does not rewind consumed prefix", () => {
            var s = Committed("Hello.");
            Equal("next", s.Frame("Hello. next"));
            Equal("", s.Frame("Hello"));
            Equal("Hello.", s.Frame("Hello! Hello."));
        });
        Case("ASR contraction of committed content never replays a shortened prefix", () => {
            var s = Committed("Hello there.");
            Equal("", s.Frame("Hello"));
            Equal("Hello.", s.Frame("Hello! Hello."));
        });
        Case("without history a complete fresh frame is available", () => {
            var s = new LiveCaptionsService();
            Equal("新的字幕。", s.Frame("新的字幕。"));
        });
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("CaptionProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return (int)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
