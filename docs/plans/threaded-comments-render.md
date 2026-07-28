# Plan — #329: render threaded comments with nesting/indentation (Threaded comments C)

Part of epic **#314**. Depends on **B (#328)** — merged (PR #371): `CommentItem` already carries
`ParentCommentId` + a loaded `Replies` collection, and the four detail-view load sites already call
`TaskService.GetTaskCommentsWithRepliesAsync`, so **the thread data is present on every
`CommentItem` the detail view renders**. This slice is the *render* half: turn that latent data into
a visibly nested thread. No API, spec, client, or service change.

## Verified current state (repo)

- `TaskDetailFormatter.Comments` (`Tui/TaskDetailFormatter.cs:79-80`) and `.Stream` (`:103-111`) map
  `OrderComments(...)` (`:117-124`, date-then-id sort) → `CommentBlock` (`:129-140`,
  `author · date · [resolved]` header + trimmed body) and join every block with the heavy
  `CommentSeparator` rule via `JoinBlocks` (`:158-159`). **Flat, thread-unaware** — no indentation
  or reply markers.
- The formatter's input list is **top-level comments only**; a parent's replies live nested in
  `CommentItem.Replies` (oldest-first), never flattened into the list (`CommentThreadLoader`
  `Services/CommentThreadLoader.cs`). So `OrderComments` already operates on exactly the top-level
  set — the nesting is a per-comment expansion, not a re-sort.
- `FeedRowFormatter.Format(CommentItem)` (`Tui/FeedRowFormatter.cs:75-100`) renders one flat line;
  the feed does **not** load replies (a feed-wide fan-out would be an unbounded storm, deliberately
  skipped in #328), but every feed `CommentItem` carries an accurate `ReplyCount` via `MapComment`.
- The E2E fake backend (`tests/ClickUpTodo.Tui.E2E/Program.cs`) serves `GET /task/{id}/comment`
  (`CommentsJson`) but has **no** `GET /comment/{id}/reply` route and seeds no `reply_count`, so no
  scenario exercises a thread yet (the #328 PR deferred that here).

## Design

### 1. Formatter (pure, unit-tested) — the core of the issue

Replace the per-comment `CommentBlock` projection in both `Comments` and `Stream` with a **thread**
projection that renders a top-level comment followed by its replies, depth-first, each reply
indented under its parent:

- `InActivityOrder(items, sort)` — extract the existing `OrderComments` body into a reusable helper
  (oldest-first for `Ascending`, its exact reverse for `Descending`; undated sorts as oldest, id
  tiebreak). Used for the **top-level** list *and*, within a thread, for a parent's `Replies` — so
  the issue's "preserving the chosen ascending/descending order within a thread" holds and the
  formatter is self-contained (doesn't rely on the loader's order). **The parent always renders
  first** (it is the thread root); only the replies' internal order follows the sort.
- `Thread(comment, sort)` — flattens the comment and its (recursively ordered) replies into
  `(depth, block)` pairs top-down, indents each block by depth, and joins them with an
  **intra-thread** separator that is a blank line only (`"\n\n"`) — deliberately **not** the heavy
  `CommentSeparator` rule, so a thread coheres and the heavy rule stays a *thread* boundary.
- `IndentBlock(block, depth)` — for `depth ≥ 1`, prefix the block's header line with
  `ReplyMarker` (`"↳ "`, 2 cells) and every body line with an equal-width blank, both preceded by
  `IndentUnit` (`"  "`) per additional level, so the reply body aligns under the reply header text.
  `depth 0` (a top-level comment / the Stream Description block) is returned unchanged.
- `Comments`/`Stream` then `JoinBlocks(... .Select(c => Thread(c, sort)))` — the heavy rule now
  separates **threads** (and the Stream Description block), never intra-thread replies.

Invariant preserved: a comment **without** replies expands to exactly its old `CommentBlock`, so
every existing no-reply test (separator counts, ordering, Comments↔Stream byte-equality) is
unchanged. ClickUp threads are one level deep (the reply endpoint returns a flat reply list), but
the recursion handles any depth uniformly.

`ReplyMarker` is exposed `public const` (like `CommentSeparator`) so unit tests and future callers
can assert against it without hard-coding the glyph.

### 2. Feed treatment (decided + documented)

The feed collapses a thread to a **reply count**, not nested replies: `FeedRowFormatter.Format`
appends `· N repl{y|ies}` after the date (before the truncatable preview, so a long preview can't
clip it) **only when `ReplyCount > 0`**. This is the zero-extra-cost treatment — the feed already
has an accurate `ReplyCount` and never loads reply bodies. Documented in the method's summary and
the README feed section. Rows with no thread are byte-for-byte unchanged.

### 3. E2E fake backend (`tests/ClickUpTodo.Tui.E2E/Program.cs`)

Behind a new `E2E_THREADS=1` knob (mirrors `E2E_VARY_COMMENTS` gating, so **every existing scenario
is untouched**):

- `CommentsJson` stamps comment **c2** with `reply_count = "2"`.
- A new `GET /comment/{id}/reply` route returns a two-reply `CommentsResponse` for `c2` (empty for
  any other id) — the same wire shape `GetThreadedCommentsAsync` reads. The real
  `CommentThreadLoader` then fetches and nests them, so the running app renders a genuine thread.

### 4. tui-validate (`thread_check.py`, documented in the skill)

New self-contained check: boot → Enter (detail, Stream tab default) → `Ctrl+→` to the Comments tab
→ scroll, accumulating the pyte screen → assert the indented reply marker (`^\s+↳`) and both reply
bodies are visible, i.e. the thread renders **nested**, not flat. Sets `E2E_THREADS=1` itself.
Run only after `dotnet test` is green (per `CLAUDE.md`).

## Phases

1. **Formatter + unit tests** — `TaskDetailFormatter` nesting; `FeedRowFormatter` reply count; tests
   in `TaskDetailFormatterTests` / `FeedRowFormatterTests`. Commit, push → draft PR.
2. **E2E backend + `thread_check.py` + docs** — fake reply route behind `E2E_THREADS`, the new
   check, README feed note, skill entry. Commit, push. Run `tui-validate`.

## Acceptance criteria (from the issue)

- Replies render visibly nested under their parent in Comments and Stream; a thread reads as a
  thread, not a flat run. ✔ (§1)
- Comments without replies look as they do today. ✔ (no-reply expansion == old `CommentBlock`;
  existing tests pin it)
- `dotnet test` green (formatter unit tests for nested output), then `tui-validate` asserts the
  indented/threaded layout. ✔ (§1, §4)

## Out of scope (tracked elsewhere)

- Posting a reply from the composer — **D / #330**.
- Collapsing/expanding a thread interactively — not requested; the render is static. If wanted
  later, a follow-up issue.
