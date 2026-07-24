# Secure token storage at rest on macOS & Linux (#306)

Cross-platform release-readiness epic (#312), sub-issue (1). Gates re-enabling the
`osx-arm64` / `linux-x64` release artifacts.

## Problem

`Configuration/TokenStore.cs` encrypts the ClickUp personal token with **Windows
DPAPI** (current-user scope) only. On every other OS it takes the fallback branch
and writes the token as **raw plaintext bytes** to `token.bin`. Shipping a
macOS/Linux binary that leaves a live API token in cleartext on disk is the main
posture gap for those platforms. The README also overstates the fallback as
"base64-obfuscated" — it is neither encrypted nor base64.

## Decision (minimum acceptable posture)

- **macOS** → the login **Keychain**, via the built-in `security` CLI
  (`add/find/delete-generic-password`). No native interop, no extra NuGet — mirrors
  how open-in-browser (#308) and the terminal launcher (#307) already shell out.
- **Linux** → the **Secret Service** (GNOME Keyring / KWallet) via `secret-tool`
  (libsecret). The secret is written over **stdin**, never argv.
- **Windows** → DPAPI, unchanged.
- **Fallback** (no secret CLI on PATH, or the store errors — headless/SSH, minimal
  containers) → the existing plaintext file, but now **clearly disclosed**: the
  first-run wizard names the exact storage method and, when it is the plaintext
  fallback, prints a one-line warning with the on-disk path. The README documents
  the fallback and how to get the secure path (install `libsecret`/`secret-tool`).

  We deliberately keep the plaintext fallback rather than hard-refusing, so
  headless/SSH users (who work today) don't regress. The posture improvement is:
  on a normal desktop the token now lands in the OS store by default; when it
  can't, the user is told, in plain words, that it's on disk in cleartext.

## Architecture (mirrors the `BrowserLaunchPlanner` + injectable-runner seam)

New `Configuration/Secrets/`:

- **`ISecretBackend`** — `Exists / Load / Save / Delete`, plus `IsSecure` and a
  human `Description` for disclosure.
- **`SecretStorePlanner`** (pure, I/O-free) — `Select(os, exists) → SecretBackendKind`
  (`Dpapi | Keychain | SecretService | Plaintext`) and `IsSecure(kind)`. Unit-tested
  across every OS × CLI-present/absent combination.
- **`ICommandRunner`** + `ProcessCommandRunner` — a thin seam that runs an executable
  with an argv and optional stdin, returning `(ExitCode, StdOut, StdErr)` or `null`
  when the exe isn't found / fails to start. The real process path lives behind the
  seam (not unit-tested, like the launchers); a fake runner drives the backend tests.
- Backends:
  - `DpapiFileSecretBackend` (Windows) — the moved DPAPI logic; file at `token.bin`.
  - `PlaintextFileSecretBackend` — the current plaintext file behaviour.
  - `KeychainSecretBackend` — pure `security` argv construction + exit-code parsing
    (44 = not-found).
  - `SecretServiceSecretBackend` — `secret-tool store/lookup/clear`, secret via stdin,
    exit-code parsing (1/absent output = not-found).

`TokenStore` becomes a **thin facade** over the selected backend, with the OS,
PATH probe and runner injectable for tests:

- `Load()` — read the selected backend; if empty **and** a legacy plaintext/DPAPI
  `token.bin` exists, read it, migrate it into the secure backend, delete the file,
  and return it (so upgrading a macOS/Linux user removes the cleartext file).
- `Save()` — write via the backend; if the backend is a secure store, also remove any
  lingering legacy `token.bin`.
- `Delete()` — clear the backend **and** remove any legacy file (`--reset` parity).
- Exposes `IsSecure` / `StorageDescription` for the wizard's disclosure line.

## Phases

1. **Seam + planner + backends** (`Configuration/Secrets/*`) with unit tests
   (planner selection matrix; each CLI backend's argv/stdin/parse via a fake runner).
2. **`TokenStore` facade + migration + disclosure** — refactor, wire the wizard's
   storage-method line, pin the existing `TokenStore` tests to the file backend
   (deterministic on any host), add facade + migration tests.
3. **Docs + integration test** — correct the README/first-run wording; a
   `SkippableFact` round-trip against the real `secret-tool`/`security` that skips
   when the CLI (or a session bus) is absent, so CI stays green.

## Acceptance criteria mapping

- *Token via OS secret store, or documented+accepted fallback* → Keychain/Secret
  Service by default; disclosed plaintext fallback otherwise. ✔
- *README/first-run wording matches behaviour* → phase 3 + wizard line. ✔
- *Unit/integration coverage mirroring `TokenStore` tests* → phases 1–3. ✔

## Non-goals / deferred

- Notarization / Gatekeeper quarantine & the Linux exec bit → **#310**.
- KWallet-specific handling beyond what `secret-tool` abstracts.
- Encrypting the file fallback itself (e.g. a passphrase-derived key) — out of scope;
  the fallback stays plaintext-but-disclosed.
