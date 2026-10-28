using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Extracts the current production members from MainWindow.xaml.cs and Views/ImmersiveWindow.xaml.cs
// instead of copying their implementations. WinUI is not required: NavigationView / Frame / AppWindow /
// DispatcherQueue / HotkeyService / SettingsService are replaced by deterministic stand-ins, so both
// defects reproduce without a window, a tray icon or an actual Windows shutdown.
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
    // WithoutLeadingTrivia drops the leading trivia, which can contain a #endregion directive that
    // would otherwise become a stray preprocessor line in the generated source.
    return string.Join(Environment.NewLine, members.Select(m => m.WithoutLeadingTrivia().ToFullString()));
}

const string mainWindowPath = "MainWindow.xaml.cs";
const string immersiveWindowPath = "Views/ImmersiveWindow.xaml.cs";

// _hWnd / _isReorderingNav / NavView / ContentFrame / ChatNavItem are declared by the stand-in class:
// they are pure UI plumbing, while the members below hold the logic under test.
string mainWindowMembers = Select(mainWindowPath, "MainWindow",
    "OnHotkeyPressed", "NavView_SelectionChanged", "NavigateToTag", "GetPageTypeForTag");
string immersiveMembers = Select(immersiveWindowPath, "ImmersiveWindow",
    "HandleImmersiveWindowClosing");

