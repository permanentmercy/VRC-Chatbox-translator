using VrcChatboxDemo.Services.Synthesis;

// TtsCatalog 回归：权重/音色目录扫描与排序规则。
// 原先这些规则埋在 1,803 行的 TtsService 里，且隐式依赖单例配置，无法直接测。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Case(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

// 用测试输出目录下的临时根（沙箱里 %TEMP% 不一定可写）
string root = Path.Combine(AppContext.BaseDirectory, "catalog-fixture");
string scriptPath() => Path.Combine(root, "tts_server.py");
string checkpointsDir() => Path.Combine(root, "checkpoints");
string promptsDir() => Path.Combine(root, "prompts");

void Reset()
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    Directory.CreateDirectory(checkpointsDir());
    Directory.CreateDirectory(promptsDir());
}
void Touch(string path, int bytes = 1024) => File.WriteAllBytes(path, new byte[bytes]);

try
{
    Case("directory resolution follows the server script location", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Check(TtsCatalog.ResolveCheckpointsDir(scriptPath()) == checkpointsDir(), "checkpoints dir mismatch");
        Check(TtsCatalog.ResolvePromptsDir(scriptPath()) == promptsDir(), "prompts dir mismatch");
    });

    Case("blank script path resolves to nothing", () =>
    {
        foreach (string? path in new[] { null, "", "   " })
        {
            Check(TtsCatalog.ResolveCheckpointsDir(path) == null, "expected null for '" + path + "'");
            Check(TtsCatalog.ResolvePromptsDir(path) == null, "expected null prompts for '" + path + "'");
        }
    });

    Case("missing directories log instead of throwing", () =>
    {
        Reset();
        // Reset 会建目录，这里要模拟"用户还没装 indextts"
        Directory.Delete(checkpointsDir());
        Directory.Delete(promptsDir());

        var logs = new List<string>();
        var models = TtsCatalog.DiscoverModels(scriptPath(), logs.Add);
        var prompts = TtsCatalog.DiscoverPrompts(scriptPath(), logs.Add);
        Check(models.Count == 0 && prompts.Count == 0, "expected empty results");
        Check(logs.Count == 2, "expected one log per scan, got " + logs.Count);
    });

    Case("vocoder and dvae weights are excluded", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(checkpointsDir(), "gpt.pth"));
        Touch(Path.Combine(checkpointsDir(), "bigvgan_v2_22khz.pth"));
        Touch(Path.Combine(checkpointsDir(), "dvae.pth"));

        var names = TtsCatalog.DiscoverModels(scriptPath()).Select(m => m.FileName).ToList();
        Check(names.Count == 1 && names[0] == "gpt.pth", "got: " + string.Join(",", names));
    });

    Case("paired prompt marks a fine-tuned weight", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(checkpointsDir(), "aya_lora.pth"));
        Touch(Path.Combine(promptsDir(), "aya_lora_prompt.pt"));

        var model = TtsCatalog.DiscoverModels(scriptPath()).Single();
        Check(model.HasMatchingPrompt, "expected a matching prompt");
        Check(!model.IsBaseModel, "lora weight must not be treated as base");
        Check(!model.Description.Contains("缺配套"), "description should not warn: " + model.Description);
    });

    Case("weight without prompt warns about unchanged timbre", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(checkpointsDir(), "mei_lora.pth"));

        var model = TtsCatalog.DiscoverModels(scriptPath()).Single();
        Check(!model.HasMatchingPrompt, "no prompt file exists");
        Check(model.Description.Contains("缺配套"), "missing warning: " + model.Description);
    });

    Case("weight without lora suffix is treated as base model", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(checkpointsDir(), "gpt.pth"));

        var model = TtsCatalog.DiscoverModels(scriptPath()).Single();
        Check(model.IsBaseModel, "gpt.pth should be the base model");
        Check(model.Description.Contains("原始底模"), "missing base hint: " + model.Description);
    });

    Case("fp8 sidecar marks a quantized weight", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(checkpointsDir(), "gpt_me_lora_q8.pth"));
        File.WriteAllText(Path.Combine(checkpointsDir(), "gpt_me_lora_q8.json"), "{\"format\":\"fp8_e4m3\"}");

        var model = TtsCatalog.DiscoverModels(scriptPath()).Single();
        Check(model.IsQuantized, "sidecar was not detected");
        Check(model.Description.Contains("FP8"), "description missing fp8 tag: " + model.Description);
    });

    Case("broken sidecar is treated as non-quantized", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(checkpointsDir(), "broken.pth"));
        File.WriteAllText(Path.Combine(checkpointsDir(), "broken.json"), "{ not json");

        var model = TtsCatalog.DiscoverModels(scriptPath()).Single();
        Check(!model.IsQuantized, "malformed sidecar must not mark the weight as quantized");
    });

    Case("ordering prefers paired fine-tuned weights, base model last", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(checkpointsDir(), "base.pth"));
        Touch(Path.Combine(checkpointsDir(), "zzz_lora.pth"));
        Touch(Path.Combine(checkpointsDir(), "aya_lora.pth"));
        Touch(Path.Combine(promptsDir(), "aya_lora_prompt.pt"));

        var names = TtsCatalog.DiscoverModels(scriptPath()).Select(m => m.FileName).ToList();
        Check(names[0] == "aya_lora.pth", "paired weight must come first: " + string.Join(",", names));
        Check(names[^1] == "base.pth", "base model must come last: " + string.Join(",", names));
    });

    Case("prompt listing puts paired prompts first", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        Touch(Path.Combine(promptsDir(), "zzz_prompt.pt"));
        Touch(Path.Combine(promptsDir(), "standalone.pt"));

        var names = TtsCatalog.DiscoverPrompts(scriptPath()).Select(p => p.FileName).ToList();
        Check(names.Count == 2, "expected 2 prompts, got " + names.Count);
        Check(names[0] == "zzz_prompt.pt", "paired prompt must come first: " + string.Join(",", names));
    });

    Case("paired prompt detection", () =>
    {
        Check(TtsCatalog.IsPromptPairedWith("aya_prompt.pt", "aya.pth"), "paired case failed");
        Check(!TtsCatalog.IsPromptPairedWith("aya_prompt.pt", "mei.pth"), "mismatched case failed");
        Check(!TtsCatalog.IsPromptPairedWith("", "aya.pth"), "empty prompt should be false");
        Check(!TtsCatalog.IsPromptPairedWith("aya_prompt.pt", null!), "null checkpoint should be false");
    });

    Case("quantization probe handles missing sidecar", () =>
    {
        Reset();
        Touch(scriptPath(), 1);
        string weight = Path.Combine(checkpointsDir(), "plain.pth");
        Touch(weight);
        Check(!TtsCatalog.IsQuantizedCheckpoint(weight), "missing sidecar must be false");
    });
}
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine($"tts catalog regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
