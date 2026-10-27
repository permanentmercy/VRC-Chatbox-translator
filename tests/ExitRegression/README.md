# Safe exit regressions

From the repository root:

```powershell
dotnet run --project tests/ExitRegression/ExitRegression.csproj
```

Uses SDK-bundled Roslyn and no additional NuGet packages. It compiles the current
production exit methods and the actual `OllamaService` against deterministic
service/HTTP doubles. The MainWindow Closed handler's body is unchanged; only
its signature is adapted from async void to Task so the harness can await it.
No WinUI application, Python process, GPU, real Ollama endpoint, or OS shutdown
is involved.

## Termination safety

**No real process is terminated, including dotnet and the host.**

- The production `TerminateCurrentProcess` implementation is excluded from the
  dynamic test assembly and replaced by an event recorder.
- For pre-fix reproduction only, Roslyn redirects the old inline Kill and
  Environment.Exit invocations to that recorder. All remaining executable
  invocations are checked before compilation to reject those terminal calls.
- The repaired internal shutdown overload is additionally tested with an
  explicitly injected recording Action.

The readonly three-second production Ollama budget is shortened to 80 ms only
on isolated test instances through reflection. Each test has an external
watchdog and releases its fake stalled HTTP handler in finally.

## Coverage

11 cases cover tray-exit unload ordering, ordinary window-close behavior,
window/tray sharing a single cleanup task, repeated cleanup, awaiting a pending
request before termination, bounded exit for cooperative/uncooperative stalled
HTTP, a shared query/unload budget that cancels remaining models, offline
Ollama, TTS disposal failure, and the injected termination delegate.

The initial 10-case suite ran before the production fix: 1/10 passed; tray exit
made zero `/api/ps` and zero unload requests. The fixed expanded suite passes
11/11. Main-project builds are intentionally left to the lead.
