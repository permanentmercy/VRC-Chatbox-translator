using VrcChatboxDemo.Services.Text;

// ChatTextNormalizer 回归：这些规则原先散落在 SettingsService 三处且正则被复制多次。
// 直接编译生产文件，纯函数断言，无服务/无 UI/无网络。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Case(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

Case("explicit newline tokens all expand", () =>
{
    Check(ChatTextNormalizer.ExpandExplicitNewlines(@"a{\n}b") == "a\nb", "brace form");
    Check(ChatTextNormalizer.ExpandExplicitNewlines("a{newline}b") == "a\nb", "named form");
    Check(ChatTextNormalizer.ExpandExplicitNewlines("a{换行}b") == "a\nb", "chinese form");
    Check(ChatTextNormalizer.ExpandExplicitNewlines(@"a\nb") == "a\nb", "bare form");
    Check(ChatTextNormalizer.ExpandExplicitNewlines("a{NEWLINE}b") == "a\nb", "case insensitive");
});

Case("plain text is untouched by newline expansion", () =>
{
    const string text = "hello world, no tokens here";
    Check(ChatTextNormalizer.ExpandExplicitNewlines(text) == text, "text changed");
    Check(ChatTextNormalizer.ExpandExplicitNewlines(null) == string.Empty, "null not handled");
    Check(ChatTextNormalizer.ExpandExplicitNewlines("") == string.Empty, "empty not handled");
});

Case("truncation respects the 144 char limit", () =>
{
    string exact = new('a', ChatTextNormalizer.MaxChatboxChars);
    Check(ChatTextNormalizer.TruncateForChatbox(exact) == exact, "exact length must pass through");
    Check(ChatTextNormalizer.MaxChatboxChars == 144, "limit changed");

    string tooLong = new('a', 200);
    Check(ChatTextNormalizer.TruncateForChatbox(tooLong).Length == 144, "not truncated to 144");
});

Case("truncation never splits a surrogate pair", () =>
{
    // 143 个 ASCII + emoji（代理对占 2 char）→ 第 144 个 char 是高代理项，必须回退
    string text = new string('a', 143) + "😀" + "tail";
    string cut = ChatTextNormalizer.TruncateForChatbox(text);
    Check(cut.Length == 143, "expected 143 chars, got " + cut.Length);
    Check(!char.IsHighSurrogate(cut[^1]), "result ends with a dangling high surrogate");
});

Case("prepare for chatbox combines expansion and truncation", () =>
{
    string input = @"line1{\n}" + new string('x', 200);
    string prepared = ChatTextNormalizer.PrepareForChatbox(input);
    Check(prepared.Length <= 144, "over limit: " + prepared.Length);
    Check(prepared.StartsWith("line1\n", StringComparison.Ordinal), "newline lost");
});

Case("tts cleaning strips rich text and control chars", () =>
{
    Check(ChatTextNormalizer.PrepareForTts("<color=#ff0000>hello</color>") == "hello.", "rich text: "
        + ChatTextNormalizer.PrepareForTts("<color=#ff0000>hello</color>"));
    string withControl = "a\u0001b\u0007c";
    Check(!ChatTextNormalizer.PrepareForTts(withControl).Contains('\u0001'), "control char kept");
});

Case("tts cleaning turns newlines into commas", () =>
{
    // 结尾是 ASCII 字母/数字时补 "."，与生产实现（以及原实现）一致
    Check(ChatTextNormalizer.PrepareForTts("line1\nline2") == "line1，line2.", "got " + ChatTextNormalizer.PrepareForTts("line1\nline2"));
    Check(ChatTextNormalizer.PrepareForTts("a\r\nb") == "a，b.", "crlf handling");
    Check(ChatTextNormalizer.PrepareForTts("第一行\n第二行") == "第一行，第二行。", "cjk line ends with full stop");
});

Case("tts cleaning adds the right terminator", () =>
{
    Check(ChatTextNormalizer.PrepareForTts("hello") == "hello.", "ascii should get a dot");
    Check(ChatTextNormalizer.PrepareForTts("你好") == "你好。", "cjk should get a full stop");
    Check(ChatTextNormalizer.PrepareForTts("done.") == "done.", "existing ascii terminator must be kept");
    Check(ChatTextNormalizer.PrepareForTts("好的！") == "好的！", "existing cjk terminator must be kept");
    Check(ChatTextNormalizer.PrepareForTts("42") == "42.", "digits should get a dot");
});

Case("tts cleaning handles blank input", () =>
{
    Check(ChatTextNormalizer.PrepareForTts("") == string.Empty, "empty");
    Check(ChatTextNormalizer.PrepareForTts("   ") == string.Empty, "whitespace only");
    Check(ChatTextNormalizer.PrepareForTts(null) == string.Empty, "null");
});

Case("space collapsing", () =>
{
    Check(ChatTextNormalizer.CollapseSpaces("a    b") == "a b", "collapse failed");
    Check(ChatTextNormalizer.CollapseSpaces(null) == string.Empty, "null");
});

Console.WriteLine($"text regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
