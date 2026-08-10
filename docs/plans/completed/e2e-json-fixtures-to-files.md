# E2E (B): move JSON fixtures out of `Program.cs` into files

Issue: #486 (sub-issue **B** of the E2E harness epic #484). Removes **append
point 6**.

## Problem

Canned response payloads live as `private const string` literals inside
`FakeClickUp` (`tests/ClickUpTodo.Tui.E2E/Program.cs`):

- `CustomFieldsJson` — a ~430-char **single-line** JSON literal.
- `CustomFieldsManyJson` — a ~600-char **single-line** JSON literal.
- `ChecklistsJson` — a multi-hundred-char canned JSON literal (already
  multi-line, but still a canned static payload clustered in the type body).

Two problems the epic calls out:

1. **Shared anchor** — every scenario needing a new canned payload appends a
   `const` at the same cluster, so scenario PRs collide.
2. **Unreviewable** — a 400+ char single-line JSON literal can't be diffed
   meaningfully; a one-field change reads as a whole-line rewrite.

## What is *in* scope

Only **canned, non-interpolated** JSON payloads — the three `const string`s
above. These are fixed bytes that never depend on a request, so they can live
in a file and be read verbatim.

## What is *out* of scope

The **interpolated** response builders (`TasksJson`, `DetailJson`,
`TreeTaskJson`, `ForeignTaskJson`, `NudgeTaskJson`, `ListJson`, `CommentsJson`,
`RepliesJson`) embed per-request values (`{{id}}`, `{{status}}`, seeded
overlays, fetch counters). They are templates, not canned payloads, so they
stay in code. The short inline literals in the routing chain (`/user`,
create-task echo, empty-list/empty-field bodies, error bodies) are all well
under 200 chars and are the routing chain's concern (append point 5 → sub-issue
D), not this one.

## Proposal

Move each canned payload to its own **pretty-printed** file under
`tests/ClickUpTodo.Tui.E2E/Fixtures/`, embedded as a resource:

```
Fixtures/custom_fields.json
Fixtures/custom_fields_many.json
Fixtures/checklists.json
```

`.csproj` picks them up with a **glob** so adding a fixture needs no project
edit (the project file must not become append point 7):

```xml
<ItemGroup>
  <EmbeddedResource Include="Fixtures\*.json" />
</ItemGroup>
```

A small static loader reads a fixture by name:

```csharp
private static string Fixture(string name) { /* GetManifestResourceStream, defensive lookup */ }
```

The three declarations flip from `const` to `static readonly`, loaded once from
the embedded resource, keeping every use-site (`CustomFieldsJson`,
`CustomFieldsManyJson`, `ChecklistsJson`) byte-for-byte unchanged in the code
that references them:

```csharp
private static readonly string CustomFieldsJson = Fixture("custom_fields");
```

The loader is **defensive**: it resolves the manifest name by suffix match
(`.Fixtures.{name}.json`) and, on a miss, throws listing the available resource
names — so a rename or a RootNamespace surprise fails loudly at first use
instead of returning a null stream.

Pretty-printing the JSON is safe: the app parses these responses with
`System.Text.Json`, which ignores insignificant whitespace, so rendering is
unchanged. The two CustomFields fixtures feed the New Task custom-field page
(gated by `E2E_CUSTOM_FIELDS` / `E2E_CUSTOM_FIELDS_MANY`); `checklists.json`
feeds the Checklists tab (gated by `E2E_CHECKLISTS`). No other check sets those
flags, so every other scenario is untouched.

## Acceptance (from the issue)

- No multi-hundred-character JSON literals remain in `Program.cs`.
- Adding a fixture requires creating one file and editing nothing else — verify
  the glob by adding and removing a scratch fixture and confirming it builds.
- All checks pass; the consumers `custom_field_check.py`,
  `new_task_custom_field_scroll_check.py` (CustomFields) and the checklists
  checks are unaffected.
- `dotnet format --verify-no-changes` clean; `dotnet build -c Release` 0/0.

## Validation

1. `dotnet build clickup-todo.slnx -c Release` — 0 warnings / 0 errors.
2. `dotnet test clickup-todo.slnx -c Release` — green (integration self-skips
   without `CLICKUP_TOKEN`). This project is the PTY host, not an xunit project,
   so its coverage is the tui-validate checks, not `dotnet test`.
3. `tui-validate` the affected checks: `custom_field_check.py`,
   `new_task_custom_field_scroll_check.py`, and the checklists check — to prove
   the embedded-resource load path serves the same payloads.
4. Glob check: drop a scratch `Fixtures/_scratch.json`, build (no `.csproj`
   edit), confirm it's embedded, then remove it.

## Phasing

Small enough for one substantive change:

- **Phase 1** — add fixtures, wire the csproj glob + loader, flip the three
  consts; build + format + `dotnet test`. First push opens the draft PR.
- **Phase 2** — `tui-validate` the affected checks; finalize and mark ready.
