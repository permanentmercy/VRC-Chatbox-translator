using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VrcChatboxDemo.Services.Tts {
    public sealed class ConfigData { public string TtsServerEndpoint = "http://fake.invalid"; }
    public sealed class SettingsService {
        public static SettingsService Instance = new();
        public ConfigData Config = new();
    }
    public sealed class Handler : HttpMessageHandler {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => Send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Send(request, token);
        public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
namespace NAudio.CoreAudioApi {
    public enum DataFlow { Render }
    public enum Role { Multimedia }
    public enum DeviceState { Active }
    public enum AudioClientShareMode { Shared }
    public static class FakeAudio {
        public static Dictionary<string, string> Endpoints = new();
        public static string DefaultId = "ears";
        public static string? FailDevice;
        public static bool ThrowFriendlyName;
        public static int OpenDevices, OpenCollections, DefaultCalls, PlayCalls;
        public static void Reset() {
            Endpoints = new() { ["ears"] = "Physical headphones", ["cable"] = "CABLE Input" };
            DefaultId = "ears"; FailDevice = null; ThrowFriendlyName = false;
            OpenDevices = 0; OpenCollections = 0; DefaultCalls = 0; PlayCalls = 0;
        }
    }
    public sealed class MMDevice : IDisposable {
        readonly string name;
        bool disposed;
        public string ID { get; }
        public string FriendlyName => FakeAudio.ThrowFriendlyName ? throw new Exception("metadata unavailable") : name;
        public MMDevice(string id, string name) { ID = id; this.name = name; Interlocked.Increment(ref FakeAudio.OpenDevices); }
        public FakeAudioClient CreateAudioClient() => new();
        public void Dispose() { if (!disposed) { disposed = true; Interlocked.Decrement(ref FakeAudio.OpenDevices); } }
    }
    public sealed class FakeAudioClient : IDisposable {
        public NAudio.Wave.WaveFormat MixFormat => new(24000, 16, 1);
        public void Dispose() { }
    }
    public sealed class MMDeviceCollection : IEnumerable<MMDevice>, IDisposable {
        bool disposed;
        public MMDeviceCollection() { Interlocked.Increment(ref FakeAudio.OpenCollections); }
        public IEnumerator<MMDevice> GetEnumerator() {
            foreach (var endpoint in FakeAudio.Endpoints) yield return new MMDevice(endpoint.Key, endpoint.Value);
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { if (!disposed) { disposed = true; Interlocked.Decrement(ref FakeAudio.OpenCollections); } }
    }
    public sealed class MMDeviceEnumerator : IDisposable {
        public MMDevice GetDevice(string id) => FakeAudio.Endpoints.TryGetValue(id, out var name)
            ? new MMDevice(id, name) : throw new Exception("missing endpoint");
        public MMDevice GetDefaultAudioEndpoint(DataFlow flow, Role role) {
            FakeAudio.DefaultCalls++; return GetDevice(FakeAudio.DefaultId);
        }
        public MMDeviceCollection EnumerateAudioEndPoints(DataFlow flow, DeviceState state) => new();
        public void Dispose() { }
    }
}
namespace NAudio.Wave {
    using NAudio.CoreAudioApi;
    public sealed class WaveFormat {
        public int SampleRate { get; }
        public int Channels { get; }
        public WaveFormat(int sampleRate, int bits, int channels) { SampleRate = sampleRate; Channels = channels; }
    }
    public interface IWaveProvider { WaveFormat WaveFormat { get; } int Read(byte[] buffer, int offset, int count); }
    public interface ISampleProvider { WaveFormat WaveFormat { get; } int Read(Span<float> buffer); }
    public static class FakeExtensions {
        public static ISampleProvider ToSampleProvider(this IWaveProvider provider) => new WaveSample(provider);
        sealed class WaveSample : ISampleProvider {
            readonly IWaveProvider source;
            public WaveSample(IWaveProvider source) => this.source = source;
            public WaveFormat WaveFormat => source.WaveFormat;
            public int Read(Span<float> buffer) { var bytes = new byte[buffer.Length * 2]; return source.Read(bytes, 0, bytes.Length) / 2; }
        }
    }
    public enum PlaybackState { Stopped, Playing }
    public sealed class WasapiOut : IDisposable {
        readonly MMDevice device;
        ISampleProvider source = null!;
        public PlaybackState PlaybackState { get; private set; }
        public WasapiOut(MMDevice device, AudioClientShareMode shareMode, bool useEventSync, int latency) => this.device = device;
        public void Init(ISampleProvider provider) => source = provider;
        public void Play() {
            Interlocked.Increment(ref FakeAudio.PlayCalls);
            if (device.ID == FakeAudio.FailDevice) { PlaybackState = PlaybackState.Stopped; return; }
            PlaybackState = PlaybackState.Playing;
            source.Read(new float[65536]); // Drain entirely in memory; no OS audio APIs are present.
        }
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() => Stop();
    }
}
namespace NAudio.Wave.SampleProviders {
    using NAudio.Wave;
    public class ForwardSample : ISampleProvider {
        readonly ISampleProvider source;
        public ForwardSample(ISampleProvider source) => this.source = source;
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<float> buffer) => source.Read(buffer);
    }
    public sealed class MonoToStereoSampleProvider : ForwardSample { public MonoToStereoSampleProvider(ISampleProvider source) : base(source) { } }
    public sealed class WdlResamplingSampleProvider : ForwardSample { public WdlResamplingSampleProvider(ISampleProvider source, int rate) : base(source) { } }
    public sealed class MultiplexingSampleProvider : ForwardSample {
        public MultiplexingSampleProvider(ISampleProvider[] sources, int channels) : base(sources[0]) { }
        public void ConnectInputToOutput(int input, int output) { }
    }
}