string source = """
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// ------------------------------------------------------------------ shared stand-ins
public sealed class AppWindow
{
    public int HideCalls;
    public void Hide() => HideCalls++;
}

public sealed class AppWindowClosingEventArgs
{
    public bool Cancel { get; set; }
}

public sealed class HotkeyService
{
    public bool Foreground;
    public int RestoreCalls;
    public int ActivateCalls;
    public bool IsWindowForeground(IntPtr hwnd) => Foreground;
    public bool RestorePreviousWindowFocus() { RestoreCalls++; return true; }
    public void ActivateWindow(IntPtr? specificHwnd = null) => ActivateCalls++;
}

public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();
    public bool IsImmersiveMode;
    public readonly HotkeyService HotkeyService = new();
    public int SuppressTypingAnimationCalls;
    public List<bool> ImmersiveModeCalls = new();
    public void SuppressTypingAnimation() => SuppressTypingAnimationCalls++;
    public void SetImmersiveMode(bool enable) { ImmersiveModeCalls.Add(enable); IsImmersiveMode = enable; }
    public void Reset()
    {
        IsImmersiveMode = false;
        SuppressTypingAnimationCalls = 0;
        ImmersiveModeCalls = new();
        HotkeyService.Foreground = false;
        HotkeyService.RestoreCalls = 0;
        HotkeyService.ActivateCalls = 0;
        ChatPage.FocusInputCalls = 0;
    }
}

public sealed class DispatcherQueue
{
    // Production posts to the UI thread; the stand-in runs inline so ordering stays deterministic.
    public bool TryEnqueue(Action callback) { callback(); return true; }
}

// ------------------------------------------------------------------ navigation stand-ins
public class NavigationTransitionInfo { }
public sealed class SuppressNavigationTransitionInfo : NavigationTransitionInfo { }

public sealed class NavigationViewItem
{
    public object? Tag { get; set; }
}

public sealed class NavigationViewSelectionChangedEventArgs
{
    public NavigationViewSelectionChangedEventArgs(object? selectedItem) { SelectedItem = selectedItem; }
    public object? SelectedItem { get; }
}

public sealed class NavigationView
{
    object? selected;
    public event EventHandler<NavigationViewSelectionChangedEventArgs>? SelectionChanged;
    public object? SelectedItem
    {
        get => selected;
        set
        {
            if (ReferenceEquals(selected, value)) return;
            selected = value;
            SelectionChanged?.Invoke(this, new NavigationViewSelectionChangedEventArgs(value));
        }
    }
}

public sealed class ChatPage
{
    public static int FocusInputCalls;
    public void FocusInput() => FocusInputCalls++;
}
public sealed class SpeechSettingsPage { }
public sealed class TranslationSettingsPage { }
public sealed class TtsSettingsPage { }
public sealed class HotkeySettingsPage { }
public sealed class InteractionSettingsPage { }
public sealed class NetworkLogSettingsPage { }

public sealed class Frame
{
    public Type? CurrentSourcePageType { get; private set; }
    public object? Content { get; private set; }
    public int NavigateCalls;
    public List<Type> NavigatedTypes = new();
    public void Navigate(Type pageType, object? parameter, NavigationTransitionInfo? info)
    {
        NavigateCalls++;
        NavigatedTypes.Add(pageType);
        CurrentSourcePageType = pageType;
        Content = Activator.CreateInstance(pageType);
    }
    public void ResetCounters() { NavigateCalls = 0; NavigatedTypes = new(); }
}

// ------------------------------------------------------------------ MainWindow under test
public sealed class MainWindow
{
    private bool _isReorderingNav = false;
    private readonly IntPtr _hWnd = new IntPtr(42);
    public readonly NavigationView NavView = new();
    public readonly Frame ContentFrame = new();
    public readonly NavigationViewItem ChatNavItem = new() { Tag = "chat" };
    public readonly DispatcherQueue DispatcherQueue = new();
    // MainWindow.xaml wires SelectionChanged="NavView_SelectionChanged".
    public MainWindow() => NavView.SelectionChanged += (s, e) => NavView_SelectionChanged((NavigationView)s!, e);
    public void PressHotkey() => OnHotkeyPressed();
""" + mainWindowMembers + """
}

// ------------------------------------------------------------------ ImmersiveWindow under test
public sealed class ImmersiveWindow
{
    public void Closing(AppWindow sender, AppWindowClosingEventArgs args, bool systemShuttingDown)
        => HandleImmersiveWindowClosing(sender, args, systemShuttingDown);
""" + immersiveMembers + """
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

    static MainWindow NewWindowOn(Type page)
    {
        SettingsService.Instance.Reset();
        var window = new MainWindow();
        window.ContentFrame.Navigate(page, null, null);
        window.ContentFrame.ResetCounters();
        ChatPage.FocusInputCalls = 0;
        return window;
    }

    // Defect A: the overlay window refused every close, including the ones Windows issues while
    // shutting down / signing out -> the app blocks logoff. MainWindow already guards this exact
    // condition (GetSystemMetrics(SM_SHUTTINGDOWN) || Environment.HasShutdownStarted).
    static Task ShutdownMustNotCancelOverlayClose()
    {
        SettingsService.Instance.Reset();
        var overlay = new ImmersiveWindow();
        var window = new AppWindow();
        var args = new AppWindowClosingEventArgs();

        overlay.Closing(window, args, systemShuttingDown: true);

        Check(!args.Cancel, "overlay still cancels its close during OS shutdown, which blocks shutdown/logoff");
        Check(window.HideCalls == 0, "overlay hid itself during shutdown (" + window.HideCalls + " hide call(s))");
        Check(SettingsService.Instance.ImmersiveModeCalls.Count == 0,
            "overlay toggled immersive mode during shutdown, which makes App re-Show the closing main window");
        return Task.CompletedTask;
    }

    // Guard: Alt+F4 on the overlay must still hide it and leave immersive mode.
    static Task AltF4StillHidesOverlay()
    {
        SettingsService.Instance.Reset();
        var overlay = new ImmersiveWindow();
        var window = new AppWindow();
        var args = new AppWindowClosingEventArgs();

        overlay.Closing(window, args, systemShuttingDown: false);

        Check(args.Cancel, "Alt+F4 must be cancelled, otherwise AppWindow.Show() throws on the next immersive toggle");
        Check(window.HideCalls == 1, "overlay was not hidden (hide calls: " + window.HideCalls + ")");
        Check(SettingsService.Instance.ImmersiveModeCalls.Count == 1 && !SettingsService.Instance.ImmersiveModeCalls[0],
            "Alt+F4 must leave immersive mode exactly once");
        return Task.CompletedTask;
    }

    // Defect B: OnHotkeyPressed sets NavView.SelectedItem (which already navigates through
    // NavView_SelectionChanged -> NavigateToTag) and then navigates to ChatPage a second time.
    static Task HotkeyNavigatesToChatExactlyOnce()
    {
        var window = NewWindowOn(typeof(SpeechSettingsPage));

        window.PressHotkey();

        Check(window.ContentFrame.NavigateCalls == 1,
            "expected exactly 1 navigation to the chat page, got " + window.ContentFrame.NavigateCalls +
            " (" + string.Join(", ", window.ContentFrame.NavigatedTypes.Select(t => t.Name)) + ")");
        Check(window.ContentFrame.NavigatedTypes[0] == typeof(ChatPage), "hotkey did not end up on the chat page");
        Check(ChatPage.FocusInputCalls == 1, "expected FocusInput once, got " + ChatPage.FocusInputCalls);
        return Task.CompletedTask;
    }

    // Guard: already on the chat page -> no redundant navigation, but the input must be focused.
    static Task HotkeyOnChatPageDoesNotRenavigate()
    {
        var window = NewWindowOn(typeof(ChatPage));

        window.PressHotkey();

        Check(window.ContentFrame.NavigateCalls == 0,
            "re-navigated while already on the chat page " + window.ContentFrame.NavigateCalls + " time(s)");
        Check(ChatPage.FocusInputCalls == 1, "expected FocusInput once, got " + ChatPage.FocusInputCalls);
        return Task.CompletedTask;
    }

    // Guard: window already in the foreground -> hand focus back, never navigate.
    static Task HotkeyWhenForegroundRestoresPreviousWindow()
    {
        var window = NewWindowOn(typeof(SpeechSettingsPage));
        SettingsService.Instance.HotkeyService.Foreground = true;

        window.PressHotkey();

        Check(SettingsService.Instance.HotkeyService.RestoreCalls == 1, "previous window focus was not restored");
        Check(SettingsService.Instance.HotkeyService.ActivateCalls == 0, "window was re-activated while already foreground");
        Check(window.ContentFrame.NavigateCalls == 0, "navigated while the window was already foreground");
        return Task.CompletedTask;
    }

    // Guard: in immersive mode the overlay owns the hotkey; the main window must stay untouched.
    static Task HotkeyInImmersiveModeDoesNothing()
    {
        var window = NewWindowOn(typeof(SpeechSettingsPage));
        SettingsService.Instance.IsImmersiveMode = true;

        window.PressHotkey();

        Check(window.ContentFrame.NavigateCalls == 0, "navigated while in immersive mode");
        Check(SettingsService.Instance.HotkeyService.ActivateCalls == 0, "activated the hidden main window in immersive mode");
        Check(ChatPage.FocusInputCalls == 0, "focused the hidden chat page in immersive mode");
        return Task.CompletedTask;
    }

    public static async Task<int> Run()
    {
        await Case("shutdown must not cancel the overlay close", ShutdownMustNotCancelOverlayClose);
        await Case("Alt+F4 still hides the overlay and leaves immersive mode", AltF4StillHidesOverlay);
        await Case("hotkey navigates to the chat page exactly once", HotkeyNavigatesToChatExactlyOnce);
        await Case("hotkey on the chat page does not re-navigate", HotkeyOnChatPageDoesNotRenavigate);
        await Case("hotkey when foreground restores the previous window", HotkeyWhenForegroundRestoresPreviousWindow);
        await Case("hotkey in immersive mode leaves the main window alone", HotkeyInImmersiveModeDoesNothing);
        Console.WriteLine($"RESULT {total-failures}/{total} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }
}
""";

var referencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Distinct(StringComparer.OrdinalIgnoreCase);
var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("UiDeepProductionRegression", [CSharpSyntaxTree.ParseText(source)], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join(Environment.NewLine, result.Diagnostics));
var assembly = Assembly.Load(output.ToArray());
return await (Task<int>)assembly.GetType("Cases")!.GetMethod("Run")!.Invoke(null, null)!;
