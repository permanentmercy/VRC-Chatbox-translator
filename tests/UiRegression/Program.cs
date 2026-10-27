using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Extracts the current production members from Views/Sections/*.xaml.cs instead of copying
// their implementations. WinUI is not required: ComboBox / ComboBoxItem / SettingsService /
// AudioDeviceService / TtsService are replaced by deterministic stand-ins, so both defects are
// reproducible without a window, a device or an IndexTTS install.
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

const string speechSectionPath = "Views/Sections/SpeechSection.xaml.cs";
const string ttsSectionPath = "Views/Sections/TtsSection.xaml.cs";

// _isInitializing is deliberately NOT extracted: production initialises it to true and only the
// Loaded/refresh paths flip it. Both defects happen with it false, so the stand-in fixes it there.
string speechMembers = Select(speechSectionPath, "SpeechSection",
    "_audioDevices", "_isRefreshingAudioDevices", "InitAudioDevicesAsync", "AudioDeviceComboBox_SelectionChanged",
    "RefreshAudioDevicesButton_Click");
string ttsMembers = Select(ttsSectionPath, "TtsSection",
    "_promptOptions", "LoadPromptOptions", "FollowPromptForWeight", "PromptNameComboBox_SelectionChanged");

string source = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// ------------------------------------------------------------------ shared stand-ins
public sealed class SelectionChangedEventArgs { }
public sealed class RoutedEventArgs { }

public sealed class ComboBoxItem
{
    public object? Content { get; set; }
    public object? Tag { get; set; }
}

public sealed class ItemCollection
{
    readonly ComboBox owner;
    readonly List<object> items = new();
    public ItemCollection(ComboBox owner) { this.owner = owner; }
    public int Count => items.Count;
    public object this[int index] => items[index];
    public void Add(object item) => items.Add(item);
    public void Insert(int index, object item) => items.Insert(index, item);
    // WinUI resets the selection (and therefore raises SelectionChanged) when Items are cleared.
    public void Clear() { items.Clear(); owner.SelectedIndex = -1; }
}

public sealed class ComboBox
{
    readonly ItemCollection items;
    int selectedIndex = -1;
    public ComboBox() { items = new ItemCollection(this); }
    public ItemCollection Items => items;
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (selectedIndex == value) return;
            selectedIndex = value;
            SelectionChanged?.Invoke(this, new SelectionChangedEventArgs());
        }
    }
    public object? SelectedItem => selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null;
}

public sealed record AudioDeviceInfo(string Id, string Name, string EndpointId, bool IsDefault);
public sealed record TtsPromptOption(string FileName, string DisplayName, string Description, long SizeBytes);

public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();
    public string AudioInputDeviceId = string.Empty;
    public string SpeechEngine = "Whisper";
    public string TtsPromptFile = string.Empty;
    public List<(string? EndpointId, string DisplayName)> SwitchCalls = new();
    public List<string> Logs = new();
    public Task SwitchAudioDeviceAsync(string? endpointId, string deviceDisplayName)
    {
        SwitchCalls.Add((endpointId, deviceDisplayName));
        AudioInputDeviceId = endpointId ?? string.Empty;
        return Task.CompletedTask;
    }
    public void AddLog(string address, string content, bool success, string? error = null) => Logs.Add(content);
    public void Reset()
    {
        AudioInputDeviceId = string.Empty;
        SpeechEngine = "Whisper";
        TtsPromptFile = string.Empty;
        SwitchCalls = new();
        Logs = new();
    }
}

public static class AudioDeviceService
{
    public static Func<Task<List<AudioDeviceInfo>>> Handler = () => Task.FromResult(new List<AudioDeviceInfo>());
    public static int Calls;
    public static Task<List<AudioDeviceInfo>> GetAudioRenderDevicesAsync()
    {
        Calls++;
        return Handler();
    }
}

public sealed class TtsService
{
    public static TtsService Instance { get; } = new();
    public List<TtsPromptOption> Prompts = new();
    public List<TtsPromptOption> DiscoverPrompts(string? serverScriptPath = null) => Prompts;
}

// ------------------------------------------------------------------ SpeechSection under test
public sealed class SpeechSection
{
    // Production flips this to false once Loaded finishes; the rebuild path runs with it false.
    private bool _isInitializing = false;
    public readonly ComboBox AudioDeviceComboBox = new();
    public readonly ComboBox SpeechEngineComboBox = new();
    private void UpdateEngineVisibility(string engine) { }
    // SpeechSection.xaml wires SelectionChanged="AudioDeviceComboBox_SelectionChanged".
    public SpeechSection() => AudioDeviceComboBox.SelectionChanged += (s, e) => AudioDeviceComboBox_SelectionChanged(s, e);
    public Task RebuildDevicesAsync() => InitAudioDevicesAsync();
    public void ClickRefresh() => RefreshAudioDevicesButton_Click(this, new RoutedEventArgs());
    public bool IsInitializing => _isInitializing;
    public bool IsRefreshingDevices => _isRefreshingAudioDevices;
""" + speechMembers + """
}

// ------------------------------------------------------------------ TtsSection under test
public sealed class TtsSection
{
    private bool _isInitializing = false;
    public readonly ComboBox PromptNameComboBox = new();
    private void UpdatePromptHint() { }
    private void UpdateModelHint() { }
    // TtsSection.xaml wires SelectionChanged="PromptNameComboBox_SelectionChanged".
    public TtsSection() => PromptNameComboBox.SelectionChanged += (s, e) => PromptNameComboBox_SelectionChanged(s, e);
    public void LoadPrompts() => LoadPromptOptions();
    public void FollowWeight(string checkpointFile) => FollowPromptForWeight(checkpointFile);
""" + ttsMembers + """
}

public static class Cases
{
    static int failures, total;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Case(string name, Func<Task> run)
    {
        total++;
        try { await run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    static readonly AudioDeviceInfo[] TwoSpeakers =
    {
        new("speaker-1", "扬声器 A", "speaker-1", false),
        new("speaker-2", "扬声器 B", "speaker-2", false),
    };

    static SpeechSection NewSection(string savedEndpointId, params AudioDeviceInfo[] devices)
    {
        SettingsService.Instance.Reset();
        SettingsService.Instance.AudioInputDeviceId = savedEndpointId;
        AudioDeviceService.Calls = 0;
        AudioDeviceService.Handler = () => Task.FromResult(new List<AudioDeviceInfo>(devices));
        return new SpeechSection();
    }

    // Defect 1a: the rebuild falls back to index 0 because the saved endpoint is gone. That
    // programmatic SelectedIndex must not be treated as "user picked 系统默认设备", otherwise
    // AudioInputDeviceId is cleared and persisted.
    static async Task RebuildWithMissingSavedEndpoint()
    {
        var section = NewSection("usb-headset-unplugged", TwoSpeakers);
        await section.RebuildDevicesAsync();
        Check(section.AudioDeviceComboBox.SelectedIndex == 0, "expected fallback selection 0, got " + section.AudioDeviceComboBox.SelectedIndex);
        Check(SettingsService.Instance.SwitchCalls.Count == 0,
            "rebuild switched the audio device " + SettingsService.Instance.SwitchCalls.Count + " time(s): " +
            string.Join(", ", SettingsService.Instance.SwitchCalls.Select(c => c.EndpointId ?? "<null>")));
        Check(SettingsService.Instance.AudioInputDeviceId == "usb-headset-unplugged",
            "saved AudioInputDeviceId was overwritten with '" + SettingsService.Instance.AudioInputDeviceId + "'");
    }

    // Defect 1b: the saved endpoint is still present, so the rebuild re-selects it and fires a
    // second SwitchAudioDeviceAsync on top of the one SettingsService.TargetAudioProcessName
    // already issued -> the speech engine is stopped and started twice per process switch.
    static async Task RebuildWithPresentSavedEndpoint()
    {
        var section = NewSection("speaker-1", TwoSpeakers);
        await section.RebuildDevicesAsync();
        Check(section.AudioDeviceComboBox.SelectedIndex == 1, "expected saved device selection 1, got " + section.AudioDeviceComboBox.SelectedIndex);
        Check(SettingsService.Instance.SwitchCalls.Count == 0,
            "rebuild switched the audio device " + SettingsService.Instance.SwitchCalls.Count + " time(s)");
    }

    // Over-fix guard: a real user pick after the rebuild must still switch the device exactly once.
    static async Task UserSelectionAfterRebuildStillSwitches()
    {
        var section = NewSection("speaker-1", TwoSpeakers);
        await section.RebuildDevicesAsync();
        Check(SettingsService.Instance.SwitchCalls.Count == 0, "rebuild must not switch on its own");
        section.AudioDeviceComboBox.SelectedIndex = 2; // 扬声器 B
        Check(SettingsService.Instance.SwitchCalls.Count == 1,
            "user pick must switch exactly once, got " + SettingsService.Instance.SwitchCalls.Count);
        Check(SettingsService.Instance.SwitchCalls[0].EndpointId == "speaker-2",
            "user pick switched to '" + SettingsService.Instance.SwitchCalls[0].EndpointId + "'");
        Check(SettingsService.Instance.AudioInputDeviceId == "speaker-2",
            "user pick did not persist, AudioInputDeviceId='" + SettingsService.Instance.AudioInputDeviceId + "'");
    }

    // Defect 1c: the manual refresh must not park _isInitializing across the device enumeration
    // await. While it is held, every other control's Toggled/SelectionChanged early-returns, so the
    // page shows the new value while SettingsService keeps the old one.
    static Task RefreshDoesNotHoldInitializingAcrossAwait()
    {
        SettingsService.Instance.Reset();
        SettingsService.Instance.AudioInputDeviceId = "speaker-1";
        var gate = new TaskCompletionSource<List<AudioDeviceInfo>>();
        AudioDeviceService.Handler = () => gate.Task;
        var section = new SpeechSection();

        section.ClickRefresh();
        Check(section.IsRefreshingDevices, "refresh did not enter the rebuild guard");
        Check(!section.IsInitializing,
            "_isInitializing is still true while the device enumeration is pending; every other control would drop its user action");

        gate.SetResult(new List<AudioDeviceInfo>(TwoSpeakers)); // continuations run inline
        Check(!section.IsRefreshingDevices, "rebuild guard was not released after the enumeration completed");
        Check(!section.IsInitializing, "_isInitializing was left true after the refresh completed");
        Check(SettingsService.Instance.SwitchCalls.Count == 0,
            "refresh switched the audio device " + SettingsService.Instance.SwitchCalls.Count + " time(s)");
        return Task.CompletedTask;
    }

    static TtsSection NewTtsSection(string savedPrompt, params string[] promptFiles)
    {
        SettingsService.Instance.Reset();
        SettingsService.Instance.TtsPromptFile = savedPrompt;
        TtsService.Instance.Prompts = promptFiles
            .Select(f => new TtsPromptOption(f, f + " (desc)", "desc", 1024))
            .ToList();
        var section = new TtsSection();
        section.LoadPrompts();
        return section;
    }

    static string SelectedTag(TtsSection section) =>
        (section.PromptNameComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "<null>";

    // Defect 2: a stale saved prompt (production default "me_prompt.pt") is prepended at index 0,
    // so Items is one longer than _promptOptions and FollowPromptForWeight selects the neighbour.
    static Task FollowPicksPairedPromptForSecondWeight()
    {
        var section = NewTtsSection("me_prompt.pt", "aya_prompt.pt", "mei_prompt.pt");
        Check(section.PromptNameComboBox.Items.Count == 3,
            "expected 3 items (placeholder + 2 prompts), got " + section.PromptNameComboBox.Items.Count);
        section.FollowWeight("mei.pth");
        Check(SelectedTag(section) == "mei_prompt.pt", "auto-follow selected '" + SelectedTag(section) + "' instead of mei_prompt.pt");
        Check(SettingsService.Instance.TtsPromptFile == "mei_prompt.pt",
            "auto-follow persisted '" + SettingsService.Instance.TtsPromptFile + "' instead of mei_prompt.pt");
        return Task.CompletedTask;
    }

    // Same off-by-one at index 0: the old code re-selected the placeholder, so the voice never changed.
    static Task FollowPicksPairedPromptForFirstWeight()
    {
        var section = NewTtsSection("me_prompt.pt", "aya_prompt.pt", "mei_prompt.pt");
        section.FollowWeight("aya.pth");
        Check(SelectedTag(section) == "aya_prompt.pt", "auto-follow selected '" + SelectedTag(section) + "' instead of aya_prompt.pt");
        Check(SettingsService.Instance.TtsPromptFile == "aya_prompt.pt",
            "auto-follow persisted '" + SettingsService.Instance.TtsPromptFile + "' instead of aya_prompt.pt");
        return Task.CompletedTask;
    }

    // No-regression guard: when the saved prompt is present there is no placeholder and the
    // original index alignment already worked, so the fix must keep this passing.
    static Task FollowWithoutPlaceholderKeepsAlignment()
    {
        var section = NewTtsSection("aya_prompt.pt", "aya_prompt.pt", "mei_prompt.pt");
        Check(section.PromptNameComboBox.Items.Count == 2,
            "expected 2 items (no placeholder), got " + section.PromptNameComboBox.Items.Count);
        section.FollowWeight("mei.pth");
        Check(SelectedTag(section) == "mei_prompt.pt", "auto-follow selected '" + SelectedTag(section) + "' instead of mei_prompt.pt");
        return Task.CompletedTask;
    }

    public static async Task<int> Run()
    {
        await Case("device rebuild with missing saved endpoint must not switch", RebuildWithMissingSavedEndpoint);
        await Case("device rebuild with present saved endpoint must not switch", RebuildWithPresentSavedEndpoint);
        await Case("user device pick after rebuild still switches once", UserSelectionAfterRebuildStillSwitches);
        await Case("refresh does not hold _isInitializing across the await", RefreshDoesNotHoldInitializingAcrossAwait);
        await Case("auto-follow picks paired prompt (stale saved prompt at index 0)", FollowPicksPairedPromptForSecondWeight);
        await Case("auto-follow picks paired prompt for the first weight", FollowPicksPairedPromptForFirstWeight);
        await Case("auto-follow keeps alignment without a placeholder", FollowWithoutPlaceholderKeepsAlignment);
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";

var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Distinct(StringComparer.OrdinalIgnoreCase);
var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("UiProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
