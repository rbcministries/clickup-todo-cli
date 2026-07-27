# Plan — #382: harden the plaintext token fallback (owner-only `0600` perms)

Issue: [#382](https://github.com/rbcministries/clickup-todo-cli/issues/382) — follow-up to
[#306](https://github.com/rbcministries/clickup-todo-cli/issues/306) (PR #381, merged), part of the
cross-platform epic [#312](https://github.com/rbcministries/clickup-todo-cli/issues/312).

## The decision (AC #1: "a decision recorded")

#306 lands the token in the OS secret store (Windows DPAPI, macOS Keychain, Linux Secret Service) and
falls back to a **disclosed plaintext `token.bin`** only when no store is reachable (headless/SSH,
minimal containers). #382 asks whether — and how — to reduce that fallback's exposure.

The issue lists four candidate directions. Grounding the choice in what each actually buys:

- **Owner-only (`0600`) file permissions on POSIX** — real, always-on protection against *other local
  users* reading the token, at zero usability cost and no prompt. **← chosen (the AC's "at minimum").**
- **Machine-/user-derived-key encryption** — the key must be derivable from data the same reader can
  also read, so it is obfuscation, not protection ("modest improvement over cleartext", per the issue).
  Not worth the complexity. **Won't do.**
- **User-passphrase encryption prompted at launch** — a genuine at-rest win, but it trades away the
  always-on/no-prompt UX and is a product tradeoff (which flows prompt, re-prompt cadence, recovery).
  **Deferred** to a focused, maintainer-reviewed decision rather than made unilaterally in an unattended
  run — tracked by a follow-up issue linked from the PR.
- **Steer users to install `secret-tool` / a keyring** — already done: the wizard discloses the fallback
  and prints an install hint (`InsecureStorageHint`). Unchanged.

So this slice implements the always-on minimum and records the rest as an explicit deferral.

## Scope (AC #2: applied to the fallback path only, unit-tested, docs updated)

- **`FileSecretBackend.TrySave`** (the shared base of the plaintext + DPAPI file backends) writes the
  file **owner-only (`0600`)** on POSIX:
  - The mode is set **at creation** via `FileStreamOptions.UnixCreateMode`, so a fresh `token.bin` is
    never momentarily world-readable (no create-then-`chmod` race).
  - It is **re-applied after the write** via `File.SetUnixFileMode`, so a file left by an older build
    (created at the default umask, e.g. `0644`) is tightened on the next save too.
  - On **Windows** it stays a plain `File.WriteAllBytes` — Unix modes don't apply and the file inherits
    the user-profile directory ACL; the change is guarded by `OperatingSystem.IsWindows()`.
  - Applying it in the base (not just the plaintext subclass) also hardens the DPAPI-encrypted file as
    cheap defense-in-depth; the secret-store backends (Keychain/Secret Service) are untouched.
- **Disclosure wording** (README first-run section) states the `0600` behaviour *and* the residual risk
  — the file is still cleartext at rest, so it's no substitute for the OS store. The wizard's
  `Description` stays honest ("UNENCRYPTED …"); perms are defense-in-depth, not encryption.

## Tests (mirroring `Configuration/Secrets` tests)

`SecretBackendTests` (POSIX-gated `SkippableFact`, self-skip on Windows):

- `Plaintext_Save_WritesOwnerOnlyPermissions_OnPosix` — after `TrySave`, mode is exactly
  `UserRead | UserWrite`.
- `Plaintext_Save_TightensPreexistingLooseFile_OnPosix` — a pre-existing `0644` file is normalized to
  `0600` on the next save, and the token round-trips.

The existing round-trip / cleartext-on-disk / delete tests stay green (perms don't change contents).

## Invariants

- No `Generated/` hand-edit, no curated-spec change, no ClickUp API surface touched (pure config/IO).
- No TUI change — no `tui-validate` needed (only a README line changes in the user-facing surface).
- OS secret-store paths unchanged (non-goal per the issue).

## Deferred (tracked)

- Opt-in **user-passphrase encryption** of the fallback — a usability-vs-always-on product decision —
  tracked by [#426](https://github.com/rbcministries/clickup-todo-cli/issues/426). (Machine-derived-key
  encryption was considered and rejected above as obfuscation, not protection.)
