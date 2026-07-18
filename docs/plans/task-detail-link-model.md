# Task Detail (B): link model — detect & classify links in body text

Issue: [#316](https://github.com/rbcministries/clickup-todo-cli/issues/316) ·
Epic: [#313](https://github.com/rbcministries/clickup-todo-cli/issues/313)

Foundation for **C** (styling, #317), **D** (mouse click, #318), **E** (focus
traversal, #319). This slice ships only the pure model; no rendering or
activation.

## Goal

A pure, Terminal.Gui-free, unit-tested model that scans the rendered
Description / Comment / Stream body text produced by
`Tui/TaskDetailFormatter.cs`, finds links, and classifies each as a **ClickUp
task link** or an **other web link** — returning immutable spans (char offset +
length + resolved target + optional task id) that the C/D/E layers consume.

## Verified current state

- Link handling in body text is greenfield — no URL detection anywhere in
  `src/` outside OAuth/API-base infrastructure.
- Body strings come from the pure `TaskDetailFormatter` (`Description`,
  `Comments`, `Stream`). `DetailPaneView` renders them by `body.Split('\n')`
  into one cell-list per line, so char offsets into the body string map cleanly
  onto (line, column) by counting newlines — that conversion is a **C**
  concern; **B** produces offsets into the string it is handed.
- The only URL-parsing precedent is `SetupWizard.ExtractListId`
  (`Setup/SetupWizard.cs:259`), a regex over ClickUp list-URL shapes. The
  task-URL parser mirrors that style. `MentionDetector`
  (`Services/MentionDetector.cs`) is the precedent for a pure, allocation-light,
  unit-tested text-scan model.
- ClickUp task URLs are `https://app.clickup.com/t/{id}`, optionally
  workspace-prefixed as `https://app.clickup.com/{workspaceId}/t/{id}`.

## Design

New sibling to `TaskDetailFormatter`, kept Terminal.Gui-free (namespace
`ClickUpTodo.Tui`, no Terminal.Gui `using`), so it is unit-tested exactly like
the formatter:

`Tui/TaskLinkExtractor.cs`

```csharp
public enum LinkKind { Task, Web }

public readonly record struct LinkSpan(
    int Start, int Length, LinkKind Kind, string Url, string? TaskId = null)
{
    public int End => Start + Length;
}

public static class TaskLinkExtractor
{
    // Scans body text for http/https URLs, in document order, returning one span
    // per link with char offsets into `text`. Task links carry Kind.Task + TaskId.
    public static IReadOnlyList<LinkSpan> Extract(string? text);

    // The task-URL classifier/id parser (mirrors ExtractListId's documented style),
    // exposed for reuse by later slices and directly unit-tested.
    public static bool TryParseTaskUrl(string url, out string taskId);
}
```

### URL detection

- Match bare `http(s)://…` URLs via a single regex (`https?://[^\s]+`), scanning
  the whole string so multi-line bodies and multiple links per line work with
  correct offsets.
- **Trailing-punctuation trim:** strip a trailing run of sentence punctuation
  (`. , ; : ! ? " '` and closing brackets `) ] }`) from the matched URL so
  `see https://x.com.` and `(https://x.com)` yield the bare URL. A closing `)`
  is kept when the URL contains a matching `(` (so Wikipedia-style
  `…_(disambiguation)` links survive). The span Length shrinks with the URL.

### Task classification

- Parse each matched URL with `Uri.TryCreate`. Classify as `Task` when the host
  is `clickup.com` or a `*.clickup.com` subdomain **and** the path matches
  `/t/{id}` or `/{workspaceId}/t/{id}` (`id = [^/?#]+`). Extract `id` as
  `TaskId`. Everything else is `Web` (TaskId null).

### Offsets

Char offsets into the exact string passed to `Extract`. `DetailPaneView`
splits the same string on `\n`, so C converts a global offset to (line, col) by
counting newlines before `Start` — no byte/rune ambiguity is introduced here
(offsets are UTF-16 char indices into the source string, the same unit
`string.Split`/`Substring` use).

## Scope boundary / deferred

- **Bare `http(s)://` URLs only** this slice. **Markdown-style `[text](url)`
  link spans are deferred** — ClickUp delivers comment text flattened and the
  detail panes render `description` (not `markdown_description`) as-is, so bare
  URLs are the realistic case. Tracked as a follow-up issue, linked from the PR.
- **Task-URL form:** the API-id `/t/{id}` form (and its workspace-prefixed
  variant) only, per the issue's open question. Custom-id URL shapes are a noted
  follow-up.

## Test plan (`tests/ClickUpTodo.Tests/TaskLinkExtractorTests.cs`)

- No links → empty. Null/blank → empty.
- Single bare web URL → one `Web` span with exact Start/Length/Url.
- Single ClickUp task URL → `Task` span with the right `TaskId`.
- Workspace-prefixed task URL → `Task` + correct id.
- Multiple links on one line, and across lines → correct in-document order and
  offsets (verified by slicing `text.Substring(span.Start, span.Length)`).
- Trailing punctuation: `.`/`,`/`)` trimmed; balanced `(...)` preserved.
- Non-clickup host that contains `/t/` in the path → `Web`, not `Task`.
- `TryParseTaskUrl` unit cases (valid/invalid/prefixed).

## Out of scope

Rendering/styling (C), mouse activation (D), focus traversal (E), markdown link
spans, custom-id task URLs.
