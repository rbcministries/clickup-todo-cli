# Plan — New Task Custom Fields (#368): create-time write path + required-field foundation

Follow-up to #249 (read foundation, merged) and #366 (New Task multi-list create, merged),
part of the Writing New Content epic (#208, closed). #249's read slice shipped
`GetListCustomFieldsAsync` → `CustomFieldDefinition` (id/name/type/`required`/options). This
issue covers the remaining, value-side parts: §1 create-time write path + a pure
value-serialization helper, §2 per-type New Task input widgets, §3 required-field enforcement.

## Scope of THIS session (a clean, fully CI-verifiable slice)

Deliver the **value foundation** — everything §2's TUI widgets will consume — as pure,
unit-tested services plus the facade write capability:

- **§1 — create-time value write path.** Carry per-field values on the domain `NewTaskRequest`
  and send them as ClickUp's `custom_fields: [{ id, value }]` array on create.
- **§1 — pure value-serialization helper** (`CustomFieldValueSerializer`) mapping a
  widget-agnostic entry into each field type's ClickUp payload shape.
- **§3 — pure required-field validator** (`CustomFieldRequiredValidator`): given a list's field
  definitions and which field ids the user filled, report the missing **fillable** required
  fields (by name) so a screen can block Save and flash them.
- **Type taxonomy** (`CustomFieldTypes`) shared by the serializer and the validator, mirroring
  the read-side dispatch in `TaskDetailFormatter.CustomFieldValue`.

### Deferred to a follow-up issue (§2 — the interactive widgets)

The per-type **input widgets** on the New Task screen (render a widget per fillable field after
the target list is chosen, re-fetch on list change, collect values, enforce required at Save,
surface server 4xx) are **not** in this slice. That work is a genuine redesign of the New Task
form — today a fixed-row layout anchored to Save with no room for a variable-count field block —
and, being Terminal.Gui, is only verifiable via `tui-validate`, not CI. Splitting keeps this PR
small, fully CI-green, and mergeable, and mirrors how #249 shipped its read foundation ahead of
any UI. The follow-up consumes this slice's serializer + validator unchanged. Tracked in a new
issue linked from the PR.

## §1 design — how the value reaches the wire

ClickUp's create-task endpoint accepts `custom_fields: [{ id, value }]`, where `value` is
loosely typed (varies per field type). Two options the issue names:

1. **AdditionalData (no spec change)** — set `custom_fields` on the generated
   `CreateTaskRequest.AdditionalData` as a Kiota `UntypedNode` tree; the JSON serializer writes
   it verbatim.
2. Model `custom_fields` as an `UntypedNode`-valued property in the curated spec and regen.

**Chosen: option 1 (AdditionalData + `UntypedNode`).** It needs **no spec edit and no regen**
(lowest churn/risk), keeps the generated `CreateTaskRequest` untouched, and mirrors the read
side which already round-trips loosely-typed data through `System.Text.Json` / `UntypedNode`
(#35, `CustomFieldReader`). Verified empirically that `UntypedString`/`UntypedLong`/`UntypedArray`
/`UntypedObject` set via `AdditionalData["custom_fields"]` serialize through Kiota's
`JsonSerializationWriterFactory` to the expected `[{ "id": …, "value": … }]` JSON (string →
string, long → number, array → array). A capturing-`HttpMessageHandler` facade test asserts the
outgoing POST body shape end-to-end through the real generated client — no live API.

- Domain: new `CustomFieldValue(string Id, JsonElement Value)` record; `NewTaskRequest` gains
  `IReadOnlyList<CustomFieldValue> CustomFields = []`. The value is a neutral `JsonElement`
  (matching the read side), so nothing Kiota-shaped escapes into the domain.
- Facade (`CreateTaskAsync`): when `CustomFields` is non-empty, build an `UntypedArray` of
  `UntypedObject { id, value }` (value via a small recursive `JsonElement → UntypedNode`
  converter at the facade boundary) and set it on `request.AdditionalData["custom_fields"]`.
  Empty ⇒ no key (leaves today's behaviour untouched).

## §1 design — the pure serializer

`CustomFieldValueSerializer.Build(CustomFieldDefinition field, CustomFieldEntry entry)` →
`CustomFieldWriteResult` (`Skip` | `Value(CustomFieldValue)` | `Error(message)`), pure and
unit-tested with hand-built inputs (no Kiota type). `CustomFieldEntry` is widget-agnostic:
`Text` (text/number/date/checkbox typed input), `Checked` (checkbox), `SelectedOptionIds`
(drop-down = first, labels = all). Per `field.Type` (lower-cased):

| Type                                   | Payload value        | Notes |
| -------------------------------------- | -------------------- | ----- |
| `text`/`short_text`/`url`/`email`/`phone` | JSON string       | blank ⇒ Skip |
| `number`/`currency`                    | JSON number          | non-numeric ⇒ Error |
| `checkbox`                             | JSON bool            | `Checked`, else parse `Text` (true/false/yes/no/1/0); neither ⇒ Skip |
| `date`                                 | epoch ms (number)    | via `TaskFieldInfo.TryParseNumeric`; unparseable ⇒ Error; blank ⇒ Skip |
| `drop_down`                            | option id (string)   | first selected; none ⇒ Skip |
| `labels`                               | array of option ids  | empty ⇒ Skip |
| read-only/computed & relationship (`formula`, `rollup`, `*_progress`, `multi_key`, `signature`, `location`, `tasks`, `users`, `emoji`, unknown) | — | Skip (not filled here; deferred) |

## §3 design — required-field validator

`CustomFieldRequiredValidator.MissingRequired(fields, filledFieldIds)` → the **names** of
fields that are `Required`, **fillable** (a required computed/relationship field the UI can't
fill must not create an unsatisfiable block), and whose id isn't in `filledFieldIds`. Pure,
order-preserving, unit-tested. The §2 screen calls it at Save; belt-and-braces server-4xx
surfacing lives in §2.

## Hard rules honored

- **No hand edits under `Generated/`; no spec change / no regen** (option 1 above).
- Generated types never escape the facade — the domain carries a neutral `JsonElement`; the
  `UntypedNode` construction lives inside `ClickUpClient`.
- Personal-token raw `Authorization` header untouched. Any credentialed test is a
  `SkippableFact` gated on `CLICKUP_TOKEN`.
- No TUI surface touched in this slice → no `tui-validate` needed here (it lands with §2).

## Phases

1. **§1 write path + serializer + taxonomy** — `CustomFieldValue` + `NewTaskRequest.CustomFields`;
   `CustomFieldTypes`; `CustomFieldValueSerializer`; facade `custom_fields` write + converter;
   unit tests (serializer per-type) + facade capturing-handler tests (wire shape, empty-omission).
   Commit/push → open draft PR.
2. **§3 required validator** — `CustomFieldRequiredValidator` + unit tests. Commit/push.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green, integration self-skips),
  `dotnet format`. No TUI surface → no `tui-validate` this slice.

## Acceptance-criteria mapping

- "values persist on the created task" — the create endpoint now carries `custom_fields`
  (facade + wire-shape test); the end-to-end persistence check lands with §2's widgets that
  populate `NewTaskRequest.CustomFields`.
- "required fields enforced client-side" — the pure validator is delivered and tested; wiring it
  into Save is §2.
- "no hand edits under Generated/; dotnet test green (value-serialization + required-validation
  unit tests)" — satisfied this slice. `tui-validate` coverage lands with §2.
