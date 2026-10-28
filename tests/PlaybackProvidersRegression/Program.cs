using NAudio.Wave;
using VrcChatboxDemo.Services.Synthesis;

// 播放侧两个 Provider 的回归：无界队列的字节搬运与软限幅的音量/削顶行为。
// 这两个类原先埋在 1,616 行的 TtsService.cs 末尾，只有间接覆盖。
int passed = 0;
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Case(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}

var format = new WaveFormat(24000, 16, 1);
byte[] Pattern(int length, byte start)
{
    var data = new byte[length];
    for (int i = 0; i < length; i++) data[i] = (byte)(start + i);
    return data;
}

Case("queued bytes are returned in order", () =>
{
    var provider = new SpeechQueueWaveProvider(format);
    provider.AddSamples(Pattern(8, 1), 0, 8);
    provider.AddSamples(Pattern(4, 100), 0, 4);

    var output = new byte[12];
    int read = provider.Read(output);
    Check(read == 12, "read=" + read);
    Check(output[0] == 1 && output[7] == 8, "first chunk corrupted");
    Check(output[8] == 100 && output[11] == 103, "second chunk corrupted");
});

Case("buffered byte count tracks pending data", () =>
{
    var provider = new SpeechQueueWaveProvider(format);
    Check(provider.BufferedBytes == 0, "empty provider reports data");
    provider.AddSamples(Pattern(10, 0), 0, 10);
    Check(provider.BufferedBytes == 10, "buffered=" + provider.BufferedBytes);

    var output = new byte[4];
    provider.Read(output);
    Check(provider.BufferedBytes == 6, "after partial read buffered=" + provider.BufferedBytes);
});

Case("empty queue returns silence instead of a short read", () =>
{
    var provider = new SpeechQueueWaveProvider(format);
    var output = new byte[16];
    for (int i = 0; i < output.Length; i++) output[i] = 0xFF;

    int read = provider.Read(output);
    Check(read == 16, "WASAPI needs a full buffer, got " + read);
    Check(output.All(b => b == 0), "buffer was not zero-filled");
});

Case("clear buffer drops pending audio", () =>
{
    var provider = new SpeechQueueWaveProvider(format);
    provider.AddSamples(Pattern(32, 0), 0, 32);
    provider.ClearBuffer();
    Check(provider.BufferedBytes == 0, "clear left " + provider.BufferedBytes + " bytes");

    var output = new byte[8];
    provider.Read(output);
    Check(output.All(b => b == 0), "cleared data was still returned");
});

Case("partial reads across chunk boundaries stay contiguous", () =>
{
    var provider = new SpeechQueueWaveProvider(format);
    provider.AddSamples(Pattern(5, 10), 0, 5);
    provider.AddSamples(Pattern(5, 50), 0, 5);

    var first = new byte[3];
    provider.Read(first);
    var second = new byte[7];
    provider.Read(second);

    var all = first.Concat(second).ToArray();
    Check(all[0] == 10 && all[4] == 14, "first chunk mismatch");
    Check(all[5] == 50 && all[9] == 54, "second chunk mismatch");
});

Case("add samples copies the source buffer", () =>
{
    var provider = new SpeechQueueWaveProvider(format);
    var source = Pattern(4, 7);
    provider.AddSamples(source, 0, 4);
    source[0] = 99;   // 调用方复用缓冲区不应影响已入队数据

    var output = new byte[4];
    provider.Read(output);
    Check(output[0] == 7, "queued data aliased the caller buffer");
});

Case("zero-length add is ignored", () =>
{
    var provider = new SpeechQueueWaveProvider(format);
    provider.AddSamples(Pattern(4, 0), 0, 0);
    Check(provider.BufferedBytes == 0, "zero-length add enqueued data");
});

Case("volume 1.0 only clamps near full scale", () =>
{
    var source = new FakeSampleSource([0.5f, 0.99f, -0.99f, 0.1f]);
    var limiter = new SoftLimiterSampleProvider(source, 1.0f);

    var buffer = new float[4];
    limiter.Read(buffer);

    Check(Math.Abs(buffer[0] - 0.5f) < 1e-6, "normal sample changed: " + buffer[0]);
    Check(Math.Abs(buffer[1] - 0.98f) < 1e-6, "should clamp to 0.98, got " + buffer[1]);
    Check(Math.Abs(buffer[2] + 0.98f) < 1e-6, "should clamp to -0.98, got " + buffer[2]);
});

Case("amplification uses soft limiting and stays bounded", () =>
{
    var source = new FakeSampleSource([0.9f, -0.9f, 0.2f]);
    var limiter = new SoftLimiterSampleProvider(source, 2.0f);

    var buffer = new float[3];
    limiter.Read(buffer);

    Check(buffer[0] < 1.0f && buffer[0] > 0.95f, "loud positive not limited: " + buffer[0]);
    Check(buffer[1] > -1.0f && buffer[1] < -0.95f, "loud negative not limited: " + buffer[1]);
    Check(Math.Abs(buffer[2] - 0.4f) < 1e-6, "quiet sample should scale linearly: " + buffer[2]);
});

Case("attenuation scales linearly", () =>
{
    var source = new FakeSampleSource([0.8f, -0.4f]);
    var limiter = new SoftLimiterSampleProvider(source, 0.5f);

    var buffer = new float[2];
    limiter.Read(buffer);

    Check(Math.Abs(buffer[0] - 0.4f) < 1e-6, "got " + buffer[0]);
    Check(Math.Abs(buffer[1] + 0.2f) < 1e-6, "got " + buffer[1]);
});

Case("volume is clamped to the supported range", () =>
{
    var source = new FakeSampleSource([1.0f]);
    var limiter = new SoftLimiterSampleProvider(source, 10.0f);   // 会被钳到 2.0
    var buffer = new float[1];
    limiter.Read(buffer);
    Check(buffer[0] <= 1.0f, "volume above the cap was not clamped: " + buffer[0]);
});

Console.WriteLine($"playback provider regressions: {passed} passed, {failures.Count} failed");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));

/// <summary>按需吐出固定样本的假音源。</summary>
sealed class FakeSampleSource : ISampleProvider
{
    private readonly float[] _samples;
    public FakeSampleSource(float[] samples) => _samples = samples;
    public WaveFormat WaveFormat { get; } = new(24000, 32, 1);
    public int Read(Span<float> buffer)
    {
        int count = Math.Min(buffer.Length, _samples.Length);
        _samples.AsSpan(0, count).CopyTo(buffer);
        return count;
    }
}
