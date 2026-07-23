# Store the workspace subdomain — skip the `app.clickup.com` redirect on Ctrl+B (#304)

Persist the logged-in workspace's ClickUp **subdomain** (e.g. `odbm`) so a Ctrl+B browser
launch rewrites an `app.clickup.com` task URL's host to `{subdomain}.clickup.com`, landing
directly on the workspace host instead of eating the `app.clickup.com` → subdomain redirect.

Ships **option (1)** from the issue's open question — **user-entered** in Settings — which the
issue recommends first. Auto-detection (2)/API lookup (3) stay out of scope (noted in the PR).

## Key facts (verified in the code)

- **Ctrl+B → `TodoApp.OpenInBrowser` → `LaunchBrowser(url, name)`** (`Tui/TodoApp.cs:504`,
  `:1435`, `:1442`; detail path re-enters `LaunchBrowser(detail.Url, …)` at `:1536`). Today it
  calls `Process.Start(new ProcessStartInfo(url){ UseShellExecute = true })` **directly** —
  it does **not** use the `IBrowserLauncher` seam (only `OAuthSignIn` does, `Setup/OAuthSignIn.cs:56`).
- **The URL is whatever ClickUp returns** — `task.Url` / `detail.Url` (`ClickUp/Models.cs:130`,
  `:254`), an `app.clickup.com` link that redirects when the org has a subdomain.
- **No subdomain is stored.** `AppConfig` holds `WorkspaceId` only; the only hosts referenced
  are `api.clickup.com` / `app.clickup.com`.
- **`--reset` deletes the whole config** (`Program.cs:27` → `ConfigStore.Delete`), so a new
  field is cleared for free — no reset wiring needed.
- **`SettingsForm`** (`Tui/Screens/SettingsForm.cs`) is the pure input-handling class the
  settings screen delegates parsing to (`ExpandHomePath`, `ParseLookbackDays`, …); its results
  flow through `SettingsResult` and are persisted by `TodoApp` in the F2 close handler
  (`TodoApp.cs:989`).
- **E2E harness** (`tests/ClickUpTodo.Tui.E2E/Program.cs`) constructs the real `TodoApp` against
  a fake backend whose task URLs are `https://app.clickup.com/t/t{n}`; it passes env through to
  the subprocess, so a check can seed config and observe side effects.

## Design

- **Pure logic — `Services/ClickUpUrl.cs`** (no Terminal.Gui, matches `RowHitTester`):
  - `const AppHost = "app.clickup.com"`, `const ApiHost = "api.clickup.com"`, `const BaseDomain = "clickup.com"`.
  - `NormalizeSubdomain(string?)` → bare label (e.g. `odbm`). Accepts a label, a full host, or a
    pasted URL; strips scheme/path/port, takes the first DNS label, lowercases, validates the
    `[a-z0-9-]` charset, and returns `""` for blank/invalid input or ClickUp's own non-workspace
    hosts `app`/`api`. `""` is the "unset" sentinel.
  - `RewriteHost(string? url, string? subdomain)` → rewritten URL. Returns the url unchanged when
    the (normalized) subdomain is blank, the url isn't an absolute http/https URL, or its host
    isn't `app.clickup.com`; otherwise swaps the host to `{subdomain}.clickup.com`, preserving
    scheme/path/query/fragment (and any explicit port). This is the "which hosts are ours" seam
    #303's URL parser can reuse.
- **`AppConfig.WorkspaceSubdomain`** (string, default `""`). Absent ⇒ blank ⇒ no rewrite, so
  existing configs need no migration; cleared on `--reset` with the rest of config.
- **Route `TodoApp` browser launches through `IBrowserLauncher`** (inject an optional ctor param
  defaulting to `SystemBrowserLauncher`). `LaunchBrowser` applies `ClickUpUrl.RewriteHost(url,
  _config.WorkspaceSubdomain)`, parses the result to a `Uri`, and calls `TryOpen`, flashing
  "Opened"/"Could not open" on the boolean. This removes the duplicated `Process.Start`, keeps
  OAuth's launcher untouched, and gives the E2E harness a fake-launcher seam to observe the URL.
- **Settings UI (`SettingsScreen`)** — a single-row "ClickUp subdomain:" label + field on the
  free left-column row (Y=8), seeded from `_config.WorkspaceSubdomain`. On Save,
  `SettingsResult.WorkspaceSubdomain = ClickUpUrl.NormalizeSubdomain(field.Text)`. No second
  focusable pane, no new bare-letter shortcut.

## Invariants preserved

- **Generated client / curated spec untouched** — no ClickUp API surface change (the subdomain is
  local config; the rewrite is a pure string transform on an already-fetched URL).
- **No second focusable pane (#3/#38)** — one extra `TextField` on the existing Settings screen.
- **Bare letters reserved for type-ahead (#12)** — no keyboard change; the field is on the modal.

## Phases

### Phase 1 — pure logic + config (fully unit-tested, no UI)
- `Services/ClickUpUrl.cs` (`NormalizeSubdomain`, `RewriteHost`, host constants).
- `AppConfig.WorkspaceSubdomain`.
- `ClickUpUrlTests`: normalize (label / host / URL / trailing-slash / uppercase / blank / `app` /
  `api` / invalid charset / port); rewrite (subdomain set on an `app.clickup.com` URL preserves
  path+query+fragment; blank subdomain → unchanged; non-`app` host → unchanged; `api.clickup.com`
  → unchanged; non-absolute / non-http → unchanged; normalize-then-rewrite round trip).

### Phase 2 — TUI wiring + E2E
- Route `TodoApp.LaunchBrowser` through an injected `IBrowserLauncher`; apply the rewrite.
- `SettingsResult` + `SettingsScreen` field; `TodoApp` F2 save persists `WorkspaceSubdomain`.
- Update both `Program.cs` constructions (prod: `SystemBrowserLauncher`; E2E: recording launcher).
- E2E: `E2E_SUBDOMAIN` seeds `config.WorkspaceSubdomain`; a recording `IBrowserLauncher` appends
  launched URLs to `E2E_BROWSER_LOG`; `subdomain_check.py` asserts (a) the Settings field renders
  with the seeded value and (b) Ctrl+B records `https://odbm.clickup.com/t/t0` (host rewritten).

## Deferred (tracked)

- **Auto-detection / API lookup of the subdomain** (options 2/3) — a convenience follow-up; the
  user-entered path is always correct. Tracked as a new issue, linked from the PR.
- **#303 consumption** — this exposes `ClickUpUrl` + the stored subdomain; wiring it into #303's
  URL parser lands with #303.
