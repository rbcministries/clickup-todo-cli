# Task Detail (K): wire the @-mention picker into the comment composer (#325)

Epic #313, sub-issue **K**. Depends on **H** (#322, structured comment write — merged)
and **J** (#324, `MentionPickerView` — merged); consumed later by **L** (#326).

## Goal

Let a user `@`-mention workspace members while composing a comment (Ctrl+N in Task
Detail). Typing `@` opens the existing mention picker; picking a member inserts a
visible `@DisplayName` token at the caret and the composer submits a **structured**
comment (`IReadOnlyList<CommentRun>`) so ClickUp records a real mention. Plain comments
(no mention) keep posting through the unchanged plain-text path.

## Acceptance criteria (from the issue)

- Composing a comment, the user can @-mention one or more members; the posted comment is
  a real ClickUp mention; the optimistic local echo reflects it and reconciles/reverts.
- Plain comments (no mention) still post via the text path unchanged.
- `dotnet test` green, then `tui-validate` drives `Ctrl+N` → trigger → pick → post.

## Verified current state

- **Composer** (`Tui/Screens/TaskDetailScreen.cs`): `Ctrl+N` → `ShowCommentComposer` over a
  `TextView` `_commentEditor` (`:554`), controls `[_commentEditor, Post, Cancel]`; keys via
  `OnCommentKey`/`ClassifyComposer` (`:1276`,`:1317`); `PostComment` (`:1369`) does the
  optimistic append + reconcile/revert through injected `_postCommentAsync` (`:105`,`:346`);
  pure `CommentComposerModel` (`Tui/Screens/CommentComposerModel.cs`).
- **Structured write substrate is done (H, #322):** `IClickUpClient.CreateTaskCommentAsync(
  taskId, IReadOnlyList<CommentRun>, ct)` (`IClickUpClient.cs:115`) and its `ClickUpClient`
  impl (maps `CommentRun.Mention` → `{type:"tag",user:{id}}`). `CommentRun` union at
  `Models.cs:311`. **But `TaskService` only has the plain-`string` overload** (`:713`) — needs a
  passthrough.
- **Mention picker is done (J, #324):** `MentionPickerView : SelectorView` wants
  `Func<string, ISet<long>, IReadOnlyList<WorkspaceMember>> match` / `topFrequent`, and raises
  `event EventHandler<MentionTarget>? MemberPicked` (`MentionPickerView.cs:71`), where
  `MentionTarget(long UserId, string DisplayName)`.
- **Member pool:** `TodoApp` holds `AssigneeFrequencyCache _assignees` (`:59`) exposing
  `Match(query, ISet<long>? exclude)` / `TopMostFrequent(n, exclude)` — **`TaskAssignee`-typed**.
  A `TaskAssignee(Id, Name)` maps to `WorkspaceMember(Id, Name, null)` (DisplayName ⇐ Name),
  which is exactly the pool + shape the picker needs — **no new fetch/roster service**.
- **Hosts:** the E2E/`tui-validate` harness boots **`TodoApp` by default**
  (`E2E/Program.cs:102`); `SingleTaskApp` only under `E2E_SINGLE_TASK`. `SingleTaskApp` has
  **no `AssigneeFrequencyCache`** (no member pool).
- **Footer:** `HelpLine.DetailCommentComposer` (`:248`) is the composer overlay footer, chosen
  by `DetailFooter(commentComposerVisible, …)` (`:262`). Composer-internal keys are routed in
  `OnCommentKey`, not through the `Keybindings` table.

## Design

### Scope / deferral

Wire mention authoring in the **dashboard host (`TodoApp`)** only, where the member pool
already exists. `SingleTaskApp` keeps `null` member/structured seams → its composer stays
plain-text, **byte-identical to today**. The composer glue is host-agnostic and lights up in
single-task mode once that host supplies member seams — tracked as a follow-up issue linked
from the PR. `#325`'s AC (the composer, the default host, the `tui-validate` pass) is fully met
by the dashboard host.

### Pure logic — `CommentComposerModel` additions (unit-tested)

- `record MentionToken(long UserId, string DisplayName)` — an inserted mention; token text
  (`.Token`) is `"@" + DisplayName`. (Insertion itself is done by the editor's own
  `TextView.InsertText`, which handles the caret/word-wrap model — no manual caret arithmetic.)
- `BuildRuns(string text, IReadOnlyList<MentionToken> tokens) → IReadOnlyList<CommentRun>`:
  greedy left-to-right scan. At each position, among not-yet-consumed tokens whose
  `"@{DisplayName}"` matches at that position, take the **longest** (so `@Ann` vs `@Ann Marie`
  disambiguate); flush accumulated literal text as a `CommentRun.Text`, emit
  `CommentRun.Mention(UserId)`, advance and mark the token consumed; else accumulate the char.
  A token the user deleted from the text simply never matches → the mention safely degrades to
  literal text (no wrong tag). Adjacent literal text is coalesced.
- `TrimRuns(runs)`: trims the leading `Text` run's start and the trailing `Text` run's end and
  drops any run that becomes empty — the structured analogue of the plain path's `Normalize`.
- `HasMention(runs) => runs.Any(r => r is CommentRun.Mention)`.

### Facade seam

- `TaskService.CreateTaskCommentAsync(string taskId, IReadOnlyList<CommentRun> runs, ct)` →
  `client.CreateTaskCommentAsync(taskId, runs, ct)` (passthrough; the structured overload
  already exists on the client/interface).

### TUI glue — `TaskDetailScreen`

- **New optional, `null`-defaulted ctor params** (after `loadTaskTreeAsync`):
  `postStructuredCommentAsync: Func<IReadOnlyList<CommentRun>, CancellationToken, Task<CommentItem>>?`,
  `memberMatch: Func<string, ISet<long>, IReadOnlyList<WorkspaceMember>>?`,
  `memberTopFrequent: Func<int, ISet<long>, IReadOnlyList<WorkspaceMember>>?`. The mention
  feature is enabled only when **all three** are present (`_mentionEnabled`).
- **`_mentionBox` overlay** — a bottom-anchored `FrameView` hosting a `MentionPickerView`,
  hidden by default, shown/sized/clamped exactly like `_commentBox` (added to the screen's
  `Add([...])`). A fresh `MentionPickerView` is built per open (no stale query/selection);
  `MemberPicked` and a KeyDown-for-`Esc` are wired on it.
- **`@` trigger** — in `OnCommentKey`, before the passthrough, when `_mentionEnabled` and the
  composer is visible and the rune is `@`: mark handled and `ShowMentionPicker()` (the literal
  `@` is not inserted — the picker inserts the full `@Name` token). Feature off ⇒ `@` types
  normally.
- **On pick** (`MemberPicked` → `MentionTarget`): hide the picker (refocusing the editor),
  `_commentEditor.InsertText("@{DisplayName} ")` at the caret, record the `MentionToken`.
- **`Esc` in the picker** → hide the picker, refocus the editor (does not cancel the composer).
- **`PostComment` routing** — build `runs = TrimRuns(BuildRuns(rawText, _mentionTokens))`. If
  `HasMention(runs)` and `_postStructuredCommentAsync != null`: optimistic provisional from the
  visible text (with `@Name` literals), then write via the structured callback; reconcile/revert
  unchanged. Otherwise the existing plain-text path, unchanged. `_mentionTokens` is cleared on
  each `ShowCommentComposer`.

### Host wiring — `TodoApp` (screen construction ~`:2018`)

```
postStructuredCommentAsync: (runs, ct) => _tasks.CreateTaskCommentAsync(resolvedId, runs, ct),
memberMatch:       (q, ex) => _assignees.Match(q, ex).Select(a => new WorkspaceMember(a.Id, a.Name, null)).ToList(),
memberTopFrequent: (n, ex) => _assignees.TopMostFrequent(n, ex).Select(a => new WorkspaceMember(a.Id, a.Name, null)).ToList(),
```

`SingleTaskApp` left unchanged (seams stay `null`).

### Footer

- Add a mention hint to `HelpLine.DetailCommentComposer` (`@` → `mention`).
- Add a `DetailMentionPicker` set and a `DetailFooter` branch gated on a new
  `mentionPickerVisible` flag (`TaskDetailScreen.HelpItems` passes `_mentionBox?.Visible == true`).
  No `Keybindings`-table change (composer-internal keys aren't table-routed; the cross-check
  test #355 covers mapped chords, which are untouched).

### E2E / `tui-validate`

- `/team` members are already served; the picker pool is `_assignees` (warmed from task
  assignees + top-up).
- Add an optional `E2E_COMMENT_LOG` to the fake `POST /task/{id}/comment` branch that appends
  the raw request body to a file, so a check can assert the **structured `comment` blocks array**
  was actually sent (the real deliverable) — analogous to the existing `E2E_BROWSER_LOG`.
- New `mention_check.py`: open Task Detail → `Ctrl+N` → type `@` → type part of a seeded
  member name → assert a picker row → `Enter` → assert `@Name` in the editor → `Ctrl+Enter` →
  assert the mention renders in the Comments/Stream pane and (via `E2E_COMMENT_LOG`) that the
  posted body carried a `{"type":"tag","user":{"id":…}}` block.
- Regression: `detail_check.py` A/B stays byte-identical (the composer/overlay are hidden until
  `Ctrl+N`); the plain-text post path is unchanged.

## Phases

1. **Pure model + facade + tests** — `CommentComposerModel` mention additions, `TaskService`
   structured passthrough; xUnit for every pure function. Build + test green. First push opens
   the draft PR.
2. **TUI glue + host wiring** — `TaskDetailScreen` ctor params, `_mentionBox`/picker, `@`
   trigger, insertion, `PostComment` routing; `TodoApp` wiring; `HelpLine`. Build + test green.
3. **E2E + tui-validate** — `E2E_COMMENT_LOG` fake echo, `mention_check.py`; run `tui-validate`.
   Mark PR ready.

## Non-goals / deferred

- **Single-task-mode mention authoring** (`SingleTaskApp` has no member pool) — deferred to a
  follow-up issue, linked from the PR.
- **Description-editor mentions** (#326, L) — separate issue; the G spike (#321) found ClickUp
  descriptions carry no structured mention payload, so L is a different write path.
- **@Brain / Super Agents** — not API-addressable (per #321); picker is human members only
  (already the case in the merged #324 picker).
