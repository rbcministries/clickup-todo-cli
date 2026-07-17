# D3 — Dispatch pane: working-directory control (text field + file-tree browser) — #95

Part of the #90 agent-dispatch epic. Blockers all merged: #91 (config wiring), #92
(base working dir + `SettingsForm.ResolveDefaultWorkingDirectory`), #93 (Dispatch pane
shell + `DispatchPaneModel` + `DispatchRequest`), #98 (task-derived dir / output subdir).

## Goal

Let the user pick the working directory a dispatched `claude` session starts in, from
inside the Dispatch pane (Ctrl+A in the task detail view), via an editable text field plus
a subdirectory file-tree browser rooted at the base working directory (#92). A blank field
falls through to the existing configured-default / task-derived behaviour (#98) — so
zero-touch dispatch is byte-for-byte unchanged.

## Interaction model (documented, per the issue)

The browser is a single-column `ListView` under the working-dir field, listing `..` first
then the immediate subdirectories of the current directory (sorted, directories only):

- **arrow up / down** — move the highlight (native ListView).
- **arrow right** — descend into the highlighted subdirectory (repopulate). On `..`, goes up.
- **arrow left** — go up one level (repopulate). No-op at a filesystem root.
- **Enter** — *select* the highlighted subdirectory: write its absolute path into the
  working-dir field and advance focus to the next dispatch control. On `..`, Enter goes up
  (nothing to select), so Enter never accidentally submits from the browser.

This follows the issue's recommended model ("Enter selects; a separate key descends"), with
right/left as the descend/ascend affordance. The field is also directly editable.

## Design

### New pure helper — `Tui/Screens/DirectoryBrowserModel.cs`

Stateful, filesystem-backed, unit-tested against scratch directories (the only
filesystem-touching piece, so the Terminal.Gui `ListView` glue stays thin):

- `CurrentDirectory` (absolute, normalised) and `Entries` (`".."` + subdir names, sorted
  case-insensitively).
- `Normalize(dir)` / `Parent(dir)` — full-path + trailing-separator handling; a filesystem
  root's parent is itself (so left / `..` at root is a no-op).
- `IsParent(index)`, `PathAt(index)` — resolve an entry to an absolute path.
- `NavigateUp()`, `Descend(index)`, `Reset()` — repopulate `Entries`.
- Enumeration is resilient (unreadable / missing / invalid dir gives just `[".."]`, never
  throws) and bounded (caps the listing for very large directories).

### Event seam — `Tui/Screens/DispatchRequest.cs`

`DispatchRequest(string Prompt, string? WorkingDirectory = null)` — the optional working-dir
the pane submits. Blank gives null gives default behaviour. (#94/#97 still extend this later.)

### Pane glue — `Tui/Screens/TaskDetailScreen.cs`

- Constructor gains `string baseWorkingDirectory` (the browser root). The `_workingDirField`
  stub becomes editable (drop `ReadOnly`); add the `_dirBrowser` `ListView` + a one-line key
  hint label; insert the browser into `_dispatchControls` after the field.
- The browser gets its own `OnBrowserKey` handler (Enter/right/left as above; everything else
  — up/down, Tab, Esc, PgUp/PgDn — routes through the existing `DispatchPaneModel` via
  `OnDispatchKey`), so Enter in the browser selects instead of submitting.
- `ShowPrompt` clears the field and resets the browser to the root each open; height sized
  via a new tested `DispatchPaneModel.PreferredHeightWithBrowser(...)` + the existing
  `ClampHeight` (bottom controls clip first on short terminals).
- `SubmitDispatch` reads the field into `DispatchRequest.WorkingDirectory`.

### Dispatch wiring — `Tui/TodoApp.cs`

- `ShowTaskDetail`: resolve the base dir (`SettingsForm.ResolveDefaultWorkingDirectory`,
  falling back to home if it doesn't exist yet) and pass it to the screen.
- `DispatchAgent(detail, comments, DispatchRequest request)`: the chosen dir is the override
  fed to the already-present `AgentDispatchSettings.ResolveEffectiveWorkingDirectory(chosen,
  baseDir, home)` (the `cachedDirectory` override slot #96 will also seed). An explicit pick
  wins over the configured mode and suppresses the task-derived `./{custom-id}` output-subdir
  + auto-create (the user chose their exact dir); a blank field gives identical behaviour to
  today.

## Tests

- `DirectoryBrowserModelTests`: listing subdirs of a scratch dir (sorted, `..` first,
  directories only), `Parent`/`Normalize` incl. at filesystem root, descend/up round-trips,
  `PathAt`/`IsParent`, missing/unreadable dir gives `[".."]`, `Reset` returns to root.
- `DispatchPaneModelTests`: `PreferredHeightWithBrowser` arithmetic.
- No test constructs `TaskDetailScreen` / `DispatchRequest` (TUI not unit-tested) — the glue
  is verified by build + reasoning + a `tui-validate` regression pass (Ctrl+A pane isn't
  scripted; the pure model tests lock its decisions).

## Deferred

- Pre-fill the field from a per-task cache across relaunches → **#96** (seeds the same field).
