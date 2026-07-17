# OAuth sign-in (user-supplied ClickUp app) — issue #1

## Goal & scope

Issue #1 asks to revisit OAuth sign-in **without** putting a client secret in the
public repo. The accepted approach (issue's recommended **option 1**) is a
**user-supplied app**: each user registers their own ClickUp OAuth app and provides
`client_id` / `client_secret` via env vars or a local gitignored config file.

This run ships the **security-sensitive, headless-verifiable core** only. It touches
**neither** `ClickUp/Generated/` **nor** the curated `clickup-openapi.json`, so it
collides with no in-flight spec/regen PR.

### In scope (this PR)

1. **`ClickUpOAuthAuthProvider`** (`ClickUp/`) — a Kiota `IAuthenticationProvider`
   that attaches `Authorization: Bearer <access_token>`, host-restricted to
   `api.clickup.com`. This is the OAuth counterpart to `ClickUpTokenAuthProvider`,
   which deliberately sends the **raw** personal token (no `Bearer`) — that provider
   is left untouched. Per issue #1: "OAuth access tokens use `Authorization: Bearer`
   (note: this differs from the personal-token provider, which sends the raw token)."
2. **`OAuthAppCredentials`** + **`OAuthAppCredentialStore`** (`Configuration/`) —
   resolves the user-supplied app credentials, **env vars first**
   (`CLICKUP_OAUTH_CLIENT_ID` / `CLICKUP_OAUTH_CLIENT_SECRET`), then a local
   **gitignored** `oauth-app.json` in the config dir. Returns `null` when neither
   source provides a complete pair. Nothing secret ever lands in the repo.
3. **`ClickUpOAuth`** (`ClickUp/`) — pure authorize-URL builder
   (`BuildAuthorizeUrl`) + `ExchangeCodeForTokenAsync` (auth code → access token)
   against ClickUp's `POST /api/v2/oauth/token`. The `HttpClient` is injected so the
   exchange is unit-tested fully offline (stub `HttpMessageHandler`).
4. **Seam in `ClickUpClient`** — add an `IAuthenticationProvider` constructor; the
   existing `string token` constructor delegates to it via
   `new ClickUpTokenAuthProvider(token)`. No change to current startup behavior; an
   OAuth token can now drive the same client.

### Deferred to a follow-up issue (interactive — cannot be verified headlessly)

- `SetupWizard` OAuth branch: browser launch → `http://localhost:<port>/callback`
  listener + manual paste-code fallback.
- Provider selection at startup (personal token vs OAuth) and persisting the OAuth
  access token (reusing `TokenStore`, or a sibling).

A new issue will be filed for this and linked from the PR.

## ClickUp OAuth specifics (from ClickUp's API reference)

- **Authorize URL:** `https://app.clickup.com/api?client_id=<id>&redirect_uri=<uri>`
  (an optional `state` is supported and recommended for CSRF protection).
- **Token exchange:** `POST https://api.clickup.com/api/v2/oauth/token` with
  `client_id`, `client_secret`, and `code` as query parameters; the success body is
  `{ "access_token": "<token>", "token_type": "Bearer" }`. Error bodies look like
  `{ "err": "...", "ECODE": "..." }`.

> Note on `Bearer`: the personal-token path sends the token raw; the issue specifies
> the OAuth access token is sent with the `Bearer` prefix. This slice follows the
> issue. The provider normalises a caller-supplied `Bearer ` prefix so the header is
> never doubled, and the XML doc flags the distinction so the wiring run can confirm
> against the live API.

## Tests (xUnit, all run in CI — no network)

- `ClickUpOAuthAuthProviderTests`: adds `Bearer <token>` for the ClickUp host; skips
  a non-ClickUp host; replaces (not duplicates) an existing `Authorization` header;
  strips a caller-supplied `Bearer ` prefix; throws on empty token.
- `OAuthAppCredentialStoreTests`: `null` when no source; env-var pair wins; falls
  back to `oauth-app.json`; `null` when only one half present and no file; `null` for
  a file missing a field; round-trips a written file.
- `ClickUpOAuthTests`: `BuildAuthorizeUrl` composes + escapes query (incl. `state`)
  and throws on empty args; `ExchangeCodeForTokenAsync` returns the `access_token`,
  sends `client_id`/`client_secret`/`code` on the request, throws (with body) on a
  non-success status, and throws when `access_token` is absent — all via a stub
  `HttpMessageHandler`.

## Quality gate (per phase, from repo root)

```
dotnet build clickup-todo.slnx -c Release   # 0 warnings / 0 errors
dotnet test  clickup-todo.slnx -c Release   # all green; integration skips w/o token
dotnet format clickup-todo.slnx
```

## Phases

1. **Core + tests** — auth provider, credential store, OAuth helper, client seam,
   `.gitignore` entry for `oauth-app.json`, full unit tests. (Single cohesive slice.)
2. Open draft PR, run review subagent, address findings, mark ready.
