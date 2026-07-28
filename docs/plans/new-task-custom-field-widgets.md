# Plan — New Task Custom Field input widgets (#395, §2 of #368)

Follow-up to #368 (PR #396, merged), which delivered the **value foundation** — the pure
`CustomFieldValueSerializer` (§1), the pure `CustomFieldRequiredValidator` (§3), the shared
`CustomFieldTypes` taxonomy, and the create-time write path (`NewTaskRequest.CustomFields` +
`CustomFieldValue`, sent as ClickUp's `custom_fields: [{ id, value }]` array by the facade). This
issue covers the remaining **TUI-coupled §2**: render per-type input widgets on the New Task screen,
collect their values, enforce required, and attach them to the create request.

Consumes (already shipped in #368 / #249 — unchanged here):

- `CustomFieldValueSerializer.Build(def, entry)` → `CustomFieldWriteResult` (`Skip`/`Value`/`Error`).
- `CustomFieldRequiredValidator.MissingRequired(defs, filledIds)` → names of unfilled required fields.
- `CustomFieldTypes.Fillable` / `IsFillable(type)` — the widget-fillable type taxonomy.
- `IClickUpClient.GetListCustomFieldsAsync(listId)` → `IReadOnlyList<CustomFieldDefinition>`.
- `NewTaskRequest.CustomFields` (empty ⇒ no `custom_fields` key on the wire).

## The layout problem and the chosen shape

`NewTaskScreen` today is a fixed-row layout anchored to the Save button: Name → Description →
Assignees (`Fill(17)`) → List → Priority → Due → buttons. Every row is spoken for; there is **no
vertical room** for a variable-count custom-field block, and the field count isn't known until the
target list's fields are fetched.

**Chosen: a two-page single screen** (not a second focusable pane on the main list — #3 is about the
task list, not this modal). The New Task screen keeps its base fields as **page 1**. When Save is
pressed and the primary/home list has **fillable** custom fields, the screen fetches them and swaps to
**page 2** — a top-down stack of one widget per fillable field that owns the whole screen area, so an
arbitrary number of fields lays out cleanly. Page 2's Save collects the values, enforces required, and
runs the *same* create path page 1 would have. `Esc` on page 2 returns to page 1.

Why two-page rather than an inline scrollable region or a separate host-owned screen:

- **One create path.** The existing `NewTaskCreator.CreateAsync` orchestration and the busy/error/keep
  -open handling in `TrySave` stay in one place; page 2 only adds `request with { CustomFields = … }`.
- **No host orchestration change.** `TodoApp` still just shows one `NewTaskScreen`; the only new host
  wiring is an injected `fetchListFieldsAsync` delegate.
- **Room for N fields.** Page 2 has the full screen height; no cramming the fixed page-1 rows.
- **Re-fetch on list change falls out for free.** Fields are fetched each time the user advances from
  page 1 → page 2, so changing the list on page 1 (then Save again) re-fetches for the new list.
- **Lists with no fillable fields are unaffected** — Save on page 1 creates directly, exactly as today.

Mirrors the established show/hide-controls precedent in `TaskDetailScreen`'s comment composer.

## Per-type widgets (page 2)

The type → widget map mirrors `CustomFieldTypes.Fillable` and the read-side taxonomy in
`TaskDetailFormatter.CustomFieldValue`:

| Field type                                   | Widget            | Collected into `CustomFieldEntry` |
| -------------------------------------------- | ----------------- | --------------------------------- |
| `text`/`short_text`/`url`/`email`/`phone`    | `TextField`       | `Text`                            |
| `number`/`currency`                          | `TextField`       | `Text`                            |
| `date`                                       | `TextField` (`yyyy-MM-dd`) | `Text`                   |
| `checkbox`                                   | `CheckBox`        | `Checked`                         |
| `drop_down`                                  | single-select `ListView` over the field's options (+ a "(none)" clear row) | first `SelectedOptionIds` |
| `labels`                                     | multi-select (`CheckBox` per option) | all `SelectedOptionIds`  |

Non-fillable/computed/relationship types (`formula`, `rollup`, `users`, `tasks`, …) are filtered out
before rendering — the serializer and required-validator already skip them, so they never render and
never block Save. Required fields are marked with a trailing `*` in their label.

## Pure collection model (CI-testable)

`NewTaskCustomFieldForm` (pure, alongside `NewTaskForm`) composes the two #368 helpers into one
screen-ready call so the aggregation logic is unit-tested without a terminal:

```
NewTaskCustomFieldForm.Collect(
    IReadOnlyList<CustomFieldDefinition> fields,          // the list's fields (any types)
    IReadOnlyDictionary<string, CustomFieldEntry> entries // widget inputs, keyed by field id
) -> NewTaskCustomFieldResult(
        IReadOnlyList<CustomFieldValue> Values,           // fields that produced a value
        IReadOnlyList<string> MissingRequired,            // required + fillable + unfilled, by name
        IReadOnlyList<string> Errors)                     // per-field validation messages
```

Logic (order-preserving):

- For each field, `CustomFieldValueSerializer.Build(field, entry ?? empty)`:
  - `Value` → add to `Values`, mark the id **filled**.
  - `Error` → add the message to `Errors`.
  - `Skip` → nothing.
- `MissingRequired = CustomFieldRequiredValidator.MissingRequired(fields, filledIds)`.
- `IsValid` ⇔ `Errors` and `MissingRequired` are both empty.

The screen calls `Collect` at page-2 Save; when valid it attaches `Values` and creates, otherwise it
flashes the first `Error` (specific), else the missing-required names, and keeps the form open. A field
that produced an `Error` is also (correctly) unfilled, so it can double-report as missing-required;
the screen surfaces `Errors` first so the more specific message wins.

## Service passthrough

`TaskService.GetListCustomFieldsAsync(listId, ct)` — a thin passthrough to the facade (mirroring
`CreateTaskAsync`/`AddTaskToListAsync`), so the screen depends only on `TaskService` like every other
screen. Unit-tested against a fake `IClickUpClient`.

## Server-side 4xx surfacing

Belt-and-braces: a create rejection (e.g. a server-enforced required field) already flows through
`TrySave`'s `catch` → `Flash($"Couldn't create task: {FirstLine(ex.Message)}")`, where the
`ClickUpApiException` message carries ClickUp's error text (naming the field). No extra path needed;
the PR notes this.

## Phases

1. **Pure + service (CI-green).** `NewTaskCustomFieldForm` + unit tests;
   `TaskService.GetListCustomFieldsAsync` passthrough + test. Build/test/format green → draft PR.
2. **TUI (build + `tui-validate`).** Two-page `NewTaskScreen`: fetch fillable fields for the primary
   list, render per-type widgets, collect via `NewTaskCustomFieldForm`, enforce required/errors, attach
   `CustomFields`, keep-open on failure. Host wires `fetchListFieldsAsync`.
3. **E2E.** Env-gated (`E2E_CUSTOM_FIELDS=1`) `GET /list/{id}/field` in the `FakeClickUp` handler;
   extend/clone `new_task_check.py` to drive a text/number/drop-down field + the required-block path,
   asserting the value round-trips through the create POST.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green; integration self-skips),
  `dotnet format`.
- `tui-validate` (`new_task_check.py` / a custom-fields sibling) after `dotnet test` is green.

## Hard rules honored

- **No hand edits under `Generated/`; no spec change / no regen** — this is UI + a passthrough over the
  existing #368/#249 facade surface.
- No generated type escapes the facade; the screen collects widget-agnostic `CustomFieldEntry`s and the
  pure serializer produces neutral `CustomFieldValue`s.
- Single sectioned `ListView` main-list model untouched; no second focusable pane on the list (#3).
- Personal-token raw `Authorization` header untouched; no new integration test needs credentials.

## Deferred (tracked separately if pursued)

- Rich pickers for relationship/computed types (`users`/`tasks`/`location`/`emoji`/`signature`) — out
  of the fillable taxonomy by design.
- If page 2's field stack ever exceeds the screen height on a very small terminal, `PgUp`/`PgDn`
  scrolling of the field region (the stack lays out top-down today).
