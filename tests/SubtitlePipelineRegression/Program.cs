using VrcChatboxDemo.Services.Speech;

// SubtitlePipeline 回归：字幕队列裁剪、假说归一化、中途译文拼接、行数上限。
// 这些规则原先埋在 619 行的 SettingsService.Speech.cs 里，只能靠读代码确认。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Case(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

SubtitlePipeline Make(int capacity = 3) => new(() => capacity);

Case("final sentence appears in the display", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("你好");
    pipeline.UpdateDisplay();
    Check(pipeline.LastRecognizedText == "你好", "got '" + pipeline.LastRecognizedText + "'");
});

Case("history is trimmed to the configured capacity", () =>
{
    var pipeline = Make(3);
    foreach (string line in new[] { "one", "two", "three", "four", "five" }) pipeline.AddFinalSentence(line);
    pipeline.UpdateDisplay();
    Check(pipeline.LastRecognizedText == "three\nfour\nfive", "got '" + pipeline.LastRecognizedText.Replace("\n", "|") + "'");
});

Case("capacity change takes effect on the next render", () =>
{
    int capacity = 3;
    var pipeline = new SubtitlePipeline(() => capacity);
    foreach (string line in new[] { "a", "b", "c", "d" }) pipeline.AddFinalSentence(line);
    pipeline.UpdateDisplay();
    Check(pipeline.LastRecognizedText == "b\nc\nd", "capacity 3 -> " + pipeline.LastRecognizedText.Replace("\n", "|"));

    capacity = 2;
    pipeline.UpdateDisplay();
    Check(pipeline.LastRecognizedText == "c\nd", "capacity 2 -> " + pipeline.LastRecognizedText.Replace("\n", "|"));
});

Case("hypothesis is appended with an ellipsis marker", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("finished sentence.");
    pipeline.UpdateDisplay("partial words");
    Check(pipeline.LastRecognizedText == "finished sentence.\npartial words ...",
        "got '" + pipeline.LastRecognizedText.Replace("\n", "|") + "'");
});

Case("hypothesis keeps only the text after the last finished sentence", () =>
{
    var pipeline = Make();
    pipeline.UpdateDisplay("已经说完。还在说");
    Check(pipeline.LastRecognizedText == "还在说 ...", "got '" + pipeline.LastRecognizedText + "'");
});

Case("hypothesis plus interim translation renders both lines", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("第一句。");
    pipeline.InterimTranslation = "first sentence.";
    pipeline.UpdateDisplay("第二句");
    Check(pipeline.LastRecognizedText == "第一句。\n第二句 ...", "original: " + pipeline.LastRecognizedText.Replace("\n", "|"));
    // 定稿句此时还没有译文，所以译文行只有假说的中途翻译
    Check(pipeline.LastTranslatedText == "first sentence. ...",
        "translated: " + pipeline.LastTranslatedText.Replace("\n", "|"));
});

Case("final translation is applied to the matching sentence", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("hello");
    pipeline.AddFinalSentence("world");
    pipeline.ApplyFinalTranslation("world", "世界", 1234);
    pipeline.UpdateDisplay();
    Check(pipeline.LastTranslatedText == "世界", "got '" + pipeline.LastTranslatedText + "'");
    Check(pipeline.LastTranslationLatencyMs == 1234, "latency=" + pipeline.LastTranslationLatencyMs);
});

Case("duplicate sentences translate the most recent occurrence", () =>
{
    var pipeline = Make(5);
    pipeline.AddFinalSentence("same");
    pipeline.AddFinalSentence("other");
    pipeline.AddFinalSentence("same");
    pipeline.ApplyFinalTranslation("same", "相同", 10);
    pipeline.UpdateDisplay();
    Check(pipeline.LastTranslatedText == "相同", "only the newest duplicate should carry the translation: "
        + pipeline.LastTranslatedText.Replace("\n", "|"));
});

