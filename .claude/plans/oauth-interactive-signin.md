# OAuth interactive sign-in (SetupWizard branch) — issue #52

Follow-up to #1 (OAuth **core**, merged in PR #53). The core shipped the
headless-verifiable pieces — `ClickUpOAuthAuthProvider` (`Authorization: Bearer`),
`OAuthAppCredentialStore`, `ClickUpOAuth` (authorize-URL builder + code→token
exchange), and the `ClickUpClient(IAuthenticationProvider)` seam. This issue ships
the **interactive** half that was deliberately deferred because it can't be verified
in a headless CI session.

## Acceptance criteria (from the issue + its refinement comment)

1. **Personal token stays the default.** The paste-a-`pk_`-token flow remains the
   default path a user gets; OAuth is an **opt-in alternative**, never a replacement.
2. **OAuth is only offered when the user supplied their own app credentials**
   (`CLICKUP_OAUTH_CLIENT_ID`/`_SECRET` or `oauth-app.json`). When
   `OAuthAppCredentialStore.Load()` returns `null`, setup silently proceeds with the
   personal-token flow (no OAuth prompt, no error).
3. **Existing personal-token users are unaffected.** A saved personal token keeps
   loading and driving `ClickUpClient(string token)` (raw header) with no migration
   and no re-auth prompt; `--reset` still returns to the personal-token setup.
4. **Config records the active auth mode**, defaulting to personal-token when
   unset/ambiguous, so startup builds the right provider.
5. Interactive flow: build the authorize URL, launch the browser, capture the
   `code` via a **localhost callback listener** with a **manual paste-code
   fallback**, exchange it for an access token, and persist it.

## Decisions (self-recommending items the issue left to the implementer)

- **Token persistence reuses `TokenStore`** (DPAPI on Windows, base64 elsewhere).
  Rationale: identical secret-at-rest handling, one store to maintain, and `--reset`
  already clears `token.bin`. The new `AppConfig.AuthMode` disambiguates which
  provider to construct from that token at startup. No sibling store.
- **Refresh tokens:** ClickUp v2 issues long-lived access tokens and no refresh
  token, so there is nothing to persist/rotate. If that changes it's a follow-up.
- **Redirect URI is fixed, not random-port.** A ClickUp OAuth app registers exactly
  one redirect URL, so a random loopback port would never match. We use a fixed
  default `http://localhost:53682/callback`, overridable via
  `CLICKUP_OAUTH_REDIRECT_URI`. The listener binds that exact port; if the bind
  fails (port busy / locked-down env) we still launch the browser with the same
  registered URI and fall back to **paste the `code`** (from the address bar).
  Documented so users register the matching URL. This keeps `OAuthAppCredentials`
  and its store/tests untouched.
- **Bearer vs raw header:** we follow #1's core (OAuth token → `Bearer`) via
  `ClickUpOAuthAuthProvider`. The live-only question is confirmed by the existing
  env-gated `ClickUpOAuthIntegrationTests` (needs `CLICKUP_OAUTH_CODE`); can't be
  settled headlessly. If raw turns out to be required, the provider selection in
  `ClickUpClientFactory` collapses to `ClickUpTokenAuthProvider` — one-line change.

## What can / can't be tested

Testable (unit, CI-green): `AuthMode` serialization + default; provider selection
(Bearer vs raw, via a capturing `HttpMessageHandler`, mirroring
`ClickUpClientAuthSeamTests`); callback URL parsing (code, `state` match/mismatch,
`error` param, missing code); pasted-input `code` extraction (raw code or full
redirect URL); the loopback listener over a **real local HTTP round-trip**
(`SkippableFact` that skips if `HttpListener` can't bind — no ClickUp needed); the
`OAuthSignIn` orchestration decision logic with injected fakes (listener success →
exchange; state mismatch → abort; paste fallback).

Not headless-testable (verified by build + reasoning, documented in the PR, mirrors
`TerminalLauncher`'s real `Process.Start`): the actual browser launch, the real
ClickUp authorize/redirect round-trip, and the `SetupWizard` console I/O.

## New / changed files

- `Configuration/AuthMode.cs` — `enum AuthMode { PersonalToken, OAuth }`.
- `Configuration/AppConfig.cs` — `AuthMode AuthMode { get; set; } = PersonalToken;`
  (serialized as a string; absent ⇒ PersonalToken).
- `ClickUp/ClickUpClientFactory.cs` — `Create(AppConfig, string token, HttpClient?)`
  picks `ClickUpOAuthAuthProvider` (OAuth) or `ClickUpTokenAuthProvider` (default).
- `Setup/OAuthCallbackListener.cs` — `IOAuthCallbackListener` +
  `LoopbackOAuthCallbackListener`: pure `ParseCallback(Uri, expectedState)`,
  `RedirectUri`, `TryStart()`, `WaitForCodeAsync(...)`, and static
  `ExtractCode(pastedInput)`.
- `Setup/IBrowserLauncher.cs` + `SystemBrowserLauncher.cs` — thin `Process.Start`
  wrapper (the untestable seam), injected so `OAuthSignIn` is testable.
- `Setup/OAuthSignIn.cs` — orchestrates state → authorize URL → browser →
  listener/paste → exchange → token, all seams injected.
- `Setup/SetupWizard.cs` — offer OAuth vs personal-token when creds present
  (default = personal token); run the OAuth flow; set `config.AuthMode`.
- `Program.cs` — build the startup `ClickUpClient` via `ClickUpClientFactory` from
  the stored token + `config.AuthMode`.
- `README.md` — OAuth sign-in section (register app, env vars, redirect URL).
- Tests mirroring `tests/ClickUpTodo.Tests/` patterns.

## Phases

1. **Core seams + tests** — `AuthMode`/config, `ClickUpClientFactory`,
   `OAuthCallbackListener` (parse + live loopback), pasted-code extraction. Full
   unit tests. Push → opens draft PR.
2. **Interactive flow + wiring + tests** — `IBrowserLauncher`, `OAuthSignIn`,
   `SetupWizard` branch, `Program.cs` startup selection. Orchestration tests.
3. **Docs + finalize** — README, plan tidy, full gate, review subagent, mark ready.

## Quality gate (per phase, from repo root)

```
dotnet build clickup-todo.slnx -c Release   # 0 warnings / 0 errors
dotnet test  clickup-todo.slnx -c Release   # all green; integration skips w/o token
dotnet format clickup-todo.slnx
```
</content>
