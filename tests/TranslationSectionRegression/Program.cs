using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Extracts the current production members from Views/Sections/TranslationSection.xaml.cs instead of
// copying their implementations. WinUI is not required: ComboBox / SettingsService / OllamaService are
// replaced by stand-ins that mirror the production behaviour of an *editable* ComboBox (typing clears
// the selection and raises SelectionChanged) and of the Ollama model list.
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
    // WithoutLeadingTrivia drops leading trivia, which can carry a #endregion directive.
    return string.Join(Environment.NewLine, members.Select(m => m.WithoutLeadingTrivia().ToFullString()));
}

string translationMembers = Select("Views/Sections/TranslationSection.xaml.cs", "TranslationSection",
    "_isLoadingModels", "OllamaModelComboBox_SelectionChanged", "OllamaModelComboBox_LostFocus", "LoadOllamaModelsAsync");

string source = """
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// ------------------------------------------------------------------ UI stand-ins
public sealed class SelectionChangedEventArgs { }
public sealed class RoutedEventArgs { }

public sealed class ItemCollection
{
    readonly ComboBox owner;
    readonly List<object> items = new();
    public ItemCollection(ComboBox owner) { this.owner = owner; }
    public int Count => items.Count;
    public object this[int index] => items[index];
    public void Add(object item) => items.Add(item);
    // WinUI resets the selection (and raises SelectionChanged) when the item list is cleared.
    public void Clear() { items.Clear(); owner.SelectedIndex = -1; }
}

public sealed class ComboBox
{
    int selectedIndex = -1;
    public ComboBox() { Items = new ItemCollection(this); }
    public ItemCollection Items { get; }
    public string Text { get; set; } = string.Empty;
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<RoutedEventArgs>? LostFocus;
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
    public object? SelectedItem => selectedIndex >= 0 && selectedIndex < Items.Count ? Items[selectedIndex] : null;
    public void RaiseLostFocus() => LostFocus?.Invoke(this, new RoutedEventArgs());
    // Simulates typing into an editable ComboBox: as soon as the text no longer matches an item the
    // selection is cleared, which is exactly what raises SelectionChanged for the first keystroke.
    public void TypeText(string text)
    {
        Text = text;
        SelectedIndex = -1;
    }
}

// ------------------------------------------------------------------ service stand-ins
public sealed class OllamaService
{
    public List<string> Models = new();
    public List<string> Unloaded = new();
    public Task<List<string>> GetInstalledModelsAsync(string endpoint, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<string>(Models));
    public Task<bool> UnloadModelAsync(string model, string endpoint, CancellationToken cancellationToken = default)
    {
        Unloaded.Add(model);
        return Task.FromResult(true);
    }
    public void Reset() { Models = new(); Unloaded = new(); }
}

public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();
    public string OllamaEndpoint = "http://127.0.0.1:11434";
    public string OllamaModel = string.Empty;
    public readonly OllamaService OllamaService = new();
    public List<string> Logs = new();
    public void AddLog(string address, string content, bool success, string? error = null) => Logs.Add(content);
    public void Reset()
    {
        OllamaEndpoint = "http://127.0.0.1:11434";
        OllamaModel = string.Empty;
        Logs = new();
        OllamaService.Reset();
    }
}

// ------------------------------------------------------------------ TranslationSection under test
public sealed class TranslationSection
{
    // Production starts true and flips it false at the end of Loaded; the handlers run after that.
    private bool _isInitializing = false;
    public readonly ComboBox OllamaModelComboBox = new();

    // TranslationSection.xaml wires SelectionChanged / LostFocus on the editable model ComboBox.
    public TranslationSection()
    {
        OllamaModelComboBox.SelectionChanged += (s, e) => OllamaModelComboBox_SelectionChanged(s, e);
        OllamaModelComboBox.LostFocus += (s, e) => OllamaModelComboBox_LostFocus(s, e);
    }

    public Task LoadModelsAsync() => LoadOllamaModelsAsync();
""" + translationMembers + """
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

    static async Task<TranslationSection> NewLoadedSection(string configuredModel, params string[] serverModels)
    {
        SettingsService.Instance.Reset();
        SettingsService.Instance.OllamaModel = configuredModel;
        SettingsService.Instance.OllamaService.Models = serverModels.ToList();
        var section = new TranslationSection();
        await section.LoadModelsAsync();
        return section;
    }

    // Defect 1: the model ComboBox is editable. The moment the user types the first character the
    // selection is cleared and SelectionChanged fires; the `else if (Text)` branch then treats that
    // half-typed text as a committed model name, writing it to the config AND unloading the previous
    // model from VRAM. The full typed name is only committed on LostFocus.
    static async Task TypingAModelNameDoesNotCommitEachKeystroke()
    {
        var section = await NewLoadedSection("qwen2.5", "qwen2.5", "llama3:8b");
        Check(section.OllamaModelComboBox.SelectedItem as string == "qwen2.5", "precondition: saved model should be selected");

        section.OllamaModelComboBox.TypeText("l"); // first keystroke of "llama3:8b"

        Check(SettingsService.Instance.OllamaModel == "qwen2.5",
            "half-typed text was committed as the model name ('" + SettingsService.Instance.OllamaModel + "')");
        Check(SettingsService.Instance.OllamaService.Unloaded.Count == 0,
            "the previous model was unloaded on the first keystroke (" +
            string.Join(", ", SettingsService.Instance.OllamaService.Unloaded) + ")");
        return;
    }

    // Guard: picking an entry from the list must still switch the model and free the old one.
    static async Task PickingFromTheListStillSwitches()
    {
        var section = await NewLoadedSection("qwen2.5", "qwen2.5", "llama3:8b");

        section.OllamaModelComboBox.SelectedIndex = 1;

        Check(SettingsService.Instance.OllamaModel == "llama3:8b",
            "picking from the list did not persist, OllamaModel='" + SettingsService.Instance.OllamaModel + "'");
        Check(SettingsService.Instance.OllamaService.Unloaded.Count == 1 &&
              SettingsService.Instance.OllamaService.Unloaded[0] == "qwen2.5",
            "the previous model was not unloaded from VRAM");
        return;
    }

    // Guard: a hand-typed name is still committed, but on LostFocus (the documented path).
    static async Task TypedModelNameIsCommittedOnLostFocus()
    {
        var section = await NewLoadedSection("qwen2.5", "qwen2.5", "llama3:8b");

        section.OllamaModelComboBox.TypeText("llama3:8b");
        section.OllamaModelComboBox.RaiseLostFocus();

        Check(SettingsService.Instance.OllamaModel == "llama3:8b",
            "typed model name was not committed on LostFocus, OllamaModel='" + SettingsService.Instance.OllamaModel + "'");
        return;
    }

    // Defect 2: when the configured model no longer exists on the server the ComboBox silently falls
    // back to HY-MT (or the first entry) without updating the config, so the page shows one model while
    // every request (test/translation) still targets the deleted one.
    static async Task FallbackSelectionMatchesTheConfiguredModel()
    {
        var section = await NewLoadedSection("deleted-model", "qwen2.5", "HY-MT-1.5");

        string? shown = section.OllamaModelComboBox.SelectedItem as string;
        Check(shown == "HY-MT-1.5", "expected the HY-MT fallback to be selected, got '" + (shown ?? "<null>") + "'");
        Check(SettingsService.Instance.OllamaModel == shown,
            "the page shows '" + shown + "' but requests still use '" + SettingsService.Instance.OllamaModel + "'");
        return;
    }

    // Guard: a configured model that is still installed stays selected and untouched.
    static async Task InstalledModelStaysSelected()
    {
        var section = await NewLoadedSection("qwen2.5", "qwen2.5", "HY-MT-1.5");

        Check(section.OllamaModelComboBox.SelectedItem as string == "qwen2.5",
            "the configured model was not selected");
        Check(SettingsService.Instance.OllamaModel == "qwen2.5", "the config was rewritten unnecessarily");
        return;
    }

    // Guard: Ollama offline (empty list) keeps the configured name selectable and unchanged.
    static async Task OfflineKeepsConfiguredModel()
    {
        var section = await NewLoadedSection("qwen2.5");

        Check(section.OllamaModelComboBox.SelectedItem as string == "qwen2.5",
            "offline fallback lost the configured model name");
        Check(SettingsService.Instance.OllamaModel == "qwen2.5", "the config was rewritten while Ollama was offline");
        return;
    }

    public static async Task<int> Run()
    {
        await Case("typing a model name does not commit each keystroke", TypingAModelNameDoesNotCommitEachKeystroke);
        await Case("picking from the list still switches and frees VRAM", PickingFromTheListStillSwitches);
        await Case("typed model name is committed on LostFocus", TypedModelNameIsCommittedOnLostFocus);
        await Case("fallback selection matches the configured model", FallbackSelectionMatchesTheConfiguredModel);
        await Case("installed model stays selected", InstalledModelStaysSelected);
        await Case("offline keeps the configured model", OfflineKeepsConfiguredModel);
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";

var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Distinct(StringComparer.OrdinalIgnoreCase);
var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("TranslationSectionProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
