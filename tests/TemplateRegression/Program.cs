using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using VrcChatboxDemo.Services;

int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
void Case(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
async Task AsyncCase(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
var now = DateTime.UtcNow;
var active = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) { ["language"] = now.AddMinutes(1) };
var blocks = new Dictionary<string, DateTime>();
string Prepare(string template) => PersistentTextService.PrepareTemplateForRender(template, active, blocks, DateTime.MinValue, now);

Case("literal URL and punctuation remain intact", () =>
{
    const string text = "https://example.com/a -- b,,c / d";
    Check(Prepare(text) == text, "Template cleanup modified ordinary text: " + Prepare(text));
});
Case("expired block cleanup does not corrupt unrelated URL", () =>
{
    const string text = "https://example.com | [gone] | tail";
    Check(Prepare(text) == "https://example.com | tail", Prepare(text));
});
Case("expired edge blocks leave no separators", () =>
{
    Check(Prepare("[gone] | middle | [gone]") == "middle", Prepare("[gone] | middle | [gone]"));
});
Case("expired adjacent blocks are removed", () => Check(Prepare("a | [gone] | [gone] | b") == "a | b", Prepare("a | [gone] | [gone] | b")));
Case("literal leading and trailing punctuation survive", () => Check(Prepare("-literal-") == "-literal-", Prepare("-literal-")));
foreach (string alias in new[] { "lang", "caption_lang", "subtitle_lang", "字幕语言" })
{
    Case("transient lifetime resolves alias " + alias, () => Check(Prepare("[{" + alias + "}]") == "{" + alias + "}", "Alias block unexpectedly hidden"));
}
Case("custom hyphenated variable renders", () =>
{
    var variables = new VariableService();
    variables.SetVariable("heart-rate", "72");
    Check(variables.Render("{heart-rate}") == "72", variables.Render("{heart-rate}"));
});
Case("alias lookup and variable detection agree", () =>
{
    var variables = new VariableService();
    variables.SetVariable("language", "English");
    Check(variables.Render("{字幕语言}") == "English", "Alias lookup failed");
    Check(variables.UsesVariable("{language}", "字幕语言"), "Alias detection is not symmetric");
});
Case("explicit newline remains supported", () =>
{
    var variables = new VariableService();
    Check(variables.Render(Prepare(@"a{\n}b")) == "a\nb", "Explicit newline lost");
});
Case("emoji truncation never splits surrogate pair", () =>
{
    var variables = new VariableService();
    string text = variables.Render(new string('a', 143) + "😀tail");
    Check(text.Length == 143 && !char.IsHighSurrogate(text[^1]), "Split surrogate pair");
});
Case("block duration override remains supported", () =>
{
    var expiry = new Dictionary<string, DateTime> { ["[##5 hello]"] = now.AddSeconds(5) };
    Check(PersistentTextService.PrepareTemplateForRender("[##5 hello]", active, expiry, DateTime.MinValue, now) == "hello", "Block override lost");
});
await AsyncCase("canonical language update reactivates submitted alias block", async () =>
{
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var osc = new OscService { Port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port };
    using var persistent = new PersistentTextService();
    var variables = new VariableService();
    variables.SetVariable("language", "English");
    Check(await persistent.SubmitAsync("[{字幕语言}]", variables, osc, new SettingsService()), "Submit failed");
    persistent.StopLoop();
    await receiver.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    var field = typeof(PersistentTextService).GetField("_varTransientUntil", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var expiries = (Dictionary<string, DateTime>)field.GetValue(persistent)!;
    foreach (var key in expiries.Keys.ToArray()) expiries[key] = DateTime.UtcNow.AddMinutes(-1);
    variables.SetVariable("language", "日本語");
    await persistent.OnVariableUpdatedAsync("language", variables, osc);
    var packet = await receiver.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    byte[] expected = OscService.BuildChatboxInputPacket("/chatbox/input", "日本語", true, false);
    Check(packet.Buffer.SequenceEqual(expected), "Updated alias was not sent as rendered text");
});
Console.WriteLine($"Template regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
