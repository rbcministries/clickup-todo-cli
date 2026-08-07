# Unify `TerminalCommandPlanner`'s two entry points (`Plan` / `PlanAppLaunch`)

Issue: #438 (deferred internal cleanup from #385 / PR #437).

## Goal

Collapse the duplicated OS-dispatch matrix in `TerminalCommandPlanner.Plan`
(agent-dispatch `claude` launch) and `TerminalCommandPlanner.PlanAppLaunch`
(`clickup-todo --task <id>` app launch) into a single shared entry point, so the
per-OS emulator selection lives in exactly one place. **Purely internal — no
behavior change.**

## Current shape

Both public entry points do the same three things and differ only in inputs:

| | `Plan` (dispatch) | `PlanAppLaunch` (app launch) |
|---|---|---|
| Windows inner cmd | `PwshCommand(promptFile, cwd, options, oneOff)` | `PwshAppCommand(command)` |
| POSIX inner cmd | `PosixCommand(promptFile, cwd, options, oneOff)` | `PosixAppCommand(command)` |
| working dir | `workingDir` (variable) | `null` |
| one-off | `oneOff` (variable) | `false` |

Each then runs the identical `os switch { Windows => PlanWindows(...), MacOS =>
PlanMacOS(...), Linux => PlanLinux(...), _ => [] }`. The per-OS builders
(`PlanWindows`/`PlanMacOS`/`PlanLinux`) and the custom-command candidate
(`CustomLaunchSpec`, #385) are **already** shared; only the switch is duplicated.

## Design

Introduce a small private strategy value that captures exactly the four things
that differ, then have both public methods build one and hand it to a single
shared dispatcher:

```csharp
private readonly record struct InnerCommand(
    string Pwsh, string Posix, string? WorkingDir, bool OneOff);

private static IReadOnlyList<LaunchSpec> PlanFor(
    OSPlatformKind os, Func<string, bool> exists, Func<string, string?> getEnv,
    in InnerCommand inner, TerminalLauncherOptions options) => os switch
    {
        OSPlatformKind.Windows => PlanWindows(exists, getEnv, inner.Pwsh,  inner.WorkingDir, options, inner.OneOff),
        OSPlatformKind.MacOS   => PlanMacOS  (exists, getEnv, inner.Posix, inner.WorkingDir, options, inner.OneOff),
        OSPlatformKind.Linux   => PlanLinux  (exists, getEnv, inner.Posix, inner.WorkingDir, options, inner.OneOff),
        _ => [],
    };
```

- `Plan` builds `new InnerCommand(PwshCommand(...), PosixCommand(...), workingDir, oneOff)`.
- `PlanAppLaunch` keeps its `ArgumentNullException.ThrowIfNull(command)` guard,
  then builds `new InnerCommand(PwshAppCommand(command), PosixAppCommand(command), null, false)`.

### Why eager-build both inner commands is safe

Each entry point now builds both the pwsh and posix inner-command strings even
though only one is used on a given OS. Both builders are **pure, allocation-only
string construction with no I/O** and are total functions on their inputs (they
cannot throw for valid inputs), so building the unused one is a discarded string
with no observable effect. This keeps `PlanFor` free of an OS-keyed builder
callback (a `Func<OSPlatformKind, string>` would restore laziness at the cost of
a closure per call and a less readable dispatcher) — the trade is trivial CPU for
one obvious code path. The value is passed `in` to avoid copying the struct.

## What does **not** change

- Public signatures of `Plan` and `PlanAppLaunch` — byte-identical.
- Returned `LaunchSpec` lists for every (OS, options, inputs) tuple.
- `PlanWindows` / `PlanMacOS` / `PlanLinux` / `CustomLaunchSpec` and every
  command/escaping helper.
- The `PlanAppLaunch` null-command guard (kept, still first).

## Tests

No behavior change, so the existing suites are the equivalence pins and must pass
**unchanged**:

- `TerminalLauncherTests` — the `Plan` (dispatch) path across all OSes, one-off,
  cwd, new-tab, custom command, quoting.
- `TerminalCommandPlannerAppLaunchTests` — the `PlanAppLaunch` path across all
  OSes, new-tab, tmux, quoting, the null-command throw, launcher orchestration.
- `TerminalCommandPlannerCustomTests` — the shared custom-command (#385)
  candidate through **both** entry points.

Do not weaken or delete any test. A green run of all three (unchanged) is the
proof of equivalence the issue asks for. `dotnet format` must stay clean.

## Out of scope

- No user-facing change; no new config surface.
- No change to `TerminalLauncher`, `DispatchCoordinator`, or callers.
