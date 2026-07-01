# Plan — Detail view: render custom-field values (loosely-typed) (issue #35)

## Goal
The detail view's **Other attributes** tab currently lists each custom field's
**name** and **type** but not its **value**. Surface the value, formatted
per-type for a terminal, with a robust fallback for unknown/unsupported types.

## Current behavior
- `CustomFieldItem(Name, Type)` carries only identity — the loosely-typed
  `value` lands in Kiota's `AdditionalData` and is dropped in `ClickUpClient.MapDetail`.
- The curated `CustomField` spec types only `id`/`name`/`type`.
- `TaskDetailFormatter.OtherAttributes` renders `• Name  (type)` per field.

## Acceptance criteria (from issue)
- Read `value` robustly.
- Format common field types for a terminal: text, number, date, drop-down
  (map option id/orderindex → option name via `type_config.options`), labels,
  users, checkbox/currency where cheap.
- Keep rendering in a **pure, unit-tested** helper (extend `TaskDetailFormatter`).
- Unknown/unsupported types fall back to a compact stringified value.

## Design

### Why no spec change / no regen (collision-free)
`value` is irreducibly polymorphic, so it lands in Kiota's `AdditionalData`
whichever path we take — a spec `value: {}` still generates an `UntypedNode`.
Editing the curated spec + regenerating would **collide head-on** with the
in-flight spec+regen PRs #43 (Created date) and #48 (Priority), which the issue
warns about. So we touch **neither `Generated/` nor the curated spec**.

A concurrent session verified (Kiota `Microsoft.Kiota.Bundle` 2.0.0) that
serializing the generated `CustomField` back to JSON **reproduces `value` and
`type_config` exactly** (`Serialize` writes `AdditionalData`). So the robust,
version-proof read is: serialize each `CustomField` to a `System.Text.Json`
`JsonElement` at the `ClickUpClient` boundary (one framework line, via
`JsonSerializationWriterFactory` directly to avoid registry assumptions) and
pluck `value` + `type_config.options` from it. No generated type escapes the
facade; nothing depends on the internal `UntypedNode` shape.

### Phase 1 — model + boundary reader + mapping
- Domain model (`ClickUp/Models.cs`):
  - `CustomFieldOption(string? Id, string? Name, double? OrderIndex)`.
  - Extend `CustomFieldItem` with `JsonElement? Value` and
    `IReadOnlyList<CustomFieldOption> Options` (default empty) — additive,
    existing callers unaffected.
- New pure `ClickUp/CustomFieldReader.Read(JsonElement field)` →
  `(JsonElement? Value, IReadOnlyList<CustomFieldOption> Options)`. `Value` is
  the cloned `value` prop (null when JSON-null/absent); `Options` is
  `type_config.options[]` mapped to `id`, `name` (falling back to `label` for
  labels-type options), and numeric `orderindex`. Pure → unit-tested with
  hand-built JSON, no Kiota type in the test.
- `ClickUpClient`: an `internal static JsonElement SerializeToJson(IParsable)`
  (the one Kiota-touching line) so `MapDetail` maps each `CustomField` via
  `CustomFieldReader.Read`.

### Phase 2 — pure formatter + tests
- `TaskDetailFormatter.CustomFieldValue(CustomFieldItem) -> string?` (pure):
  dispatch on `Type` then `Value.ValueKind` (returns null when no value so the
  caller omits it):
  - `drop_down` → match `Options` by orderindex (number) or id (string) → option name.
  - `labels` → array of option ids → mapped names, comma-joined.
  - `users` → array of user objects → username/email/id, comma-joined.
  - `date` → epoch-ms (string/number) → local date.
  - `checkbox` → bool → Yes/No.
  - `number`/`currency`/`emoji`(rating) → numeric → string (currency keeps the number).
  - text-ish (`text`,`short_text`,`url`,`email`,`phone`,`location`) → string as-is.
  - unknown/parse-failure → compact stringified JSON (raw value), never throws.
  - empty/absent value → `—`.
- `OtherAttributes`: append `: <value>` after each field's name/type.
- Tests in `TaskDetailFormatterTests` for every branch incl. id-vs-orderindex
  drop-down match, label/user arrays, malformed JSON fallback, null value, and
  unknown type.

## Out of scope / deferred (own issues)
- Multi-list membership (`locations`) — #36.
- Color rendering of option/label swatches — not needed for text terminal.

## Verification
- `dotnet build -c Release` (0 warn/0 err), `dotnet test -c Release` (integration
  skips without creds), `dotnet format`.
- TUI not unit-testable in CI: the change is richer text in the existing "Other"
  pane (no new pane, single-ListView model untouched). Manual: open a task with
  custom fields, press the detail key, Tab to Other, confirm values render.