Case("translation for an unknown sentence is ignored", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("known");
    pipeline.ApplyFinalTranslation("unknown", "x", 99);
    pipeline.UpdateDisplay();
    Check(pipeline.LastTranslatedText == string.Empty, "unexpected translation: " + pipeline.LastTranslatedText);
});

Case("display never exceeds capacity even with a hypothesis", () =>
{
    var pipeline = Make(2);
    foreach (string line in new[] { "a", "b", "c" }) pipeline.AddFinalSentence(line);
    pipeline.UpdateDisplay("tail");
    string[] lines = pipeline.LastRecognizedText.Split('\n');
    Check(lines.Length <= 2, "lines=" + lines.Length);
    Check(lines[^1] == "tail ...", "hypothesis must stay last: " + pipeline.LastRecognizedText.Replace("\n", "|"));
});

Case("clear resets text, entries and interim translation", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("hello");
    pipeline.InterimTranslation = "translated";
    pipeline.UpdateDisplay("tail");

    pipeline.Clear();
    pipeline.UpdateDisplay();

    Check(pipeline.LastRecognizedText == string.Empty, "recognized text survived clear");
    Check(pipeline.LastTranslatedText == string.Empty, "translated text survived clear");
    Check(pipeline.LastTranslationLatencyMs == 0, "latency survived clear");
    Check(pipeline.InterimTranslation == null, "interim translation survived clear");
});

Case("blank sentences are ignored", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("");
    pipeline.AddFinalSentence("   ");
    pipeline.AddFinalSentence(null);
    pipeline.UpdateDisplay();
    Check(pipeline.LastRecognizedText == string.Empty, "blank sentence leaked: '" + pipeline.LastRecognizedText + "'");
});

Case("events distinguish final text from hypothesis", () =>
{
    var pipeline = Make();
    var recognized = new List<string>();
    var hypotheses = new List<string>();
    var translations = new List<(string Text, long Latency)>();
    int displayChanges = 0;
    pipeline.RecognizedUpdated += t => recognized.Add(t);
    pipeline.HypothesisUpdated += t => hypotheses.Add(t);
    pipeline.TranslationUpdated += (t, ms) => translations.Add((t, ms));
    pipeline.DisplayChanged += () => displayChanges++;

    pipeline.AddFinalSentence("done.");
    pipeline.UpdateDisplay();
    pipeline.UpdateDisplay("still talking");

    Check(recognized.Count == 1 && recognized[0] == "done.", "recognized events: " + recognized.Count);
    Check(hypotheses.Count == 1 && hypotheses[0].EndsWith("..."), "hypothesis events: " + hypotheses.Count);
    Check(translations.Count == 2, "translation events: " + translations.Count);
    Check(displayChanges == 2, "display events: " + displayChanges);
});

Case("sentence cut index ignores decimal points", () =>
{
    Check(SubtitlePipeline.FindLastSentenceCutIndex("3.14 is pi") == -1, "decimal point treated as a sentence end");
    Check(SubtitlePipeline.FindLastSentenceCutIndex("a. b") == 1, "ascii terminator");
    Check(SubtitlePipeline.FindLastSentenceCutIndex("你好。再见") == 2, "cjk terminator");
    Check(SubtitlePipeline.FindLastSentenceCutIndex("really?!") == 7, "punctuation run");
    Check(SubtitlePipeline.FindLastSentenceCutIndex("") == -1, "empty");
});

Case("newlines inside a sentence are flattened", () =>
{
    var pipeline = Make();
    pipeline.AddFinalSentence("line1\nline2");
    pipeline.UpdateDisplay();
    Check(!pipeline.LastRecognizedText.Contains('\n') || pipeline.LastRecognizedText == "line1 line2",
        "got '" + pipeline.LastRecognizedText + "'");
    Check(pipeline.LastRecognizedText == "line1 line2", "got '" + pipeline.LastRecognizedText + "'");
});

Console.WriteLine($"subtitle pipeline regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
