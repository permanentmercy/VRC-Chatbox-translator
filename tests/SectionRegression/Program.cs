using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Extracts the current production members from Views/Sections/NetworkLogSection.xaml.cs instead of
// copying their implementations. WinUI is not required: TextBox / ToggleSwitch / DispatcherTimer /
// SettingsService / InboundService are replaced by deterministic stand-ins, so the port-editing
// behaviour is reproducible without binding a real socket.
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

string networkMembers = Select("Views/Sections/NetworkLogSection.xaml.cs", "NetworkLogSection",
    "HttpPortTextBox_TextChanged", "OscInPortTextBox_TextChanged", "ScheduleInboundRestart", "ApplyInboundPorts");

string source = """
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ------------------------------------------------------------------ UI stand-ins
public sealed class TextChangedEventArgs { }

public sealed class TextBox
{
    public string Text = string.Empty;
}

public sealed class ToggleSwitch
{
    public bool IsOn;
}

public sealed class DispatcherTimer
{
    public TimeSpan Interval { get; set; }
    public bool IsEnabled { get; private set; }
    public event EventHandler<object>? Tick;
    public void Start() => IsEnabled = true;
    public void Stop() => IsEnabled = false;
    // Drives one tick the way the dispatcher would: only when the timer is running, and the
    // timer stays running afterwards unless the handler stops it.
    public void FireTick()
    {
        if (!IsEnabled) return;
        Tick?.Invoke(this, EventArgs.Empty);
    }
}

// ------------------------------------------------------------------ service stand-ins
public sealed class InboundService
{
    public int HttpPort = 9002;
    public int OscPort = 9001;
    public int StartCalls;
    public List<(int Http, int Osc)> Started = new();
    // Production Start() tears down whatever is listening and rebinds on the given ports.
    public void Start(int httpPort = 9002, int oscPort = 9001)
    {
        StartCalls++;
        Started.Add((httpPort, oscPort));
        HttpPort = httpPort;
        OscPort = oscPort;
    }
    public void Reset() { StartCalls = 0; Started = new(); HttpPort = 9002; OscPort = 9001; }
}

public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();
    public bool InboundEnabled = true;
    public int SaveCalls;
    public readonly InboundService InboundService = new();
    public void SaveConfigDebounced() => SaveCalls++;
    public void Reset() { InboundEnabled = true; SaveCalls = 0; InboundService.Reset(); }
}

// ------------------------------------------------------------------ NetworkLogSection under test
public sealed class NetworkLogSection
{
    // Production starts true and flips it false at the end of Loaded; the handlers run after that.
    private bool _isInitializing = false;
    private DispatcherTimer? _inboundRestartTimer;
    public readonly TextBox HttpPortTextBox = new();
    public readonly TextBox OscInPortTextBox = new();
    public readonly ToggleSwitch InboundSwitch = new();

    // Mirrors the production constructor wiring (timer created there, Tick -> ApplyInboundPorts).
    public NetworkLogSection()
    {
        _inboundRestartTimer = new DispatcherTimer();
        _inboundRestartTimer.Tick += (s, e) =>
        {
            _inboundRestartTimer.Stop();
            ApplyInboundPorts();
        };
        InboundSwitch.IsOn = true;
    }

    public void TypeHttpPort(string text)
    {
        HttpPortTextBox.Text = text;
        HttpPortTextBox_TextChanged(this, new TextChangedEventArgs());
    }

    public void TypeOscPort(string text)
    {
        OscInPortTextBox.Text = text;
        OscInPortTextBox_TextChanged(this, new TextChangedEventArgs());
    }

    public void FirePendingRestart() => _inboundRestartTimer?.FireTick();
""" + networkMembers + """
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

    static NetworkLogSection NewSection()
    {
        SettingsService.Instance.Reset();
        return new NetworkLogSection();
    }

    static void TypeWhole(NetworkLogSection section, string digits)
    {
        for (int i = 1; i <= digits.Length; i++)
        {
            section.TypeHttpPort(digits.Substring(0, i));
        }
    }

    // Defect: the port boxes are wired to TextChanged, so every keystroke is treated as "the port is
    // final". Typing 18080 walks through 1808, which is a valid port -> the live listener is torn down
    // and rebound on 1808 (broadcasting "已就绪 (HTTP: 1808)") before being torn down again.
    static Task TypingAPortRestartsTheListenerOnlyOnce()
    {
        var section = NewSection();
        var service = SettingsService.Instance.InboundService;

        TypeWhole(section, "18080");

        Check(service.StartCalls == 0,
            "the listener was restarted " + service.StartCalls + " time(s) while the port was still being typed (" +
            string.Join(", ", service.Started.Select(s => s.Http)) + ")");
        Check(service.HttpPort == 18080, "the pending port was not recorded (HttpPort=" + service.HttpPort + ")");

        section.FirePendingRestart();

        Check(service.StartCalls == 1,
            "expected exactly 1 restart after the edit settled, got " + service.StartCalls);
        Check(service.Started[0].Http == 18080, "restarted on port " + service.Started[0].Http + " instead of 18080");
        return Task.CompletedTask;
    }

    // Guard: a partial/invalid port must never touch the listener.
    static Task InvalidPortDoesNotRestart()
    {
        var section = NewSection();
        var service = SettingsService.Instance.InboundService;

        section.TypeHttpPort("18");
        section.FirePendingRestart();

        Check(service.StartCalls == 0, "restarted on an invalid port");
        Check(service.HttpPort == 9002, "an invalid port was written to the service (" + service.HttpPort + ")");
        return Task.CompletedTask;
    }

    // Guard: with the inbound switch off nothing may be restarted.
    static Task SwitchOffDoesNotRestart()
    {
        var section = NewSection();
        var service = SettingsService.Instance.InboundService;
        SettingsService.Instance.InboundEnabled = false;
        section.InboundSwitch.IsOn = false;

        TypeWhole(section, "18080");
        section.FirePendingRestart();

        Check(service.StartCalls == 0, "restarted while the inbound switch was off");
        return Task.CompletedTask;
    }

    // Guard: the OSC receive port goes through the same debounced path.
    static Task TypingOscPortRestartsOnce()
    {
        var section = NewSection();
        var service = SettingsService.Instance.InboundService;

        for (int i = 1; i <= 5; i++) section.TypeOscPort(("17001").Substring(0, i));
        Check(service.StartCalls == 0, "OSC port typing restarted the listener " + service.StartCalls + " time(s)");

        section.FirePendingRestart();
        Check(service.StartCalls == 1, "expected 1 restart, got " + service.StartCalls);
        Check(service.Started[0].Osc == 17001, "restarted on OSC port " + service.Started[0].Osc);
        return Task.CompletedTask;
    }

    // Guard: a value that equals the current port must not schedule anything at all.
    static Task UnchangedPortDoesNotRestart()
    {
        var section = NewSection();
        var service = SettingsService.Instance.InboundService;

        section.TypeHttpPort("9002");
        section.FirePendingRestart();

        Check(service.StartCalls == 0, "restarted although the port did not change");
        return Task.CompletedTask;
    }

    // Guard: if the switch is turned off while a restart is pending, the pending tick must not
    // resurrect the listener the user just stopped.
    static Task PendingRestartRespectsSwitchTurnedOff()
    {
        var section = NewSection();
        var service = SettingsService.Instance.InboundService;

        TypeWhole(section, "18080");
        SettingsService.Instance.InboundEnabled = false;
        section.InboundSwitch.IsOn = false;

        section.FirePendingRestart();

        Check(service.StartCalls == 0,
            "a pending restart re-enabled the inbound service after the user turned it off");
        return Task.CompletedTask;
    }

    public static async Task<int> Run()
    {
        await Case("typing a port restarts the listener only once", TypingAPortRestartsTheListenerOnlyOnce);
        await Case("invalid port never restarts", InvalidPortDoesNotRestart);
        await Case("switch off never restarts", SwitchOffDoesNotRestart);
        await Case("typing the OSC port restarts once", TypingOscPortRestartsOnce);
        await Case("unchanged port never restarts", UnchangedPortDoesNotRestart);
        await Case("pending restart respects the switch being turned off", PendingRestartRespectsSwitchTurnedOff);
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";

var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Distinct(StringComparer.OrdinalIgnoreCase);
var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("SectionProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
