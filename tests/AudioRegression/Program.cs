using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NAudio.Wave;

// Extract current production members, not copies of their implementations. Native audio,
// Whisper downloads/inference and WinUI are not required. NAudio formats are real 3.1.0 types.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
CompilationUnitSyntax Parse(string relativePath) => CSharpSyntaxTree.ParseText(
    File.ReadAllText(Path.Combine(root, relativePath))).GetCompilationUnitRoot();
string Select(string relativePath, string className, params string[] names)
{
    var wanted = names.ToHashSet(StringComparer.Ordinal);
    var members = Parse(relativePath).DescendantNodes().OfType<ClassDeclarationSyntax>()
        .Where(c => c.Identifier.Text == className).SelectMany(c => c.Members)
        .Where(m => m switch
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
const string autoSwitcherPath = "Services/Speech/LiveCaptionsLanguageAutoSwitcher.cs";
string bufferSource = Parse(autoSwitcherPath).DescendantNodes().OfType<ClassDeclarationSyntax>()
    .Single(c => c.Identifier.Text == "AudioSlidingBuffer").ToFullString();
string settingsMember = Select("Services/Settings/SettingsService.cs", "SettingsService", "SpeechModelType");
string audioMembers = Select(autoSwitcherPath, "LiveCaptionsLanguageAutoSwitcher",
    "_renderSlidingBuffer", "_micSlidingBuffer", "_lastRenderAudioTime", "_lastMicAudioTime",
    "ProcessIncomingAudio", "ExtractMonoSample", "CalculateRms");
string source = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
public sealed class ConfigData { public string SpeechModelType = "tiny"; }
public sealed class FakeSpeechService {
    readonly SemaphoreSlim modelLock = new(1,1);
    public string CurrentModelType {get; private set;} = "tiny";
    public TaskCompletionSource<bool> BaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> BaseRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<Task> Pending = new();
    public List<string> Requested = new();
    // Same FIFO/actual-state behavior as SpeechService.SwitchModelAsync. The native
    // model builder is replaced by a deterministic gate so the setter race is reproducible.
    public Task SwitchModelAsync(string model) {
        Requested.Add(model);
        var task = SwitchCore(model);
        Pending.Add(task);
        return task;
    }
    async Task SwitchCore(string model) {
        await modelLock.WaitAsync();
        try {
            if (CurrentModelType == model) return;
            if (model == "base") {
                BaseEntered.TrySetResult(true);
                await BaseRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            CurrentModelType = model;
        } finally { modelLock.Release(); }
    }
}
public sealed class SettingsService {
    public FakeSpeechService SpeechService = new();
    public ConfigData Config = new();
    public int SaveCalls;
    void SaveConfigDebounced() => SaveCalls++;
""" + settingsMember + """
}
""" + bufferSource + """
public sealed class LiveCaptionsLanguageAutoSwitcher {
""" + audioMembers + """
    public float[] Decode(byte[] bytes, WaveFormat format, bool isMic = false, int? byteCount = null) {
        _renderSlidingBuffer.Clear();
        _micSlidingBuffer.Clear();
        ProcessIncomingAudio(bytes, byteCount ?? bytes.Length, format, isMic);
        return (isMic ? _micSlidingBuffer : _renderSlidingBuffer).GetLatestSnapshot(1) ?? Array.Empty<float>();
    }
    public static float Rms(float[] samples) => CalculateRms(samples);
}
public static class Cases {
    static int failures, total;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Case(string name, Func<Task> run) {
        total++;
        try { await run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    static async Task ModelSequence(params string[] targets) {
        var settings = new SettingsService();
        settings.SpeechModelType = "base";
        await settings.SpeechService.BaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var target in targets) settings.SpeechModelType = target;
        settings.SpeechService.BaseRelease.SetResult(true);
        await Task.WhenAll(settings.SpeechService.Pending).WaitAsync(TimeSpan.FromSeconds(5));
        string expected = targets.Last();
        Check(settings.SpeechService.CurrentModelType == expected,
            "latest requested " + expected + ", actual " + settings.SpeechService.CurrentModelType);
        Check(settings.Config.SpeechModelType == expected, "configuration lost latest request");
        Check(settings.SpeechService.Requested.Count == targets.Length + 1, "setter filtered an in-flight request");
    }
    static WaveFormat Format(string kind, int rate, int channels) {
        bool floating = kind.EndsWith("float32");
        int bits = kind.Contains("16") ? 16 : kind.Contains("24") && !kind.Contains("in32") ? 24 : 32;
        if (kind.StartsWith("ext")) return new WaveFormatExtensible(rate, bits, channels,
            floating ? AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT : AudioMediaSubtypes.MEDIASUBTYPE_PCM,
            kind.Contains("in32") ? 24 : bits, 0);
        return floating ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, bits, channels);
    }
    static byte[] Encode(string kind, WaveFormat format, int frames) {
        var bytes = new byte[frames * format.BlockAlign];
        int width = format.BitsPerSample / 8;
        for (int f = 0; f < frames; f++) {
            for (int ch = 0; ch < format.Channels; ch++) {
                double sample = 0.5 * Math.Sin(2 * Math.PI * 1000 * f / format.SampleRate) * (ch == 0 ? 1 : 0.5);
                int offset = f * format.BlockAlign + ch * width;
                if (kind.EndsWith("float32")) {
                    BitConverter.GetBytes((float)sample).CopyTo(bytes, offset);
                } else {
                    long integer = (long)Math.Round(sample * (1L << (format.BitsPerSample - 1)));
                    if (kind.Contains("in32")) integer = (integer >> 8) << 8; // WAVEFORMATEXTENSIBLE PCM is left aligned.
                    for (int i = 0; i < width; i++) bytes[offset + i] = (byte)(integer >> (8 * i));
                }
            }
        }
        return bytes;
    }
    static Task AudioWave(string kind, int rate, int channels) {
        var format = Format(kind, rate, channels);
        const int frames = 480;
        var bytes = Encode(kind, format, frames);
        var decoder = new LiveCaptionsLanguageAutoSwitcher();
        float[] actual = decoder.Decode(bytes, format, isMic: channels == 1);
        int expectedCount = rate == 48000 ? frames / 3 : frames;
        Check(actual.Length == expectedCount, "wrong output sample count " + actual.Length);
        double scale = channels == 1 ? 0.5 : 0.375;
        double Expected(int f) => scale * Math.Sin(2 * Math.PI * 1000 * f / rate);
        var expected = new float[expectedCount];
        for (int i = 0; i < expectedCount; i++) {
            expected[i] = (float)(rate == 48000
                ? Expected(3*i) * 0.25 + Expected(3*i+1) * 0.5 + Expected(3*i+2) * 0.25
                : Expected(i));
            Check(float.IsFinite(actual[i]) && Math.Abs(actual[i] - expected[i]) < 0.00004,
                "sample " + i + " expected " + expected[i] + ", actual " + actual[i]);
        }
        Check(Math.Abs(LiveCaptionsLanguageAutoSwitcher.Rms(actual) - LiveCaptionsLanguageAutoSwitcher.Rms(expected)) < 0.00004,
            "RMS changed across encodings");
        return Task.CompletedTask;
    }
    static Task PcmBoundaries(int bits) {
        var format = new WaveFormat(16000, bits, 1);
        int width = bits / 8;
        long min = -(1L << (bits-1)), max = (1L << (bits-1)) - 1;
        long[] integers = [min, -1, 0, 1, max];
        var bytes = new byte[integers.Length * width];
        for (int i = 0; i < integers.Length; i++)
            for (int b = 0; b < width; b++) bytes[i*width+b] = (byte)(integers[i] >> (8*b));
        var actual = new LiveCaptionsLanguageAutoSwitcher().Decode(bytes, format);
        for (int i = 0; i < integers.Length; i++)
            Check(actual.Length == integers.Length && Math.Abs(actual[i] - (float)(integers[i] / (double)-min)) < 0.0000001,
                "signed " + bits + "-bit boundary " + integers[i] + " decoded incorrectly");
        return Task.CompletedTask;
    }
    static Task UnsupportedFormat() {
        var format = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Adpcm, 16000, 1, 64000, 4, 32);
        Check(new LiveCaptionsLanguageAutoSwitcher().Decode(new byte[32], format).Length == 0,
            "unsupported compression must not be interpreted as float PCM");
        var unknown = new WaveFormatExtensible(16000, 32, 1, Guid.NewGuid(), 32, 0);
        Check(new LiveCaptionsLanguageAutoSwitcher().Decode(new byte[32], unknown).Length == 0,
            "unknown Extensible SubFormat must be rejected");
        return Task.CompletedTask;
    }
    public static async Task<int> Run() {
        await Case("model tiny-base-tiny keeps latest request", () => ModelSequence("tiny"));
        await Case("model tiny-base-small keeps latest request", () => ModelSequence("small"));
        await Case("model repeated tiny requests during base load", () => ModelSequence("tiny", "tiny"));
        foreach (string kind in new[] {"pcm16", "pcm24", "pcm32", "float32", "ext-pcm16", "ext-pcm24", "ext-pcm32", "ext-float32", "ext-pcm24in32"}) {
            await Case(kind + " 16kHz mono", () => AudioWave(kind, 16000, 1));
            await Case(kind + " 48kHz stereo FIR", () => AudioWave(kind, 48000, 2));
        }
        foreach (int bits in new[] {16,24,32}) await Case("PCM signed boundaries " + bits, () => PcmBoundaries(bits));
        await Case("unsupported format rejected", UnsupportedFormat);
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";
var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Append(typeof(WaveFormat).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase);
var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("AudioProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
