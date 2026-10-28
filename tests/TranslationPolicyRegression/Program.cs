using VrcChatboxDemo.Services.Translation;

// TranslationPolicy 回归：节流档位与目标语言归一化是纯规则，
// 原先埋在 681 行的 SettingsService.Speech.cs 里只能靠读代码确认。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Case(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

Case("default mode resolves to 0.8s / 3 chars", () =>
{
    var option = TranslationPolicy.ResolveFrequency(TranslationPolicy.DefaultMode);
    Check(option.IntervalMs == 800, "interval=" + option.IntervalMs);
    Check(option.MinNewChars == 3, "minNewChars=" + option.MinNewChars);
});

Case("mode lookup is case insensitive", () =>
{
    Check(TranslationPolicy.ResolveFrequency("off").IntervalMs == 0, "OFF not recognised");
    Check(TranslationPolicy.ResolveFrequency("ULTRA").IntervalMs == 200, "ULTRA not recognised");
    Check(TranslationPolicy.ResolveFrequency(" low ").IntervalMs == 2000, "trimmed lookup failed");
});

Case("off mode disables interim translation", () =>
{
    var option = TranslationPolicy.ResolveFrequency("Off");
    Check(option.IntervalMs <= 0, "off mode must have a non-positive interval");
});

Case("unknown or missing mode falls back to default", () =>
{
    foreach (string? mode in new[] { null, "", "   ", "nonsense" })
    {
        var option = TranslationPolicy.ResolveFrequency(mode);
        Check(option.Mode == TranslationPolicy.DefaultMode, "fallback for '" + mode + "' gave " + option.Mode);
        Check(option.IntervalMs == 800, "fallback interval=" + option.IntervalMs);
    }
});

Case("frequency table is ordered from slowest to fastest", () =>
{
    var options = TranslationPolicy.FrequencyOptions;
    Check(options.Count == 5, "expected 5 options, got " + options.Count);
    Check(options[0].Mode == "Off", "first option should be Off");
    Check(options[0].IntervalMs <= 0, "Off must use a non-positive interval sentinel");

    // 跳过 Off（它是"关闭"哨兵，不是最慢的间隔），其余必须严格递增频率
    for (int i = 2; i < options.Count; i++)
    {
        Check(options[i].IntervalMs < options[i - 1].IntervalMs,
            "option " + i + " (" + options[i].Mode + ") is not faster than " + options[i - 1].Mode);
    }
});

Case("every option has a non-empty label", () =>
{
    foreach (var option in TranslationPolicy.FrequencyOptions)
    {
        Check(!string.IsNullOrWhiteSpace(option.Label), "empty label for " + option.Mode);
        Check(!string.IsNullOrWhiteSpace(option.Mode), "empty mode name");
    }
});

Case("target language names normalise", () =>
{
    Check(TranslationPolicy.ParseTargetLanguage("英语 (English)") == "English", "english label");
    Check(TranslationPolicy.ParseTargetLanguage("English") == "English", "plain english");
    Check(TranslationPolicy.ParseTargetLanguage("日语") == "Japanese", "japanese label");
    Check(TranslationPolicy.ParseTargetLanguage("日本語 (Japanese)") == "Japanese", "japanese long label");
    Check(TranslationPolicy.ParseTargetLanguage("中文") == "Chinese", "chinese label");
    Check(TranslationPolicy.ParseTargetLanguage("韩语") == "Korean", "korean label");
    Check(TranslationPolicy.ParseTargetLanguage("俄语") == "Russian", "russian label");
    Check(TranslationPolicy.ParseTargetLanguage("法语") == "French", "french label");
    Check(TranslationPolicy.ParseTargetLanguage("德语") == "German", "german label");
    Check(TranslationPolicy.ParseTargetLanguage("西班牙语") == "Spanish", "spanish label");
});

Case("unknown target language defaults to English", () =>
{
    Check(TranslationPolicy.ParseTargetLanguage(null) == "English", "null");
    Check(TranslationPolicy.ParseTargetLanguage("") == "English", "empty");
    Check(TranslationPolicy.ParseTargetLanguage("Klingon") == "English", "unknown");
});

Console.WriteLine($"translation policy regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
