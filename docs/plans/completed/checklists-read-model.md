# Checklists (A): read model

Issue #454 — foundation slice of the Task Checklists epic #453. No UI in this
slice: bring ClickUp's native task `checklists` through the curated spec → Kiota
client → stable domain records, populated by the existing `GetTaskDetailAsync`
read. Everything B–G builds on the shape pinned here.

## The wire shape (ClickUp v2 `GET /task/{task_id}`)

Checklists ride on the task response the app **already fetches** — no new
request, no extra round-trip. ClickUp's documented v2 shape:

```jsonc
"checklists": [
  {
    "id": "b8a8-48d8-…",
    "task_id": "9hz",
    "name": "Release steps",
    "orderindex": 0,             // checklist-level: a clean integer
    "resolved": 1,              // count of resolved items
    "unresolved": 2,            // count of unresolved items
    "date_created": "1567780450249",
    "creator": 183,
    "items": [
      {
        "id": "uuid",
        "name": "Cut the tag",
        "orderindex": "0",       // item-level: number OR numeric string (varies)
        "assignee": null,        // null | bare user id | full user object (varies)
        "resolved": false,       // boolean
        "parent": null,          // parent item id (id-pointer nesting)
        "date_created": "…",
        "children": []           // populated-children nesting (may be present)
      }
    ]
  }
]
```

### Inconsistencies this slice must tolerate (per #454's "pin the shape")

The checklist **container** fields (`id`, `name`, `orderindex`, `resolved`,
`unresolved`) are cleanly typed and stable, so they ride as typed Kiota
properties. The wobble is entirely in the **items**, and matches the
`CustomField` precedent (#35) where a loosely-typed sub-shape is read from raw
JSON rather than typed in the spec:

- **`orderindex`** — an integer on the checklist but a number **or** numeric
  string on an item. Read tolerantly to `double?`.
- **`assignee`** — `null` when unassigned, otherwise historically a bare numeric
  id and on newer responses a full user object. Read all three; reuse the
  existing `TaskAssignee` record (id + best-effort display name; empty name when
  only a bare id is present, for a later slice to resolve).
- **nesting** — ClickUp may express it as a `parent` id-pointer on the child,
  a populated `children` array on the parent, or both. Carry **both** so **B**
  is free to reconstruct from `parent` or read `children` directly without a
  re-derivation.

Because the raw item JSON is exactly the kind of loosely-typed shape the repo
already reads out-of-band, the items are **not** typed in the spec: they stay on
the generated `Checklist`'s `AdditionalData` and are read by a pure
`ChecklistReader` over `System.Text.Json`, mirroring `CustomFieldReader` (#35).
This puts every tolerance in one pure, hand-JSON-unit-testable place and keeps
the fragile bits off Kiota's typed deserializer.

> Live capture note: the maintainer's workspace spans 35 spaces, so
> brute-force hunting a task that happens to carry a checklist is not tractable
> in an unattended session, and the ClickUp MCP returns a normalized (not raw)
> view. The design is therefore built against ClickUp's documented v2 shape and
> made defensively tolerant of every inconsistency above; the env-gated
> `SkippableFact` round-trips it against a real task whenever `CLICKUP_TOKEN`
> is present, which is the live-fidelity check B–G inherit.

## Changes

1. **Curated spec** (`clickup-openapi.json`): add a `Checklist` component schema
   (id, name, orderindex:int, resolved:int, unresolved:int) and a `checklists`
   array on `Task`. The item array is intentionally left untyped (AdditionalData)
   — schema `description` says why. No `/checklist…` write paths (they land with
   D–G so an unused endpoint never ships generated).
2. **Regenerate** via `dotnet kiota generate …` (the `scripts/regen-client.ps1`
   command; run directly where `pwsh` is absent). `Generated/` is script-only —
   no hand edits.
3. **Domain records** (`ClickUp/Models.cs`): `TaskChecklist` and
   `TaskChecklistItem`, plus `TaskDetail.Checklists` defaulting to `[]` so every
   existing construction site keeps compiling and a task without checklists is
   indistinguishable from today.
4. **Pure reader** (`ClickUp/ChecklistReader.cs`): raw item JSON → domain items,
   tolerant of the orderindex/assignee/nesting variants; recurses `children`.
5. **Facade** (`ClickUpClient.MapDetail` + `MapChecklist`): map generated →
   domain; the API omitting `checklists` entirely yields `[]`, never a null-deref;
   one malformed checklist degrades to empty items (mirrors `MapCustomField`).

## Tests

- `ChecklistReaderTests` (pure, `Fact`): two checklists incl. nested items and
  mixed resolved state; absent `checklists` → `[]`; `assignee: null`; bare-id
  assignee; user-object assignee; numeric-string vs numeric `orderindex`;
  `parent`- and `children`-expressed nesting.
- `ClickUpClientChecklistMapTests` (pure, `Fact`): deserialize a `TaskObject`
  from hand JSON via Kiota's `JsonParseNodeFactory`, assert
  `MapDetail(t).Checklists` — mirrors `ClickUpClientCustomFieldTests`.
- `ClickUpClientIntegrationTests`: a `SkippableFact` asserting checklists
  round-trip off a real task (skips without `CLICKUP_TOKEN`).

## Out of scope

Rendering (B/C), any write endpoint (D–G), and the `/checklist…` write paths.
