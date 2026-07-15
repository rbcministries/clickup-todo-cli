# Task Detail `Ctrl+N` — compose & post a new Comment (#216)

Part of the *Writing New Content* epic (#208), sub-issue **G**. Depends on the create-comment
facade **#210** (`CreateTaskCommentAsync`), already merged on `main`.

## Goal

Let the user write and post a **plain-text** comment from the Task Detail screen with `Ctrl+N`.
On success the comment appears in the Comments/Stream tabs immediately (optimistic append,
reconciled from the server response); on failure the append is reverted and the error flashed.
Rich content (@-mentions, task links, entity tagging) is an explicit non-goal here.

## Design

Mirror the two established precedents that already live in `TaskDetailScreen`:

- **Inline overlay** like the Dispatch pane (`Ctrl+A`): a bottom-anchored `FrameView`, hidden until
  `Ctrl+N`, so the task context stays visible. A transient child view within the single already-open
  screen — no nested run-loop, no second screen (keeps the #3/#38 single-toplevel model).
- **Multi-line editor + default button** like `PromptTemplateEditorScreen`: a `TextView`
  (`TabKeyAddsTab = false`, `WordWrap = true`) so `Enter` inserts a newline, with a **Post** button
  (`IsDefault = true`) + **Cancel** button reachable by `Tab`. `Ctrl+Enter` is wired as an extra
  submit shortcut (best-effort; on drivers that fold it into `Enter` it just inserts a newline —
  harmless). This keeps submit driver-robust (Tab→Post→Enter always works, and is `tui-validate`-able).

### Optimistic append / revert (mirrors `ApplyStatus`)

The comment list is owned by the screen (`_comments`). The host owns the ClickUp write via an
**injected async callback** (the #212/#229/#231 `applyAsync`/`createAsync` pattern):
`Func<string, CancellationToken, Task<CommentItem>>? postCommentAsync`.

On submit (non-empty, trimmed text):

1. Hide the composer, build a **provisional** `CommentItem` (client sentinel id `__pending__{n}`,
   empty author, client "now" timestamp so it sorts as newest), append it, re-render via
   `UpdateData`, flash `Posting comment…`.
2. Off-thread `postCommentAsync`. On success, **reconcile** the provisional → the server-confirmed
   `CommentItem` (matched by sentinel id), re-render, flash `Comment posted.`
3. On failure, **revert** (drop the provisional), re-render, flash the error.

The provisional renders as `(unknown)` author until the 30s auto-refresh / `Ctrl+R` pulls the
authoritative author — consistent with the facade's documented minimal-response mapping (#210) and
self-healing, exactly like the Quick Updates status/priority reconcile. A background refresh that
lands mid-post can drop the provisional before reconcile finds it; the next refresh re-pulls the
real posted comment, so it self-heals (same superseded-continuation model the status path accepts).

### Pure model — `CommentComposerModel`

Decision-free logic (unit-tested, no Terminal.Gui), sibling of `DispatchPaneModel`:

- `Route(ComposerKey) → ComposerAction` (`Submit`→`Post`, `Cancel`→`Cancel`, else `PassThrough`).
- `IsPostable(text)` / `Normalize(text)` — non-whitespace gate + trim.
- `Provisional(id, text, dateMs)` — builds the optimistic `CommentItem`.
- `Append` / `Reconcile(provisionalId, confirmed)` / `Revert(provisionalId)` — immutable list transforms.

## Invariants preserved

- **No curated-spec / Kiota / `Generated/` change** — the create-comment facade already exists (#210).
- **No second focusable pane on the main list (#3):** the composer is a transient overlay inside the
  detail screen; the main dashboard stays a single sectioned `ListView`.
- **No bare-letter keybinding (#12):** launch is the `Ctrl+N` chord; inside the composer only
  Tab/Shift+Tab/Enter(newline)/Ctrl+Enter/Esc. `Ctrl+N` confirmed free in `TaskDetailScreen.OnKey`.
- Personal-token raw `Authorization` header untouched; integration tests stay `SkippableFact`.

## Phases

1. **Service seam + pure model + unit tests** — `TaskService.CreateTaskCommentAsync`;
   `CommentComposerModel` + `CommentComposerModelTests`.
2. **TUI composer + host wiring + help** — composer overlay & `Ctrl+N` in `TaskDetailScreen`;
   `postCommentAsync` wired in `TodoApp.OpenTaskDetail`; `Ctrl+N` entry in `HelpItemSets.Detail`.
3. **`tui-validate` + finalize** — teach the fake backend to answer `POST /task/{id}/comment` with a
   created-comment shape; add a `detail_comment_check.py` drive script; run the A/B + latency guards.

## Acceptance criteria (from #216)

- `Ctrl+N` on Task Detail opens a comment composer; posting adds the comment to the Comments/Stream tab.
- Optimistic append on submit; revert + error flash on server failure; empty body is a no-op.
- Plain text only; `Ctrl+N` registered in `HelpItemSets.Detail`; editor doesn't swallow Esc/F1.
- `dotnet test` green (pure model unit-tested), then `tui-validate` confirms open → type → post → appears.

## Deferred (tracked)

- Rich comment content (@-mentions, task links, entity pickers) → the follow-up epic named in #216/#210.
