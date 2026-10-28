using System.Net;
using System.Net.Sockets;
using System.Text;
using TypingRegressionTests;
using VrcChatboxDemo.Services;
using VrcChatboxDemo.Services.Messaging;

// TypingIndicator 回归：真实 OscService + 真实 UDP 回环，断言实际发出的 OSC 报文。
// 打字状态机原先埋在 SettingsService 里，只能靠读源码确认；这里改为行为断言。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task Case(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

static (string Address, string Tags, string? Text) Decode(byte[] data)
{
    int i = 0;
    string ReadString()
    {
        int start = i;
        while (i < data.Length && data[i] != 0) i++;
        string s = Encoding.UTF8.GetString(data, start, i - start);
        while (i < data.Length && data[i] == 0) i++;
        return s;
    }
    string address = ReadString();
    string tags = data.Length > i && data[i] == ',' ? ReadString() : string.Empty;
    string? text = tags.Contains('s') ? ReadString() : null;
    return (address, tags, text);
}

var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

async Task<(string Address, string Tags, string? Text)?> TryReceive(int timeoutMs)
{
    using var cts = new CancellationTokenSource(timeoutMs);
    try
    {
        var result = await listener.ReceiveAsync(cts.Token);
        return Decode(result.Buffer);
    }
    catch (OperationCanceledException) { return null; }
}

// 收齐一段时间内的所有报文
async Task<List<(string Address, string Tags, string? Text)>> Collect(int durationMs)
{
    var list = new List<(string, string, string?)>();
    var deadline = DateTime.UtcNow.AddMilliseconds(durationMs);
    while (DateTime.UtcNow < deadline)
    {
        var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
        var packet = await TryReceive(Math.Min(remaining, 200));
        if (packet.HasValue) list.Add(packet.Value);
    }
    return list;
}

using var osc = new OscService { Port = port };

await Case("typing sends a draft preview and typing=true", async () =>
{
    var ctx = new FakeContext();
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("hello");

    var packets = await Collect(700);
    Check(packets.Any(p => p.Address == "/chatbox/input" && p.Text == "hello"),
        "missing draft preview: " + string.Join(",", packets.Select(p => p.Address + ":" + p.Text)));
    Check(packets.Any(p => p.Address == "/chatbox/typing" && p.Tags == ",T"), "missing typing=true");
    Check(ctx.LastNotifyValue == true, "typing intent not forwarded");
});

await Case("rapid keystrokes collapse into one preview of the latest text", async () =>
{
    var ctx = new FakeContext();
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("a");
    indicator.UpdateLiveTyping("ab");
    indicator.UpdateLiveTyping("abc");

    var packets = await Collect(800);
    var previews = packets.Where(p => p.Address == "/chatbox/input").ToList();
    Check(previews.Count == 1, "expected 1 preview, got " + previews.Count);
    Check(previews[0].Text == "abc", "preview text=" + previews[0].Text);
});

await Case("clearing the draft sends typing=false and clears the bubble once", async () =>
{
    var ctx = new FakeContext();
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("draft");
    await Collect(600);

    indicator.UpdateLiveTyping("");
    var packets = await Collect(500);
    Check(packets.Any(p => p.Address == "/chatbox/typing" && p.Tags == ",F"), "missing typing=false");
    var clears = packets.Where(p => p.Address == "/chatbox/input" && string.IsNullOrEmpty(p.Text)).ToList();
    Check(clears.Count == 1, "expected exactly one clear, got " + clears.Count);
    Check(ctx.LastNotifyValue == false, "clearing did not forward typing=false");
});

await Case("empty input without prior draft never clears the previous bubble", async () =>
{
    var ctx = new FakeContext();
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("");
    var packets = await Collect(400);
    Check(!packets.Any(p => p.Address == "/chatbox/input"), "cleared a bubble that was never drafted");
    Check(packets.Any(p => p.Address == "/chatbox/typing"), "typing=false was not sent");
});

await Case("live typing disabled only maintains the animation", async () =>
{
    var ctx = new FakeContext { LiveTypingEnabled = false };
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("typing without preview");
    var packets = await Collect(500);
    Check(!packets.Any(p => p.Address == "/chatbox/input"), "draft preview sent although live typing is off");
    Check(packets.Any(p => p.Address == "/chatbox/typing" && p.Tags == ",T"), "typing=true missing");
});

await Case("template-managed messages never write the chatbox preview", async () =>
{
    var ctx = new FakeContext { IsTemplateManagingMessages = true };
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("managed");
    var packets = await Collect(600);
    Check(!packets.Any(p => p.Address == "/chatbox/input"), "preview leaked while the template owns the bubble");
    Check(packets.Any(p => p.Address == "/chatbox/typing"), "typing state missing");
});

await Case("final send suppresses previews until it ends", async () =>
{
    var ctx = new FakeContext();
    using var indicator = new TypingIndicator(osc, ctx);

    var cts = indicator.BeginFinalSend();
    cts?.Cancel();
    cts?.Dispose();
    indicator.UpdateLiveTyping("ignored while sending");
    var duringSend = await Collect(400);
    Check(!duringSend.Any(p => p.Address == "/chatbox/input"), "preview sent during final send");

    indicator.EndFinalSend();
    indicator.UpdateLiveTyping("after send");
    var afterSend = await Collect(700);
    Check(afterSend.Any(p => p.Address == "/chatbox/input" && p.Text == "after send"),
        "typing did not resume after final send");
});

await Case("suppress cancels a pending preview", async () =>
{
    var ctx = new FakeContext();
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("pending");
    indicator.Suppress();
    var packets = await Collect(600);
    Check(!packets.Any(p => p.Address == "/chatbox/input"), "preview still sent after Suppress");
    Check(packets.Any(p => p.Address == "/chatbox/typing" && p.Tags == ",F"), "Suppress must send typing=false");
});

await Case("heartbeat keeps typing alive", async () =>
{
    var ctx = new FakeContext();
    using var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("heartbeat");
    var packets = await Collect(2200);   // 心跳 1500ms，至少应看到 2 次 typing=true
    int typingTrue = packets.Count(p => p.Address == "/chatbox/typing" && p.Tags == ",T");
    Check(typingTrue >= 2, "heartbeat did not repeat, saw " + typingTrue);
});

await Case("dispose stops further traffic", async () =>
{
    var ctx = new FakeContext();
    var indicator = new TypingIndicator(osc, ctx);
    indicator.UpdateLiveTyping("before dispose");
    await Collect(500);
    indicator.Dispose();
    indicator.UpdateLiveTyping("after dispose");
    var packets = await Collect(500);
    Check(!packets.Any(p => p.Address == "/chatbox/input"), "traffic continued after Dispose");
});

listener.Dispose();
Console.WriteLine($"typing regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
