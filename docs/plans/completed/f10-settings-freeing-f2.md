# Plan — Contextual chords (B): F10 = Settings, freeing F2 (#539)

Slice **B** of the contextual key/chord remapping epic (#537). Small, self-contained, and
**independent of the #538 model decision (A)**: it does not touch the contextual-chord model,
it only moves one binding. Its whole purpose is to leave `F2` bound to nothing so the later
rename slices (D #541, E #542, H #545) can claim it.

## Goal

Rebind **Settings** from `F2` → `F10` everywhere it is surfaced, so:

- Settings opens with `F10` from every surface that previously offered it on `F2`.
- `F2` is bound to nothing in every context.
- No other behaviour changes.

## Where Settings lives today

Settings is bound in exactly one place in the source-of-truth table (#355):

```
[(ScreenContext.MainList, KeyAction.Settings)] = "F2"   // Keybindings.cs:103
```

It is **dispatched** table-driven — `TodoApp` wires `.On(KeyAction.Settings, OpenSettings)`
(`TodoApp.cs:544`), which reads the token from `Keybindings`, so there is **no hardcoded
`KeyCode.F2`** to chase. Changing the table entry rebinds the live keypress automatically.

`F10` is currently unused anywhere in the repo (verified by grep), so there is no collision.

## Change surface

| # | File | Change |
|---|------|--------|
| 1 | `src/ClickUpTodo/Tui/Screens/Keybindings.cs` | `MainList/Settings` token `"F2"` → `"F10"` (the binding) |
| 2 | `src/ClickUpTodo/Tui/Screens/HelpLine.cs` | Main-list footer item `new("F2", "⚙")` → `new("F10", "⚙")` (required by the #355 cross-check `Footer_ShowsTheTableKey_ForEveryBinding`) |
| 3 | `src/ClickUpTodo/Tui/Screens/HelpScreen.cs` | The F1 help listing line `F2 ⚙ Settings` → `F10`; the exit-guard aside "F2 to turn off" → "F10" |
| 4 | `README.md` | Task-list shortcut table row `F2 ⚙ Settings` → `F10`; the prose that names the Settings key (quit-guard aside, Detail-view / Ctrl+Click-destination / preferred-terminal / custom-terminal pointers) → `F10` |
| 5 | `tests/ClickUpTodo.Tests/HelpLineTests.cs` | Pinned full-footer string: `F2 ⚙` → `F10 ⚙` |
| 6 | `tests/ClickUpTodo.Tui.E2E/subdomain_check.py` | Send `F10` (`\x1b[21~`, matching the harness's CSI-tilde form for F5/F6/F12) instead of `F2` (`\x1bOQ`) to open Settings; update its comments/labels |

Out of scope: internal implementation comments that use "F2 settings" as shorthand for the
settings *dialog* (its transaction boundary, rebuild-on-save, cycle buttons, etc.) — those
describe dialog behaviour, not the key, and sweeping ~15 files of them would broaden the diff
and risk conflicts with the busy PR queue. The enumerated acceptance surfaces (table, footer,
help screen, README, tests, tui-validate) are what "offered it on F2" and are all updated.

## Tests / gates

- `KeybindingsTests` stays green: the table→footer cross-check now resolves Settings under
  `F10` on both sides; `EveryToken_IsParseable` covers `F10` (Terminal.Gui parses it).
- `HelpLineTests.Format_MainList_RendersTheFullFooter` updated to the `F10 ⚙` footer.
- Add a pinned guard: `Settings_IsF10_OnMainList_AndF2_IsUnbound` — asserts the token is `F10`
  and that no `(context, action)` in the table maps to `F2`, so a future re-introduction of an
  `F2` binding without deciding the rename model fails loudly here.
- Full gate: `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format --verify-no-changes`.
- `tui-validate`: `subdomain_check.py` (F10 now opens Settings). It's the only check that
  exercises the Settings key.

## Verification (manual, TUI not CI-unit-testable)

`F10` opens Settings from the main list; `F2` does nothing; the footer shows `F10 ⚙`; the F1
Help screen and README list `F10`. `subdomain_check.py` is the automated proof for the key.
