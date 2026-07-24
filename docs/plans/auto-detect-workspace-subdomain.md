# Auto-detect the workspace subdomain (follow-up to #304, #351)

#304 shipped **option (1)** — a user-entered `ClickUp subdomain` field in Settings that a Ctrl+B
browser launch rewrites onto (`app.clickup.com` → `{subdomain}.clickup.com`, via
`Services.ClickUpUrl`). This follow-up adds the convenience the #304 open question deferred:
populating `AppConfig.WorkspaceSubdomain` **without** the user typing it. The user-entered path
stays the always-correct fallback, so this is purely additive.

## The two options the issue lists

- **Option (3) — a ClickUp API field.** *Investigated: not available.* The v2
  `GET /team` (`GetAuthorizedTeams`) response is the only workspace-shaped payload we fetch, and
  its `Workspace` schema in the curated `clickup-openapi.json` exposes only `id`, `name`, and
  `members` — no host/subdomain. ClickUp's public v2 API documents no other endpoint that returns
  the org subdomain. So there is nothing to map into the domain records, and **no curated-spec /
  Kiota change is warranted** for this issue.
- **Option (2) — auto-detect once by following an `app.clickup.com` redirect** and capturing the
  final host. This is what we build, as an **opt-in, best-effort** affordance.

## Empirical caveat (why this is opt-in + best-effort)

The `app.clickup.com` → `{subdomain}.clickup.com` redirect is, in a browser, driven by the signed-in
web **session cookie**, not the API token. An anonymous HTTP probe may therefore land on a login /
marketing page still on `app.clickup.com` (⇒ no subdomain detected) rather than the workspace host.
Whether the redirect fires for an unauthenticated request is environment-dependent and **could not
be exercised in CI** (the sandbox network policy denies `app.clickup.com`). The design accordingly:

- makes detection a **manual, opt-in** action (a "Detect" button), never an automatic write;
- **fails soft** — when it can't determine a subdomain it changes nothing, leaving the
  user-entered value intact;
- isolates the network round-trip behind an **env-gated `SkippableFact`** so a maintainer can
  confirm/deny the real redirect behaviour on their own network without gating normal CI.

If the maintainer's run shows the redirect never fires anonymously, the button is harmless (reports
"none") and #351 can be closed as "probe not feasible; keep manual entry", with this note as the
record.

## Design

### Pure seam — `Services/ClickUpUrl.cs` (no Terminal.Gui / I/O, unit-tested)

Reuses the existing "which hosts are ours" helper. Adds:

- `ReservedSubdomains` — the non-workspace `*.clickup.com` labels (`app`, `api`, `www`, `help`,
  `support`, `docs`, `sso`, `sharing`) that must never be mistaken for a workspace subdomain.
- `SubdomainFromWorkspaceHost(string? host)` → the workspace label **only** when `host` is exactly
  `{label}.clickup.com` (a single label under the base domain), the label is a valid DNS label
  (via `NormalizeSubdomain`), and it isn't reserved; otherwise `""`. Stricter than
  `NormalizeSubdomain` (which takes the first label of *any* host) so `www.clickup.com`,
  `a.b.clickup.com`, `clickup.com`, and non-ClickUp hosts all yield `""` — no false positives.
- `SubdomainFromFinalUrl(Uri? finalUrl)` → `SubdomainFromWorkspaceHost(finalUrl?.Host)`; `""` for
  null. This is the seam the probe feeds its post-redirect URL into.

### Probe service — `Services/SubdomainProbe.cs`

- Wraps an injected `HttpClient` (constructed with `AllowAutoRedirect = true` + a short timeout).
- `DetectAsync(ct)` issues a `GET https://app.clickup.com/`, reads the **final** URL from
  `response.RequestMessage?.RequestUri` (reflects the followed redirect chain), and returns
  `ClickUpUrl.SubdomainFromFinalUrl(final)`. Any network / cancellation / parse failure returns
  `""` (best-effort — never throws into the UI).
- `SubdomainProbe.Default()` returns a process-lifetime singleton over a redirect-following
  `HttpClient`, so the default wiring needs no new disposable ownership in `TodoApp`.

Injecting the `HttpClient` keeps `DetectAsync` unit-testable with a fake `HttpMessageHandler` that
sets `RequestMessage.RequestUri` to the simulated final host — no real network in unit tests.

### Opt-in UI — `Tui/Screens/SettingsScreen.cs`

- New optional ctor param `Func<CancellationToken, Task<string>>? detectSubdomain`. When non-null,
  a **"Detect"** `Button` sits next to the existing "ClickUp subdomain" field (Y=8). Clicking it
  runs the detector off the UI thread (`Task.Run` → `Application.Invoke`, the app's established
  async-from-UI pattern), fills the field on a non-empty result, and reports "none" otherwise.
- Stays inside the existing Settings modal — **no second focusable pane** (#3), **no new bare-letter
  shortcut** (#12). The button is reached by Tab like the other Settings buttons.
- Save path is unchanged: the field is still normalized via `ClickUpUrl.NormalizeSubdomain` into
  `SettingsResult.WorkspaceSubdomain`, persisted by `TodoApp`'s F2 close handler.

### Wiring — `Tui/TodoApp.cs`

- New optional ctor param `Func<CancellationToken, Task<string>>? subdomainDetector = null`,
  defaulting to `SubdomainProbe.Default().DetectAsync`. Passed into `SettingsScreen`. Mirrors the
  `IBrowserLauncher` injection #304 added, so the E2E harness can substitute a deterministic
  detector.

### Invariants preserved

- **Generated client / curated spec untouched** — option 3 ruled out; detection is a local HTTP
  probe + pure string transform, no ClickUp API surface change.
- **Auth quirk untouched** — the probe is an unauthenticated web request; it doesn't go through
  `ClickUpTokenAuthProvider` and adds no token handling.
- **No second focusable pane; bare letters reserved for type-ahead.**

## Phases

### Phase 1 — pure seam + probe + tests (no UI)
- `ClickUpUrl.ReservedSubdomains` / `SubdomainFromWorkspaceHost` / `SubdomainFromFinalUrl`.
- `Services/SubdomainProbe.cs`.
- `ClickUpUrlTests`: workspace-host extraction (label / reserved / multi-label / non-clickup /
  null / scheme forms). `SubdomainProbeTests`: fake handler → label / stays-on-app → "" /
  throws → "". `SubdomainProbeIntegrationTests` (`SkippableFact`, env-gated on
  `CLICKUP_SUBDOMAIN_PROBE`; asserts equality when `CLICKUP_EXPECTED_SUBDOMAIN` is set).

### Phase 2 — opt-in Settings affordance + E2E
- `SettingsScreen` Detect button; `TodoApp` detector injection.
- E2E harness: `E2E_DETECT_SUBDOMAIN` injects a canned detector; `subdomain_detect_check.py`
  opens Settings, presses Detect, and asserts the field fills with the injected value.

## Deferred (tracked)

- **A `--detect-subdomain` CLI one-shot** and/or **first-run auto-probe** — a further convenience
  once the redirect behaviour is confirmed on real networks. Out of scope here; note in the PR.
</content>
</invoke>
