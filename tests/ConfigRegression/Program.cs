using System.Text.Json;
using VrcChatboxDemo.Services;
using VrcChatboxDemo.Services.Configuration;

// ConfigStore 回归：直接实例化生产类，验证持久化语义（不再解析源码文本）。
// 覆盖：缺文件/损坏文件/正常加载的区分、防抖保存取最新快照、立即保存取消挂起的防抖、
// 旧版绝对路径迁移、默认值不再绑定某台机器。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Case(string name, Func<Task> body)
{
    try { body().GetAwaiter().GetResult(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

// ConfigStore.ConfigPath 固定指向程序目录，测试直接操作该文件，结束时还原用户真实配置。
string configPath = ConfigStore.ConfigPath;
string? backup = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
void ResetConfigFile()
{
    if (File.Exists(configPath)) File.Delete(configPath);
}

try
{
    Case("missing file reports Missing", () =>
    {
        ResetConfigFile();
        using var store = new ConfigStore();
        var outcome = store.Load(out var cfg);
        Check(outcome == ConfigStore.LoadOutcome.Missing, "outcome=" + outcome);
        Check(cfg == null, "config should be null when missing");
        return Task.CompletedTask;
    });

    Case("corrupted file reports Corrupted and keeps the file", () =>
    {
        ResetConfigFile();
        File.WriteAllText(configPath, "{ this is not json");
        using var store = new ConfigStore();
        var outcome = store.Load(out var cfg);
        Check(outcome == ConfigStore.LoadOutcome.Corrupted, "outcome=" + outcome);
        Check(cfg == null, "config should be null when corrupted");
        Check(File.Exists(configPath), "corrupted file must not be deleted by Load");
        return Task.CompletedTask;
    });

    Case("save then load round-trips values", () =>
    {
        ResetConfigFile();
        using var store = new ConfigStore();
        store.Save(new AppConfig { WindowWidth = 1234, OllamaModel = "custom:1b", InboundHttpPort = 9123 });

        using var reader = new ConfigStore();
        Check(reader.Load(out var loaded) == ConfigStore.LoadOutcome.Loaded, "load failed");
        Check(loaded!.WindowWidth == 1234 && loaded.OllamaModel == "custom:1b" && loaded.InboundHttpPort == 9123,
            "round-trip mismatch");
        return Task.CompletedTask;
    });

    Case("debounced save persists the latest snapshot, not the scheduled one", () =>
    {
        ResetConfigFile();
        using var store = new ConfigStore();
        var cfg = new AppConfig { OllamaModel = "first" };
        store.SaveDebounced(() => cfg, 120);
        cfg.OllamaModel = "second";
        Thread.Sleep(400);

        using var reader = new ConfigStore();
        reader.Load(out var loaded);
        Check(loaded?.OllamaModel == "second", "persisted=" + loaded?.OllamaModel);
        return Task.CompletedTask;
    });

    Case("immediate save cancels a pending debounced save", () =>
    {
        ResetConfigFile();
        using var store = new ConfigStore();
        var cfg = new AppConfig { OllamaModel = "debounced" };
        store.SaveDebounced(() => cfg, 250);
        cfg.OllamaModel = "immediate";
        store.Save(cfg);
        Thread.Sleep(450);

        using var reader = new ConfigStore();
        reader.Load(out var loaded);
        Check(loaded?.OllamaModel == "immediate", "persisted=" + loaded?.OllamaModel);
        return Task.CompletedTask;
    });

    Case("migrate clears machine-specific absolute paths", () =>
    {
        var cfg = new AppConfig
        {
            TtsServerScriptPath = @"Z:\does-not-exist\indextts\tts_server.py",
            TtsPythonExePath = @"Z:\does-not-exist\python.exe"
        };
        bool changed = ConfigStore.MigrateLegacyPaths(cfg);
        Check(changed, "migration should report a change for dead paths");
        Check(!cfg.TtsServerScriptPath.Contains(@"Z:\does-not-exist"), "dead script path kept: " + cfg.TtsServerScriptPath);
        Check(!cfg.TtsPythonExePath.Contains(@"Z:\does-not-exist"), "dead python path kept: " + cfg.TtsPythonExePath);
        Check(!cfg.TtsServerScriptPath.Contains(@"H:\") && !cfg.TtsPythonExePath.Contains(@"D:\"),
            "migration must never inject another machine's path");
        return Task.CompletedTask;
    });

    Case("migrate keeps an existing configured path", () =>
    {
        // 用测试目录内的临时文件（沙箱里 %TEMP% 可能不可写）
        string realFile = Path.Combine(AppContext.BaseDirectory, "existing-path-probe.tmp");
        File.WriteAllText(realFile, "x");
        try
        {
            var cfg = new AppConfig { TtsServerScriptPath = realFile, TtsPythonExePath = realFile };
            bool changed = ConfigStore.MigrateLegacyPaths(cfg);
            Check(!changed, "migration should not touch existing paths");
            Check(cfg.TtsServerScriptPath == realFile && cfg.TtsPythonExePath == realFile, "existing paths were replaced");
        }
        finally { try { File.Delete(realFile); } catch { } }
        return Task.CompletedTask;
    });

    Case("defaults no longer hardcode a specific machine", () =>
    {
        var cfg = new AppConfig();
        Check(!cfg.TtsServerScriptPath.Contains(@"H:\program"), "default script path is machine-specific");
        Check(!cfg.TtsPythonExePath.Contains(@"D:\software"), "default python path is machine-specific");
        Check(cfg.OllamaPromptTemplate.Contains("{text}"), "default prompt lost its placeholder");
        Check(cfg.OllamaRequestFrequency == AppConfig.DefaultRequestFrequencyMode, "default frequency mismatch");
        return Task.CompletedTask;
    });

    Case("saved json is indented and keeps property names", () =>
    {
        ResetConfigFile();
        using var store = new ConfigStore();
        store.Save(new AppConfig());
        string json = File.ReadAllText(configPath);
        Check(json.Contains('\n'), "config.json should be indented");
        using var doc = JsonDocument.Parse(json);
        Check(doc.RootElement.TryGetProperty("WindowWidth", out _), "property names must stay readable");
        return Task.CompletedTask;
    });
}
finally
{
    try
    {
        if (backup != null) File.WriteAllText(configPath, backup);
        else if (File.Exists(configPath)) File.Delete(configPath);
    }
    catch { }
}

Console.WriteLine($"config regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
