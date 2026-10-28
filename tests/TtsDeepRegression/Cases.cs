using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace VrcChatboxDemo.Services.Tts;
public static class Cases {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(2));
    static HttpResponseMessage Health(string alias = "voice.pth", bool ready = true) => Handler.Json(JsonSerializer.Serialize(new {
        ready, status = ready ? "ready" : "loading", model = "fake", low_vram = false,
        models = new { models = new[] { alias } }
    }));
    static Handler Pcm() => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new ByteArrayContent(new byte[24000])
    }));
    static async Task HealthAfterStop(bool refresh) {
        var gate = Gate<HttpResponseMessage>();
        var service = TtsService.Create(new Handler((_, _) => gate.Task));
        var pending = refresh ? service.RefreshHealthAsync() : service.CheckHealthAsync();
        service.StopManagedServer(); gate.SetResult(Health()); await Wait(pending);
        Check(service.State == TtsServerState.Stopped, "Late health probe resurrected " + service.State);
        Check(!service.IsModelRegistered("voice.pth"), "Late health probe restored stopped model aliases");
        service.CloseClient();
    }
    static async Task HealthAfterNewSynthesis(bool ready) {
        var gate = Gate<HttpResponseMessage>();
        var service = TtsService.Create(new Handler((_, _) => gate.Task));
        var pending = service.RefreshHealthAsync();
        service.BeginFakeSynthesis(); gate.SetResult(Health(ready: ready)); await Wait(pending);
        Check(service.State == TtsServerState.Synthesizing, "Health probe overwrote active synthesis with " + service.State);
        service.CloseClient();
    }
    static async Task OutOfOrderHealth(bool refresh) {
        var first = Gate<HttpResponseMessage>(); int calls = 0;
        var service = TtsService.Create(new Handler((_, _) => ++calls == 1 ? first.Task : Task.FromResult(Health("new.pth"))));
        var old = refresh ? service.RefreshHealthAsync() : service.CheckHealthAsync();
        await Wait(refresh ? service.RefreshHealthAsync() : service.CheckHealthAsync());
        first.SetResult(Health("old.pth", ready: false)); await Wait(old);
        Check(service.IsModelRegistered("new.pth") && !service.IsModelRegistered("old.pth"), "Older probe replaced newer model registry");
        if (refresh) Check(service.State == TtsServerState.Ready, "Older refresh replaced newer Ready state");
        service.CloseClient();
    }
    static Task DeviceOwnership(string route) {
        FakeAudio.Reset();
        if (route == "explicit") {
            var devices = TtsService.Resolve("cable", "ears", true);
            Check(devices.Mic?.ID == "cable" && devices.Monitor?.ID == "ears", "Explicit routing changed");
            TtsService.Release(devices.Mic, devices.Monitor);
        } else if (route == "scan") {
            FakeAudio.Endpoints = new() { ["first"] = "Physical speaker", ["second"] = "Other speaker", ["cable"] = "CABLE Input" };
            using var enumerator = new MMDeviceEnumerator();
            using var device = TtsService.FindFirstVirtualAudioDevice(enumerator);
            Check(device?.ID == "cable", "Virtual endpoint not selected");
            device?.Dispose();
        } else if (route == "fallback") {
            FakeAudio.Endpoints = new() { ["ears"] = "Physical headphones" };
            var devices = TtsService.Resolve("missing", "missing", true);
            Check(devices.Mic?.ID == "ears" && devices.Monitor?.ID == "ears" && devices.Same, "Missing IDs did not fall back to default");
            TtsService.Release(devices.Mic, devices.Monitor);
        } else if (route == "metadata-error") {
            FakeAudio.ThrowFriendlyName = true;
            using var enumerator = new MMDeviceEnumerator();
            Check(TtsService.FindFirstVirtualAudioDevice(enumerator) == null, "Expected failed enumeration");
        } else {
            var service = TtsService.Create(Pcm());
            Check(service.GetPlaybackDevices().Count == 2, "Device listing changed");
            service.CloseClient();
        }
        Check(FakeAudio.OpenDevices == 0, "Leaked MMDevice wrappers: " + FakeAudio.OpenDevices);
        Check(FakeAudio.OpenCollections == 0, "Leaked MMDeviceCollection: " + FakeAudio.OpenCollections);
        return Task.CompletedTask;
    }
    static async Task StoppedSink(string failedDevice, bool sameDevice) {
        FakeAudio.Reset(); FakeAudio.FailDevice = failedDevice;
        var service = TtsService.Create(Pcm());
        var task = service.PlayStreamAsync("test", sameDevice ? "ears" : "cable", "ears", true);
        bool bounded = await Task.WhenAny(task, Task.Delay(500)) == task;
        if (!bounded) service.StopPlayback();
        await Wait(task);
        Check(service.QueueCount == 1, "Playback queue was not released");
        Check(bounded, "Stopped output left buffered PCM in an infinite drain loop");
        Check(!task.Result, "Stopped output was reported as successful playback");
        FakeAudio.FailDevice = null;
        Check(await service.PlayStreamAsync("next", "cable", "ears", true), "Playback queue did not accept subsequent request");
        Check(service.QueueCount == 1, "Subsequent playback leaked queue lock");
        service.CloseClient();
    }
    static async Task NormalPlayback() {
        FakeAudio.Reset(); var service = TtsService.Create(Pcm());
        Check(await service.PlayStreamAsync("normal", "cable", "ears", true), "Normal playback failed");
        Check(service.QueueCount == 1, "Normal playback leaked lock");
        service.CloseClient();
    }
    static async Task QueuedCancellation() {
        FakeAudio.Reset(); var service = TtsService.Create(Pcm());
        var queue = (SemaphoreSlim)typeof(TtsService).GetField("_playbackQueueLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        await queue.WaitAsync();
        var first = service.PlayStreamAsync("first", "cable", "ears", true);
        var second = service.PlayStreamAsync("second", "cable", "ears", true);
        service.StopPlayback(); queue.Release();
        await Wait(Task.WhenAll(first, second));
        Check(!first.Result && !second.Result && service.QueueCount == 1, "Queued cancellation double-released or hung queue");
        Check(FakeAudio.PlayCalls == 0, "Cancelled queued request reached fake playback");
        service.CloseClient();
    }
    static async Task OllamaBufferedContract() {
        bool requestChecked = false;
        var handler = new Handler(async (request, token) => {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(!doc.RootElement.GetProperty("stream").GetBoolean(), "Translation unexpectedly requested a stream");
            requestChecked = true;
            return Handler.Json("{\"response\":\" translated \"}");
        });
        var service = new OllamaService();
        var field = typeof(OllamaService).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((HttpClient)field.GetValue(service)!).Dispose(); using var client = new HttpClient(handler);
        field.SetValue(service, client);
        var result = await service.TranslateAsync("text", "English", "model", "http://fake.invalid");
        Check(requestChecked && result.Text == "translated", "Buffered Ollama parsing regressed");
    }
    public static async Task<int> Run() {
        var tests = new (string Name, Func<Task> Run)[] {
            ("late refresh after stop cannot resurrect state/aliases", () => HealthAfterStop(true)),
            ("late direct health check cannot restore stopped aliases", () => HealthAfterStop(false)),
            ("ready probe cannot overwrite newer synthesis", () => HealthAfterNewSynthesis(true)),
            ("not-ready probe cannot overwrite newer synthesis", () => HealthAfterNewSynthesis(false)),
            ("older direct health result cannot replace registry", () => OutOfOrderHealth(false)),
            ("older refresh cannot replace newer state/registry", () => OutOfOrderHealth(true)),
            ("explicit routing releases all opened devices", () => DeviceOwnership("explicit")),
            ("virtual scan releases skipped wrappers and collection", () => DeviceOwnership("scan")),
            ("missing device IDs fall back without resource leaks", () => DeviceOwnership("fallback")),
            ("failed metadata lookup releases device and collection", () => DeviceOwnership("metadata-error")),
            ("device listing releases endpoint collection", () => DeviceOwnership("listing")),
            ("stopped shared sink releases playback queue", () => StoppedSink("ears", true)),
            ("stopped mic sink releases playback queue", () => StoppedSink("cable", false)),
            ("stopped monitor sink releases playback queue", () => StoppedSink("ears", false)),
            ("normal playback still completes", NormalPlayback),
            ("queued cancellation does not double-release lock", QueuedCancellation),
            ("Ollama translation uses buffered response contract", OllamaBufferedContract)
        };
        int failures = 0;
        foreach (var test in tests) {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} deep TTS regressions passed; fake audio only, process operations forbidden");
        return failures == 0 ? 0 : 1;
    }
}
