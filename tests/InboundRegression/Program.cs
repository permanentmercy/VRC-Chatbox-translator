using System.Net;
using System.Reflection;
using System.Text;
using VrcChatboxDemo.Services;

// External-interface regression: exercises the real OSC parser, the JSON value coercion and
// the avoidance state machine. The live HTTP section is optional because http.sys cannot always
// be created inside restricted/sandboxed hosts (HttpListener.SetupV2Config -> invalid handle).
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Case(string name, Func<Task> body)
{
    try { body().GetAwaiter().GetResult(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

var inbound = new InboundService();
var parser = typeof(InboundService).GetMethod("ParseOscOrPlainMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
string Parse(params byte[] data) => (string)parser.Invoke(inbound, new object[] { data })!;
string ParseText(string text) => Parse(Encoding.UTF8.GetBytes(text));

// OSC helpers: address / type tags / arguments, each 4-byte aligned.
static byte[] OscString(string value)
{
    var bytes = Encoding.UTF8.GetBytes(value);
    int total = (bytes.Length / 4 + 1) * 4;
    var buffer = new byte[total];
    Array.Copy(bytes, buffer, bytes.Length);
    return buffer;
}
static byte[] OscConcat(params byte[][] chunks)
{
    var all = new List<byte>();
    foreach (var chunk in chunks) all.AddRange(chunk);
    return all.ToArray();
}

Case("osc string argument is extracted", () =>
{
    var packet = OscConcat(OscString("/chatbox/input"), OscString(",s"), OscString("hello"));
    Check(Parse(packet) == "hello", "got '" + Parse(packet) + "'");
    return Task.CompletedTask;
});

Case("osc avatar/tracking/input paths are dropped", () =>
{
    foreach (var address in new[] { "/avatar/parameters/Viseme", "/tracking/head", "/input/Jump" })
    {
        var packet = OscConcat(OscString(address), OscString(",s"), OscString("leak"));
        Check(Parse(packet) == string.Empty, address + " leaked text");
    }
    return Task.CompletedTask;
});

Case("osc int argument before string is skipped correctly", () =>
{
    var tag = new byte[4];
    Encoding.ASCII.GetBytes(",is").CopyTo(tag, 0);
    var packet = OscConcat(OscString("/chatbox/input"), tag, new byte[] { 7, 0, 0, 0 }, OscString("after-int"));
    Check(Parse(packet) == "after-int", "got '" + Parse(packet) + "'");
    return Task.CompletedTask;
});

Case("osc float argument before string is skipped correctly", () =>
{
    var tag = new byte[4];
    Encoding.ASCII.GetBytes(",fs").CopyTo(tag, 0);
    var packet = OscConcat(OscString("/chatbox/input"), tag, new byte[] { 0, 0, 0x80, 0x3f }, OscString("after-float"));
    Check(Parse(packet) == "after-float", "got '" + Parse(packet) + "'");
    return Task.CompletedTask;
});

Case("osc packet without string argument yields nothing", () =>
{
    var packet = OscConcat(OscString("/chatbox/typing"), OscString(",T"));
    Check(Parse(packet) == string.Empty, "expected empty, got '" + Parse(packet) + "'");
    return Task.CompletedTask;
});

Case("osc address only never falls back to plain text", () =>
{
    var packet = OscString("/avatar/parameters/Thing");
    Check(Parse(packet) == string.Empty, "address was decoded as chat text");
    return Task.CompletedTask;
});

Case("plain utf8 payload is passed through", () =>
{
    Check(ParseText("hello") == "hello", "plain text lost");
    Check(ParseText("hello\u0000\u0000") == "hello", "trailing nulls not trimmed");
    return Task.CompletedTask;
});

Case("plain payload that looks like a telemetry path is dropped", () =>
{
    Check(ParseText("/avatar/parameters/x") == string.Empty, "telemetry path leaked");
    return Task.CompletedTask;
});

Case("empty packet yields empty text", () =>
{
    Check(Parse(Array.Empty<byte>()) == string.Empty, "empty packet produced text");
    return Task.CompletedTask;
});

Case("json coercion accepts non-string scalars", () =>
{
    var coerce = typeof(InboundService).GetMethod("GetJsonStringValue", BindingFlags.Static | BindingFlags.NonPublic)!;
    string Coerce(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return (string)coerce.Invoke(null, new object[] { doc.RootElement })!;
    }
    Check(Coerce("123") == "123", "number: " + Coerce("123"));
    Check(Coerce("true") == "true", "bool: " + Coerce("true"));
    Check(Coerce("\"text\"") == "text", "string: " + Coerce("\"text\""));
    Check(Coerce("null") == string.Empty, "null: " + Coerce("null"));
    Check(Coerce("{\"a\":1}") == "{\"a\":1}", "object: " + Coerce("{\"a\":1}"));
    return Task.CompletedTask;
});

Case("avoidance clamps out-of-range seconds", () =>
{
    using var persistent = new PersistentTextService();
    foreach (int requested in new[] { 0, -100, 1 })
    {
        persistent.PauseAvoidance(requested);
        Check(persistent.IsInAvoidance(out _, out int remaining), "pause(" + requested + ") not applied");
        Check(remaining >= 1 && remaining <= 300, "pause(" + requested + ") remaining=" + remaining);
    }
    persistent.PauseAvoidance(9999);
    Check(persistent.IsInAvoidance(out _, out int capped) && capped <= 300, "upper clamp missing: " + capped);
    persistent.ResumeAvoidance();
    Check(!persistent.IsInAvoidance(out _, out _), "resume did not clear avoidance");
    return Task.CompletedTask;
});

// ---- optional live HTTP section -------------------------------------------------------
bool httpAvailable = false;
foreach (int candidate in new[] { 19160, 19170, 19180 })
{
    inbound.Start(candidate, candidate + 1);
    if (inbound.IsRunning)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await probe.GetAsync("http://127.0.0.1:" + candidate + "/api/variables");
            httpAvailable = response.IsSuccessStatusCode;
        }
        catch { httpAvailable = false; }
        if (!httpAvailable) inbound.Stop();
        if (httpAvailable) break;
    }
    inbound.Stop();
}

if (!httpAvailable)
{
    Console.WriteLine("SKIP live HTTP routes (http.sys unavailable in this host)");
}
else
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    string baseUrl = "http://127.0.0.1:" + inbound.HttpPort;
    var variables = new VariableService();
    inbound.VariableService = variables;

    Case("http variables listing is json", async () =>
    {
        string body = await client.GetStringAsync(baseUrl + "/api/variables");
        Check(body.Contains("\"success\":true"), "listing: " + body);
    });

    Case("http variable set applies value", async () =>
    {
        await client.GetStringAsync(baseUrl + "/api/variables/set?name=k&value=v");
        Check(variables.GetVariableValue("k") == "v", "value not stored");
    });

    Case("http numeric json value is accepted", async () =>
    {
        using var content = new StringContent("{\"name\":\"n\",\"value\":42}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync(baseUrl + "/api/variables", content);
        Check(response.IsSuccessStatusCode, "status " + (int)response.StatusCode);
        Check(variables.GetVariableValue("n") == "42", "value=" + variables.GetVariableValue("n"));
    });

    Case("http cors preflight is allowed", async () =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, baseUrl + "/api/variables");
        using var response = await client.SendAsync(request);
        Check(response.IsSuccessStatusCode, "status " + (int)response.StatusCode);
        Check(response.Headers.Contains("Access-Control-Allow-Origin"), "missing CORS header");
    });

    inbound.Stop();
}

Console.WriteLine("inbound regressions: " + passed + " passed, " + failures.Count + " failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
