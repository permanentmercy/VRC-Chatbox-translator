# TTS deterministic regressions

Run from the repository root:

```powershell
dotnet run --project tests/TtsRegression/TtsRegression.csproj
```

The console harness uses Roslyn bundled with the .NET SDK to compile selected,
unmodified members from the current production `TtsService` and its models in
memory. Settings and HTTP are deterministic doubles. It does not run the WinUI
application, NAudio, real playback devices, Python, or model inference, and adds
no NuGet packages.

The production HTTP client and readonly body-read timeout are replaced through
reflection only on isolated test instances. The timeout is shortened from five
minutes to 80 ms; a 600 ms watchdog fails a stalled test and then cancels/releases
its controlled stream. A timeout must propagate as `TimeoutException`, not EOF,
so `PlayStreamAsync`'s existing exception path returns false even after PCM has
already been received.

Coverage (15 cases):

- Stopping before stream headers return: cancellation, error, and late success.
- Stopping before buffered synthesis completes or fails.
- Stopping during a body read; stopping before early iterator disposal.
- An older iterator cannot reset a newer request's Synthesizing state.
- Timeout before first PCM, after a PCM prefix, for a stream ignoring cancellation,
  and while reading a non-success HTTP response body.
- Normal completion, early disposal, caller cancellation, and an already-cancelled
  request that must not change state or send HTTP.

The initial 12-case suite was run against the pre-fix working-tree version:
2/12 passed and 10 failed. After the fix and three additional lifecycle cases,
15/15 passed. Main-project compilation and hardware playback are intentionally
left to the lead/integration verification.
