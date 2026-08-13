# Split-pane (K): WT profile selection composes with the split form (#516)

Part of the Split-pane epic (#502). Depends on **B (#504)** and **#462** — both merged.

## Status: the feature is already on `main`; this pins its one untested seam

The headline deliverable of #516 — thread the matched Windows Terminal profile into
the split candidate as `-p`, alongside the new-tab/new-window forms, and keep it out of
`PlanAppLaunch` — shipped with **#504** (PR #575). The geometry/focus surface it must
compose with (`-V`/`-H`, `-s <fraction>`, `; mf previous`) shipped with **#505** (PR
#581). Because the Windows split spec is built as

```csharp
// TerminalCommandPlanner.cs (PlanWindows)
WtArgs(WtSplitPrefix(options), options.WindowsTerminalProfile, command,
       options.SplitFocus == SplitFocus.StayPut)
```

the profile threads in **after** the geometry prefix and **before** the trailing
command, and the stay-put focus subcommand is appended after the escape pass — so profile
and geometry already compose. Nothing new needs building.

## What was missing

#516's **Tests** section asks specifically for "composition with `-w 0` and the geometry
arguments." That was the one requirement still unmet:

- `Windows_Split_CarriesTheWtProfile_WhenSet` pins profile-alone (Auto direction, no
  size, follow-focus).
- The geometry tests (`Windows_Split_Beside/Below/Auto`, `..._Size_MapsToParentFraction`,
  `..._DirectionAndSize_Compose`) and the focus tests
  (`Windows_Split_StayPut_ChainsMoveFocusPrevious...`) all run with **no** profile.

Nothing pinned `-p` **together with** `-V`/`-H`, `-s`, and `; mf previous` in a single
split spec. The composition is emergent from `WtArgs` + `WtSplitPrefix`, so an
argv-ordering regression there (e.g. a future edit inserting `-p` before the geometry
flags, or letting the escape pass swallow the focus `;`) would pass CI silently. WT's
`sp` accepts its options in any order *before* the trailing commandline, and `; mf
previous` must remain a chained subcommand after it — an order the argv-vector shape
helps with but does not guarantee, exactly the concern #516 raises.

## Change (tests only)

Add to `TerminalCommandPlannerSplitPaneTests`, in the WT profile region:

1. **Full composition** — profile + `Beside` + `30%` + `StayPut` inside WT emits
   `["-w","0","sp","-V","-s","0.3","-p","Ubuntu","pwsh","-NoExit","-Command", <payload>]`
   with `[";","mf","previous"]` as the last three tokens. Pins the whole order at once:
   geometry (from the prefix) → `-p <profile>` → command → focus retention.
2. **Direction-only + profile** and **size-only + profile** — smaller cross-products so a
   regression in either geometry axis' interleaving with `-p` is caught in isolation.
3. **No-match guard with geometry set** — a split with geometry but a blank profile is
   byte-identical to the same split with no profile field at all (the `-p` insertion is
   the *only* difference a matched profile makes), reusing the `BlankProfile...` precedent
   but with non-default geometry.

`-p` is Windows-only (#462), so the composition is a WT concern; Linux/macOS split hosts
carry no profile and need no composition test.

## Out of scope / deferred

- **Phase 0 live WT-CLI verification** — confirming `wt -w 0 sp -p "<profile>" pwsh
  -NoExit -Command "<cmd>"` actually runs *our* command rather than the profile's own, and
  how `sp` treats `-d`/`startingDirectory` vs. the baked-in `Set-Location`. This is #462's
  shared manual-Windows spike (decided once with #515's `-d` question); it cannot run under
  the Linux `tui-validate` PTY harness and is not code. The feature ships off by default
  (`AgentDispatchSettings.TryUseWindowsTerminalProfiles`), so it is inert until a user opts
  in and a profile matches.

## Tests

`dotnet build -c Release` (0/0) → `dotnet test -c Release` green → `dotnet format
--verify-no-changes` clean. No `tui-validate` run: no rendering, list-source, driver, or
keypress code is touched (pure planner test coverage), so there is no
second-focusable-pane or keypress-latency surface (#3/#12).
