# Task Detail link model: markdown `[text](url)` spans + custom-id task URLs

Issue: [#356](https://github.com/rbcministries/clickup-todo-cli/issues/356) ·
Follow-up to [#316](https://github.com/rbcministries/clickup-todo-cli/issues/316) ·
Epic: [#313](https://github.com/rbcministries/clickup-todo-cli/issues/313)

Picks up the two extensions #316 explicitly scoped out of the pure link model
(`Tui/TaskLinkExtractor.cs`). Still pure and Terminal.Gui-free; still just the
model — no rendering (#317) or activation (#318/#320) change here.

## Goal

Extend `TaskLinkExtractor` so it also:

1. Detects **markdown-style `[text](url)` link spans**, returning a span over the
   **visible `text`** with the resolved **`url`** as the target.
2. Recognizes **custom-id ClickUp task URLs** (e.g.
   `app.clickup.com/t/{teamId}/{CUSTOM-123}`), classifying them as
   `LinkKind.Task` and carrying **both the raw id and a discriminator** so the
   activation layer (#318/#320) can tell a custom id from an API id.

Existing bare-`http(s)://`-URL behaviour is unchanged.

## Verified current state (#316, shipped)

- `Extract(string?)` scans for bare `http(s)://` URLs via `UrlPattern`
  (`https?://[^\s]+`), trims trailing prose punctuation (`TrimUrl`), guards
  well-formedness (absolute http(s) `Uri` with a host), and classifies each as
  `Task` (with `TaskId`) or `Web`.
- `TryParseTaskUri` matches the path `^(?:/\d+)?/t/([^/]+)/?$` — i.e. `/t/{id}`
  or workspace-prefixed `/{workspaceId}/t/{id}` — on host `app.clickup.com` /
  `clickup.com` only.
- `LinkSpan(int Start, int Length, LinkKind Kind, string Url, string? TaskId)`
  is a `readonly record struct`. No consumer on `main` constructs it (the render
  layer #317 is still an open PR); it is read-only from the app's side.

## Design decisions

### 1. Offset contract for markdown links (the deliberate decision #356 asks for)

`Extract` continues to return offsets **into the exact string it is handed** —
the #316 seam ("B produces offsets into the string it is handed; offset→(line,
col) is a C concern"). For a markdown link, the emitted span covers **only the
visible-text characters** — `Start` = index of the first char of `text` (just
after `[`), `Length` = `text.Length` — **not** the `[text](url)` markup. So
`text.Substring(span.Start, span.Length)` is exactly the display text, and the
`[`, `]`, `(url)` markup falls outside the span.

Rationale / alternative considered: an alternative contract returns a *collapsed
display string* (`[text](url)` → `text`) plus offsets into that string. Rejected
for this slice because it changes `Extract`'s shape (it would have to return the
rewritten text too), and the panes today render the plain `description`, not
`markdown_description`, so nothing collapses markup yet. Keeping `Extract` a pure
detector over its input — with the span bounding just the visible text — gives
the eventual markdown renderer (#317-family) a span that already excludes the
markup, and it stays consistent with how bare-URL offsets already work. This is
noted here so the contract is explicit if a renderer later collapses markup.

### 2. Single-pass unified scan (markdown consumes its own URL)

Detection becomes one combined regex, alternation ordered markdown-first:

```
(?<md>\[(?<mdtext>[^\]]*)\]\((?<mdurl>[^)\s]+)\))|(?<bare>https?://[^\s]+)
```

Because `Regex.Matches` is left-to-right and non-overlapping, a `[text](url)`
link is matched whole at the `[`, so the URL inside the parens is **consumed**
and the bare-URL alternative never re-detects it (no duplicate span for one
markdown link). A malformed markdown link (no closing `)`) simply fails the
markdown alternative and its URL, if bare, is still caught by the bare branch.

Per branch:
- **markdown**: validate `mdurl` (absolute http(s) with host); skip if it fails
  the guard or if `mdtext` is blank/whitespace (nothing to render/click). No
  trailing-trim — the `)` delimiter bounds the URL — but `mdurl` admits one level
  of balanced `(...)` so a URL like `.../Foo_(bar)` isn't truncated at its inner
  `(` (mirrors the bare path's `TrimUrl`/balanced-paren handling). `mdtext` is
  single-line (`[^\r\n\]]*`) so a visible-text span never crosses a newline.
  Span = `mdtext` range; `Url` = `mdurl`; classify via the shared task parser.
- **bare**: unchanged — `TrimUrl` + well-formedness guard + classify.

### 3. Custom-id task URLs + discriminator

ClickUp custom-id task URLs are `/t/{teamId}/{customId}` (`/t/` first, then a
numeric team id, then the custom id — distinct from the API-id
`/{workspaceId}/t/{id}` shape where the workspace id precedes `/t/`). Add a
second path pattern for it and return the custom id + a flag:

- API id: `^(?:/\d+)?/t/([^/]+)/?$` (existing) → `taskId`, custom = `false`.
- Custom id: `^/t/(\d+)/([^/]+)/?$` → `taskId` = the custom id, custom = `true`.

The two shapes cannot collide: the API pattern requires a single segment after
`/t/`, the custom pattern requires two (numeric team id + id). The existing
`/9014107164/t/id/extra` invalid case stays invalid (it is neither shape).

`LinkSpan` gains a trailing `bool IsCustomTaskId = false` (last positional,
defaulted → back-compatible; the full `Url` is already on the span, so the
activation layer has everything it needs to open a custom-id URL, and the flag
lets it choose in-app-by-id vs open-URL). `TryParseTaskUrl` gains a 3-arg
overload `(url, out taskId, out isCustomId)`; the existing 2-arg overload
delegates to it, so its callers and tests are untouched.

## Scope boundary / deferred

- Only the `/t/{teamId}/{customId}` custom-id shape (as the issue documents).
- Markdown link whose **visible text itself** contains a bare URL is not
  separately spanned (the markdown target wins) — a degenerate case; noted, not
  handled.
- No renderer/activation change — those are #317/#318/#319/#320.

## Test plan (`tests/ClickUpTodo.Tests/TaskLinkExtractorTests.cs`)

- Markdown web link → one `Web` span over the visible text, `Url` = target;
  `Substring(Start, Length)` == the visible text.
- Markdown link whose URL is a ClickUp task → `Task` span, right `TaskId`.
- Markdown link with a non-http url (`mailto:`, relative) → no span.
- Markdown link mixed with a bare URL on the same line → both, in order, no
  duplicate for the markdown target's URL.
- Empty visible text `[](url)` → no span.
- Bare-URL-only text is byte-identical to before (regression guard).
- Custom-id URL (bare and workspace/team-prefixed form) → `Task`,
  `IsCustomTaskId == true`, id = the custom id.
- API-id URL → `IsCustomTaskId == false` (all existing task cases).
- `TryParseTaskUrl` 3-arg overload: custom vs API discriminator; 2-arg overload
  still passes every existing case.

## Out of scope

Rendering/styling (#317), mouse activation (#318), focus traversal (#319),
Ctrl+Click destination (#320), collapsed-display-string offset model.
