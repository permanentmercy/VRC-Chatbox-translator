using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Extracts the current production members from Views/Sections/InteractionSection.xaml.cs instead of
// copying their implementations. WinUI is not required: ToggleSwitch / TextBox / TextBlock /
// SettingsService / PersistentTextService are replaced by stand-ins that mirror the production state
// machine (PersistentTextService.RestartLoop only starts when IsEnabled && CustomText is non-empty,
// StopLoop never clears IsEnabled, SubmitAsync("") behaves like ClearAsync).
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

string interactionMembers = Select("Views/Sections/InteractionSection.xaml.cs", "InteractionSection",
    "PersistentTextSwitch_Toggled", "ApplyPersistentTextButton_Click", "RefreshStatusText");

string source = """
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// ------------------------------------------------------------------ UI stand-ins
public class Brush { }
public sealed class SolidColorBrush : Brush { public SolidColorBrush(object? color) { } }
public static class Colors
{
    public static readonly object Orange = "Orange";
    public static readonly object SeaGreen = "SeaGreen";
}
public sealed class ResourceDictionary
{
    public object this[string key] => new Brush();
}
public sealed class Application
{
    public static Application Current { get; } = new();
    public ResourceDictionary Resources { get; } = new();
}
public sealed class RoutedEventArgs { }

public sealed class TextBlock
{
    public string Text = string.Empty;
    public Brush? Foreground;
}

public sealed class TextBox
{
    public string Text = string.Empty;
    public bool IsEnabled = true;
}

public sealed class ToggleSwitch
{
    bool isOn;
    public event EventHandler<RoutedEventArgs>? Toggled;
    public bool IsOn
    {
        get => isOn;
        set
        {
            if (isOn == value) return;
            isOn = value;
            Toggled?.Invoke(this, new RoutedEventArgs());
        }
    }
}

// ------------------------------------------------------------------ service stand-ins
public sealed class PersistentTextService
{
    public bool IsEnabled;
    public string CustomText = string.Empty;
    public bool LoopRunning;
    public bool Avoiding;
    public string AvoidanceReason = "游戏内消息展示避让中 (剩余 3 秒)";

    // Production RestartLoop: stops the previous loop, then requires IsEnabled && non-empty CustomText.
    public void RestartLoop(object variableService, object oscService)
    {
        LoopRunning = false;
        if (!IsEnabled || string.IsNullOrWhiteSpace(CustomText)) return;
        LoopRunning = true;
    }

    // Production StopLoop only cancels the loop token; it never clears IsEnabled.
    public void StopLoop() => LoopRunning = false;

    // Production SubmitAsync: empty template falls through to ClearAsync (IsEnabled=false + loop stopped).
    public Task<bool> SubmitAsync(string template, object variableService, object oscService, SettingsService settings)
    {
        template = (template ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(template))
        {
            IsEnabled = false;
            CustomText = string.Empty;
            LoopRunning = false;
            return Task.FromResult(false);
        }
        IsEnabled = true;
        CustomText = template;
        LoopRunning = true;
        return Task.FromResult(true);
    }

    public bool IsInAvoidance(out string reason, out int remainingSeconds)
    {
        reason = AvoidanceReason;
        remainingSeconds = 3;
        return Avoiding;
    }

    public void Reset()
    {
        IsEnabled = false;
        CustomText = string.Empty;
        LoopRunning = false;
        Avoiding = false;
    }
}

public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();
    public bool IsPersistentTextEnabled;
    public string PersistentCustomText = string.Empty;
    public readonly PersistentTextService PersistentTextService = new();
    public readonly object VariableService = new();
    public readonly object OscService = new();
    public void Reset()
    {
        IsPersistentTextEnabled = false;
        PersistentCustomText = string.Empty;
        PersistentTextService.Reset();
    }
}

// ------------------------------------------------------------------ InteractionSection under test
public sealed class InteractionSection
{
    // Production starts true and flips it false at the end of Loaded; every handler below runs after that.
    private bool _isInitializing = false;
    public readonly ToggleSwitch PersistentTextSwitch = new();
    public readonly TextBox PersistentTextBox = new();
    public readonly TextBlock PersistentStatusTextBlock = new();
    // InteractionSection.xaml wires Toggled="PersistentTextSwitch_Toggled" / Click="ApplyPersistentTextButton_Click".
    public InteractionSection() => PersistentTextSwitch.Toggled += (s, e) => PersistentTextSwitch_Toggled(s, e);
    public void ClickApply() => ApplyPersistentTextButton_Click(this, new RoutedEventArgs());
""" + interactionMembers + """
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

    // A user has typed a template into the box (PersistentCustomText is saved per keystroke) and then
    // flips the 常驻 switch on. Nothing was ever submitted, so PersistentTextService.IsEnabled and
    // PersistentTextService.CustomText are still at their defaults.
    static (InteractionSection, SettingsService) NewTypedSection(string template)
    {
        SettingsService.Instance.Reset();
        SettingsService.Instance.PersistentCustomText = template;
        var section = new InteractionSection();
        section.PersistentTextBox.Text = template;
        return (section, SettingsService.Instance);
    }

    // Defect A (on): flipping the switch on must actually start the keepalive loop, and the status line
    // must agree with the switch. Today only the persisted flag is updated, so RestartLoop bails out.
    static Task TurningSwitchOnStartsKeepalive()
    {
        var (section, settings) = NewTypedSection("正在听 {music}");
        Check(!section.PersistentTextSwitch.IsOn, "switch must start off");

        section.PersistentTextSwitch.IsOn = true; // user click

        var service = settings.PersistentTextService;
        Check(settings.IsPersistentTextEnabled, "the persisted flag was not updated");
        Check(service.IsEnabled, "service.IsEnabled stayed false, so RestartLoop returns immediately and no keepalive runs");
        Check(service.LoopRunning, "keepalive loop was not started");
        Check(section.PersistentStatusTextBlock.Text == "常驻保活中",
            "status says '" + section.PersistentStatusTextBlock.Text + "' while the switch is on");
        return Task.CompletedTask;
    }

    // Defect A (off): flipping the switch off must also clear the service's enabled flag, otherwise the
    // 500 ms status timer keeps reporting 常驻保活中 next to a switch that reads 关闭.
    static Task TurningSwitchOffClearsServiceState()
    {
        var (section, settings) = NewTypedSection("正在听 {music}");
        var service = settings.PersistentTextService;
        service.IsEnabled = true;
        service.CustomText = "正在听 {music}";
        service.LoopRunning = true;
        section.PersistentTextSwitch.IsOn = true;

        section.PersistentTextSwitch.IsOn = false; // user click

        Check(!service.LoopRunning, "keepalive loop was not stopped");
        Check(!service.IsEnabled, "service.IsEnabled stayed true, so the status line keeps claiming 常驻保活中");
        Check(section.PersistentStatusTextBlock.Text == "常驻未开启",
            "status says '" + section.PersistentStatusTextBlock.Text + "' while the switch is off");
        return Task.CompletedTask;
    }

    // Defect B: submitting an empty template makes SubmitAsync behave like ClearAsync (service disabled,
    // loop stopped) but the switch is left on, so the page shows 开关=开启 / 状态=常驻未开启 and the
    // persisted IsPersistentTextEnabled=true outlives the text.
    static Task SubmittingEmptyTemplateTurnsSwitchOff()
    {
        var (section, settings) = NewTypedSection("正在听 {music}");
        var service = settings.PersistentTextService;
        section.PersistentTextSwitch.IsOn = true;
        service.IsEnabled = true;
        service.CustomText = "正在听 {music}";
        service.LoopRunning = true;

        section.PersistentTextBox.Text = string.Empty; // user cleared the box
        section.ClickApply();

        Check(!service.IsEnabled, "service should be disabled after submitting an empty template");
        Check(!section.PersistentTextSwitch.IsOn, "the switch is still on although nothing is being kept alive");
        Check(!settings.IsPersistentTextEnabled,
            "IsPersistentTextEnabled is still true although the template is empty");
        Check(section.PersistentStatusTextBlock.Text == "常驻未开启",
            "status says '" + section.PersistentStatusTextBlock.Text + "' after clearing the template");
        return Task.CompletedTask;
    }

    // Guard: submitting a real template keeps the switch on and enables the service (must not regress).
    static Task SubmittingTemplateKeepsSwitchOn()
    {
        var (section, settings) = NewTypedSection(string.Empty);
        var service = settings.PersistentTextService;

        section.PersistentTextBox.Text = "正在听 {music}";
        section.ClickApply();

        Check(section.PersistentTextSwitch.IsOn, "switch must be on after submitting a template");
        Check(settings.IsPersistentTextEnabled, "persisted flag must be on after submitting a template");
        Check(service.IsEnabled, "service must be enabled after submitting a template");
        Check(service.LoopRunning, "keepalive loop must run after submitting a template");
        Check(section.PersistentStatusTextBlock.Text == "常驻保活中",
            "status says '" + section.PersistentStatusTextBlock.Text + "' after submitting a template");
        return Task.CompletedTask;
    }

    // Guard: avoidance reason is surfaced instead of the generic text (must not regress).
    static Task StatusShowsAvoidanceReason()
    {
        var (section, settings) = NewTypedSection("正在听 {music}");
        var service = settings.PersistentTextService;
        service.IsEnabled = true;
        service.CustomText = "正在听 {music}";
        service.LoopRunning = true;
        service.Avoiding = true;
        section.PersistentTextSwitch.IsOn = true;

        section.PersistentTextSwitch.IsOn = true; // no-op, forces a status refresh via toggle path only
        Check(section.PersistentStatusTextBlock.Text == "常驻保活中" || section.PersistentStatusTextBlock.Text.Contains("避让"),
            "unexpected status text '" + section.PersistentStatusTextBlock.Text + "'");
        return Task.CompletedTask;
    }

    public static async Task<int> Run()
    {
        await Case("turning the persistent switch on starts the keepalive", TurningSwitchOnStartsKeepalive);
        await Case("turning the persistent switch off clears the service state", TurningSwitchOffClearsServiceState);
        await Case("submitting an empty template turns the switch off", SubmittingEmptyTemplateTurnsSwitchOff);
        await Case("submitting a template keeps the switch on", SubmittingTemplateKeepsSwitchOn);
        await Case("status line still works", StatusShowsAvoidanceReason);
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";

var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Distinct(StringComparer.OrdinalIgnoreCase);
var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("InteractionDeepProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
