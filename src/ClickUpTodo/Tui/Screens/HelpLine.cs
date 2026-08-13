namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// One shortcut on the contextual help footer (#103): a <paramref name="Key"/> (a key or key-combo,
/// e.g. <c>Ctrl+B</c>, <c>↑/↓</c>) and the <paramref name="Label"/> for the action it triggers.
/// <para>
/// <paramref name="IsAction"/> distinguishes a clickable <em>action</em> hint (#289) from a
/// non-clickable <em>movement/informational</em> hint (cursor-arrow / PgUp-PgDn glyphs,
/// <c>type to search</c>). Only action items fire on a footer click; movement items render as plain
/// text as they always have. It defaults to <c>true</c> because most footer items are actions — only
/// movement hints are annotated <c>IsAction: false</c> — which also keeps the pre-#289 two-argument
/// construction and record equality unchanged.
/// </para>
/// <para>
/// <paramref name="Chord"/> is the parseable key token to re-raise when the item is clicked, for the
/// few items whose display <paramref name="Key"/> is a glyph or a compound label rather than a single
/// parseable key (e.g. <c>␣</c>→<c>Space</c>, <c>↩</c>→<c>Enter</c>, <c>Del</c>→<c>Delete</c>). When
/// <c>null</c> the display <paramref name="Key"/> is itself the token (see <see cref="ActionKey"/>).
/// </para>
/// </summary>
public readonly record struct HelpItem(string Key, string Label, bool IsAction = true, string? Chord = null)
{
    /// <summary>The key token a footer click re-raises (#289): the explicit <see cref="Chord"/> when the
    /// display <see cref="Key"/> isn't a single parseable key, otherwise the <see cref="Key"/> itself.
    /// The host parses this with <c>Key.TryParse</c> and dispatches it via
    /// <c>Application.RaiseKeyDownEvent</c>, so a click converges on the same handler as the keypress.</summary>
    public string ActionKey => Chord ?? Key;
}

/// <summary>
/// Pure model + formatter for the single contextual help footer (#103, part of #102). The window owns
/// one bottom-row help line; each screen (and the main list) declares an ordered set of
/// <see cref="HelpItem"/>s, and the host renders the active context's set on that shared line —
/// replacing, never stacking. Kept free of Terminal.Gui so the shortcut sets and the
/// "which items for which context" selection are unit-testable (mirrors <c>SettingsForm</c> /
/// <c>StatusPickerModel</c>). Responsive truncation of an over-long line is the follow-up #H2 (#104).
/// </summary>
public static class HelpLine
{
    /// <summary>Renders items as <c>"key label · key label · …"</c> (empty for an empty set).</summary>
    public static string Format(IReadOnlyList<HelpItem> items)
        => string.Join(" · ", items.Select(i => $"{i.Key} {i.Label}"));

    /// <summary>
    /// The trailing item shown when the full set doesn't fit (#H2/#104): <c>F1 ℹ</c>
    /// opens the full list via the F1 <c>HelpScreen</c>, which #103 made reachable from every context.
    /// </summary>
    public static readonly HelpItem HelpFallback = new("F1", "ℹ");

    /// <summary>
    /// Fits <paramref name="items"/> to <paramref name="width"/> display columns (#H2/#104). When the
    /// full line fits, returns <paramref name="items"/> unchanged. When it doesn't, returns the longest
    /// leading prefix that fits <em>alongside</em> a reserved trailing <see cref="HelpFallback"/>, with
    /// the fallback appended — so <c>F1 ℹ</c> is always the last thing shown when
    /// truncated and the full list stays one keypress away. Item order is priority order: the leading
    /// (highest-value) shortcuts are kept first. Any existing F1-keyed item is dropped from the
    /// candidates while truncating (the fallback subsumes it) so F1 never renders twice. At a width too
    /// narrow for even the fallback, returns just <see cref="HelpFallback"/> (it still renders, clipped
    /// by the host) — the "only F1 fits" case.
    /// <para>
    /// <paramref name="measure"/> returns the display-column width of a rendered string; callers pass a
    /// grapheme/column-aware measure (Terminal.Gui's <c>StringExtensions.GetColumns</c>) so the wide
    /// glyphs/emoji already in the footers count as their true column width, not their char count. Kept
    /// free of Terminal.Gui (the measure is injected) so the fit rule stays unit-testable.
    /// </para>
    /// </summary>
    public static IReadOnlyList<HelpItem> Fit(
        IReadOnlyList<HelpItem> items, int width, Func<string, int> measure)
    {
        if (items.Count == 0 || measure(Format(items)) <= width)
            return items;

        // Truncating: reserve the F1 fallback as the last item and keep the longest leading prefix that
        // still fits with it. Skip any existing F1 item — the fallback already covers "F1 → Help".
        var kept = new List<HelpItem>();
        foreach (var item in items)
        {
            if (item.Key == HelpFallback.Key)
                continue;
            List<HelpItem> candidate = [.. kept, item, HelpFallback];
            if (measure(Format(candidate)) <= width)
                kept.Add(item);
            else
                break;
        }

        kept.Add(HelpFallback);
        return kept;
    }

    /// <summary>
    /// The display-column span <c>[Start, End)</c> of each item within <see cref="Format"/> of the same
    /// list (#289 click hit-testing). Item <c>i</c> occupies its <c>"key label"</c> width; the ` · `
    /// separators between items are the gaps <em>between</em> spans. <paramref name="measure"/> is the
    /// same column-aware measure passed to <see cref="Fit"/>, so wide glyphs/emoji count as their true
    /// column width — the spans line up with what the host renders. Kept free of Terminal.Gui so the
    /// mapping stays unit-testable.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> ColumnRanges(
        IReadOnlyList<HelpItem> items, Func<string, int> measure)
    {
        var ranges = new List<(int, int)>(items.Count);
        int separator = measure(" · ");
        int pos = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                pos += separator;
            int end = pos + measure($"{items[i].Key} {items[i].Label}");
            ranges.Add((pos, end));
            pos = end;
        }

        return ranges;
    }

    /// <summary>
    /// The index of the item rendered at display <paramref name="column"/> (#289), or <c>-1</c> when the
    /// column falls on a ` · ` separator, before the first item, or past the last — so a click there is
    /// a no-op rather than snapping to the nearest item. Columns are measured with the host's
    /// column-aware <paramref name="measure"/> (matching <see cref="ColumnRanges"/> / <see cref="Fit"/>).
    /// </summary>
    public static int HitTest(IReadOnlyList<HelpItem> items, int column, Func<string, int> measure)
    {
        if (column < 0)
            return -1;

        var ranges = ColumnRanges(items, measure);
        for (int i = 0; i < ranges.Count; i++)
            if (column >= ranges[i].Start && column < ranges[i].End)
                return i;

        return -1;
    }

    /// <summary>
    /// The selection rule: the active screen's items when a screen is open (and it declares any),
    /// otherwise the list's items. A screen that declares an empty set falls back to the list's set
    /// rather than showing a blank footer.
    /// </summary>
    public static IReadOnlyList<HelpItem> ForActiveScreen(
        IReadOnlyList<HelpItem>? screenItems, IReadOnlyList<HelpItem> fallback)
        => screenItems is { Count: > 0 } ? screenItems : fallback;
}

/// <summary>
/// The canonical, ordered shortcut sets for each context. Centralised (rather than hand-rolled per
/// screen) so the footer is consistent and testable; each screen exposes its set via
/// <c>Screen.HelpItems</c> and the host renders the list's set (<see cref="MainList"/>) when no screen
/// is open. Every screen set ends with an Esc/close item and (except Help itself) offers F1 → Help.
/// <para>
/// Action labels favour concise glyphs over words where a glyph reads clearly (#343 established the
/// vocabulary; extended here). The less-literal glyphs, for maintainers: <c>ℹ</c> help/shortcuts,
/// <c>🗁</c> open-by-id, <c>⧩</c> filter, <c>▼▲</c> sort / order activity, <c>⛚</c> group,
/// <c>✨</c> dispatch to Claude, <c>✏</c> edit description, <c>➕</c> new/add, <c>🌐</c> browser,
/// <c>🔔</c> mentions/comments feed, <c>📌</c> pin, <c>⚙</c> settings, <c>↻</c> refresh,
/// <c>👁✅</c> show/hide completed. Each glyph keeps its <c>Key</c> hint so the shortcut stays
/// discoverable and the item stays clickable (#289). The <c>HelpScreen</c> mirrors these glyphs
/// beside each explanation so users can match a footer icon to its concept.
/// </para>
/// </summary>
public static class HelpItemSets
{
    /// <summary>The main task list. Matched the pre-#103 help line byte-for-byte until #290 rebound the
    /// Quick Updates launcher from <c>Space</c> to <c>Ctrl+U</c> (to agree with Task Detail), so the
    /// third item now reads <c>Ctrl+U quick update</c> instead of <c>␣ status</c>.</summary>
    public static readonly IReadOnlyList<HelpItem> MainList =
    [
        new("↑/↓", "move", IsAction: false),
        new("→|", "next section", IsAction: false),
        new("Ctrl+U", "quick update"),
        new("↩", "detail", Chord: "Enter"),
        new("Ctrl+O", "🗁 by ID"),
        new("Ctrl+↩", "new tab", Chord: "Ctrl+Enter"),
        new("Ctrl+N", "➕"),
        new("Ctrl+B", "🌐"),
        new("Ctrl+P", "📌"),
        new("Ctrl+E", "🔔"),
        new("F1", "ℹ"),
        new("F10", "⚙"),
        new("F2", "✏ rename"),
        new("F3", "⧩ ▼▲ ⛚"),
        new("F4", "subtasks"),
        new("F5", "↻"),
        new("F6", "badges"),
        new("F12", "👁✅"),
        new("→/←", "expand/collapse", IsAction: false),
        new("Ctrl+→/←", "all", IsAction: false),
        new("Ctrl+Q", "quit"),
        new("type", "to search", IsAction: false),
    ];

    /// <summary>The task detail view (adds F1, which the detail screen previously did not handle).</summary>
    public static readonly IReadOnlyList<HelpItem> Detail =
    [
        new("Ctrl+←/→", "switch tab", IsAction: false),
        new("↑/↓ PgUp/PgDn", "scroll", IsAction: false),
        new("Ctrl+PgUp/PgDn", "▼▲", IsAction: false),
        new("Ctrl+A", "✨Dispatch"),
        new("Ctrl+N", "➕Comment"),
        new("Ctrl+T", "↩Reply"),
        new("Ctrl+E", "✏Description"),
        new("Ctrl+B", "🌐"),
        new("Ctrl+U", "quick update"),
        new("Ctrl+O", "🗁 by ID"),
        new("␣", "☑ toggle", Chord: "Space"),
        new("Ctrl+G", "➕ list"),
        new("F2", "✏ edit"),
        new("Del", "🗑 delete", Chord: "Delete"),
        new("Shift+↑", "move", Chord: "Shift+CursorUp"),
        new("Shift+↓", "move", Chord: "Shift+CursorDown"),
        new("Shift+←", "outdent", Chord: "Shift+CursorLeft"),
        new("Shift+→", "indent", Chord: "Shift+CursorRight"),
        new("F5", "↻"),
        new("F1", "ℹ"),
        new("Esc", "back"),
    ];

    /// <summary>The task detail view when the Task Tree tab is present (#291): <see cref="Detail"/> plus
    /// the tab's F6 badge-display cycle (#415, mirroring the main list's F6) and the Ctrl+Enter "open this
    /// task in a new terminal tab" gesture (#384/#435, the detail counterpart of the list's #301 gesture).
    /// Both hosts carry it: the dashboard-hosted detail, and — since #374 gave single-task launch mode the
    /// Task Tree tab too — single-task mode. The leaner <see cref="Detail"/> set (neither F6 nor Ctrl+Enter)
    /// is only used when no tree loader was supplied.</summary>
    public static readonly IReadOnlyList<HelpItem> DetailWithTaskTree =
    [
        new("Ctrl+←/→", "switch tab", IsAction: false),
        new("↑/↓ PgUp/PgDn", "scroll", IsAction: false),
        new("Ctrl+PgUp/PgDn", "▼▲", IsAction: false),
        new("Ctrl+A", "✨Dispatch"),
        new("Ctrl+N", "➕Comment"),
        new("Ctrl+T", "↩Reply"),
        new("Ctrl+E", "✏Description"),
        new("Ctrl+B", "🌐"),
        new("Ctrl+U", "quick update"),
        new("Ctrl+O", "🗁 by ID"),
        new("Ctrl+↩", "new tab", Chord: "Ctrl+Enter"),
        new("␣", "☑ toggle", Chord: "Space"),
        new("Ctrl+G", "➕ list"),
        new("F2", "✏ edit"),
        new("Del", "🗑 delete", Chord: "Delete"),
        new("Shift+↑", "move", Chord: "Shift+CursorUp"),
        new("Shift+↓", "move", Chord: "Shift+CursorDown"),
        new("Shift+←", "outdent", Chord: "Shift+CursorLeft"),
        new("Shift+→", "indent", Chord: "Shift+CursorRight"),
        new("F5", "↻"),
        new("F6", "badges"),
        new("F1", "ℹ"),
        new("Esc", "back"),
    ];

    /// <summary>The Task Detail description editor overlay (Ctrl+E, #217): a multi-line editor with
    /// Save/Cancel; Ctrl+Enter (or Tab→Save) saves, Esc cancels (confirming if there are unsaved edits),
    /// F1 opens Help. The @ trigger (#326) opens the mention picker to splice a plain <c>@Name</c>
    /// reference (a description mention is literal text, not a live mention — the #321 verdict).</summary>
    public static readonly IReadOnlyList<HelpItem> DetailDescriptionEditor =
    [
        new("Tab", "editor/Save/Cancel"),
        // The @ trigger (#326) is a character the user types, not a clickable chord — IsAction:false, like
        // the composer's, so it isn't re-raised on a footer click.
        new("@", "mention", IsAction: false),
        new("Ctrl+Enter", "save"),
        new("F1", "ℹ"),
        new("Esc", "cancel"),
    ];

    /// <summary>The Task Detail comment composer overlay (Ctrl+N, #216): a multi-line editor whose own
    /// keys are Ctrl+Enter (or Tab→Post) to post and Esc to cancel; F1 opens Help even while composing
    /// (<c>OnCommentKey</c>). Mirrors <see cref="DetailDescriptionEditor"/> so the footer advertises only
    /// what the composer actually does — otherwise the full command footer stays up and a click on an
    /// inert hint re-raises its chord into the composer (#436).</summary>
    public static readonly IReadOnlyList<HelpItem> DetailCommentComposer =
    [
        new("Tab", "editor/Post/Cancel"),
        // The @ trigger (#325) is a character the user types, not a clickable chord — IsAction:false, like
        // "type to search", so it isn't re-raised on a footer click.
        new("@", "mention", IsAction: false),
        new("Ctrl+Enter", "post"),
        new("F1", "ℹ"),
        new("Esc", "cancel"),
    ];

    /// <summary>The @-mention picker overlaid over the comment composer (#325) or the description editor
    /// (#326): a search field over a candidate list — type to search, ↑/↓ to move, Enter to insert the
    /// mention, Esc to go back to the editor it opened over. Shown while the picker is open so the footer
    /// reflects only what it does (the host editor's own keys resume when it closes). Search/move are
    /// informational; Enter/Esc are clickable actions (they re-raise into the focused picker).</summary>
    public static readonly IReadOnlyList<HelpItem> DetailMentionPicker =
    [
        new("type", "search", IsAction: false),
        new("↑↓", "move", IsAction: false),
        new("Enter", "mention"),
        new("Esc", "back"),
    ];

    /// <summary>The Task Detail reply-target picker overlay (Ctrl+T, #330): a list of the task's comments
    /// whose own keys are ↑/↓ to choose, Enter to reply (opening the composer in reply mode) and Esc to
    /// cancel. Mirrors <see cref="DetailCommentComposer"/> so the footer advertises only what the picker
    /// does — otherwise a click on an inert command hint re-raises its chord (#436).</summary>
    public static readonly IReadOnlyList<HelpItem> DetailReplyPicker =
    [
        new("↑/↓", "choose", IsAction: false),
        new("Enter", "reply"),
        new("Esc", "cancel"),
    ];

    /// <summary>The Checklists-tab item add/rename input overlay (E, #458): a single-line name field whose
    /// own keys are Enter to submit and Esc to cancel (arming a discard confirm on an edited rename).
    /// Mirrors <see cref="DetailCommentComposer"/> so the footer advertises only what the overlay does —
    /// otherwise a click on an inert command hint would re-raise its chord into the field (#436).</summary>
    public static readonly IReadOnlyList<HelpItem> DetailChecklistItemEditor =
    [
        new("↩", "save", Chord: "Enter"),
        new("F1", "ℹ"),
        new("Esc", "cancel"),
    ];

    /// <summary>The main-list task-rename overlay (H, #545): a single-line title field launched by F2,
    /// whose own keys are Enter to save and Esc to cancel; F1 opens Help. Mirrors
    /// <see cref="DetailChecklistItemEditor"/> so the footer advertises only what the overlay does.</summary>
    public static readonly IReadOnlyList<HelpItem> RenameTask =
    [
        new("↩", "save", Chord: "Enter"),
        new("F1", "ℹ"),
        new("Esc", "cancel"),
    ];

    /// <summary>Picks the Task Detail footer set for the current overlay state (#436). Pure so the
    /// branch order is unit-testable — the <see cref="TaskDetailScreen.HelpItems"/> property that calls
    /// it lives on a Terminal.Gui view and can't run in CI. The mention picker (#325), comment composer,
    /// reply-target picker (#330) and description editor overlays are checked in a fixed order: the mention
    /// picker sits over the composer so it wins when both are up, then the composer, then the description
    /// editor, then the reply picker. When no overlay is open the set depends on whether the Task Tree tab
    /// is present (its F6 badge cycle #415, and the Ctrl+Enter new-tab gesture #384/#435): present →
    /// <see cref="DetailWithTaskTree"/>, absent → <see cref="Detail"/>. Both the dashboard and single-task
    /// launch mode (since #374) supply a tree loader, so both get <see cref="DetailWithTaskTree"/>;
    /// <see cref="Detail"/> is the no-tree-loader case.
    /// <para>
    /// The non-overlay set also varies by the front-most <paramref name="sub"/> tab (contextual chords C,
    /// #540): the single <c>Ctrl+N</c> item reads <c>➕ item</c> on the Checklists tab (where the chord
    /// adds a checklist item) and <c>➕Comment</c> on every other tab (where it opens the comment
    /// composer) — the footer label thus tracks what the shared chord actually does, resolved from the
    /// same <see cref="Keybindings.ResolveDetail"/> table the dispatch consults. The retired <c>F7</c>
    /// add-item hint is gone.
    /// </para></summary>
    public static IReadOnlyList<HelpItem> DetailFooter(
        bool commentComposerVisible, bool descriptionEditorVisible, bool replyPickerVisible, bool hasTaskTree,
        DetailSubContext sub = DetailSubContext.Default,
        bool mentionPickerVisible = false, bool checklistItemEditorVisible = false) =>
        mentionPickerVisible ? DetailMentionPicker
        : commentComposerVisible ? DetailCommentComposer
        : descriptionEditorVisible ? DetailDescriptionEditor
        : replyPickerVisible ? DetailReplyPicker
        : checklistItemEditorVisible ? DetailChecklistItemEditor
        : WithContextualNewLabel(hasTaskTree ? DetailWithTaskTree : Detail, sub);

    /// <summary>Relabels the single shared <c>Ctrl+N</c> item to match what the chord does on the
    /// front-most tab (contextual chords C, #540): <c>➕ item</c> on the Checklists tab, otherwise the
    /// list's own <c>➕Comment</c> label. Returns the set unchanged for every non-Checklists tab, so the
    /// common case allocates nothing new.</summary>
    private static IReadOnlyList<HelpItem> WithContextualNewLabel(IReadOnlyList<HelpItem> set, DetailSubContext sub)
        => sub != DetailSubContext.Checklists
            ? set
            : [.. set.Select(i => i is { IsAction: true, Key: "Ctrl+N" } ? i with { Label = "➕ item" } : i)];

    /// <summary>The settings screen (F2).</summary>
    public static readonly IReadOnlyList<HelpItem> Settings =
    [
        new("Tab", "moves"),
        new("Space", "cycles buttons"),
        new("F1", "ℹ"),
        new("Esc", "cancels"),
    ];

    /// <summary>The filter · sort · group screen (F3).</summary>
    public static readonly IReadOnlyList<HelpItem> FilterSortGroup =
    [
        new("Tab", "moves"),
        new("Enter", "in Value adds"),
        new("Del", "removes selected filter", Chord: "Delete"),
        new("F1", "ℹ"),
        new("Esc", "cancels"),
    ];

    /// <summary>The Quick Updates screen (Ctrl+U from both the main list and Task Detail, #156/#290):
    /// Tab cycles Status → Priority → Assignees → Lists, ↑/↓ move within a pane, Enter applies the
    /// highlighted status/priority (#157; assignee apply is #158; list add/remove is #242/#365).</summary>
    public static readonly IReadOnlyList<HelpItem> QuickUpdates =
    [
        new("Tab", "Status/Priority/Assignees/Lists"),
        new("↑/↓", "move", IsAction: false),
        new("Enter", "apply status/priority"),
        new("F1", "ℹ"),
        new("Esc", "exit"),
    ];

    /// <summary>The quick-open entry surface (Ctrl+O, #303): a single field for a task id, custom id, or
    /// URL; Enter (or the default Open button) resolves and opens, Esc cancels, F1 help.</summary>
    public static readonly IReadOnlyList<HelpItem> QuickOpen =
    [
        new("Enter/Open", "open", Chord: "Enter"),
        new("F1", "ℹ"),
        new("Esc", "cancel"),
    ];

    /// <summary>The New Task compose screen (Ctrl+N, #213/#240): Tab moves between
    /// Name/Description/Assignees/List and the buttons, Enter (or the default Save button) files the task
    /// in the primary (home) list, Esc cancels, F1 help.</summary>
    public static readonly IReadOnlyList<HelpItem> NewTask =
    [
        new("Tab", "Name/Descr/Assignees/List"),
        new("Enter/Save", "saves", Chord: "Enter"),
        new("Esc", "cancels"),
        new("F1", "ℹ"),
    ];

    /// <summary>The dispatch prompt-template editor (#100), reached from F2.</summary>
    public static readonly IReadOnlyList<HelpItem> PromptTemplateEditor =
    [
        new("Tab", "moves"),
        new("Ctrl+Alt+R", "reset to default"),
        new("F1", "ℹ"),
        new("Esc", "cancel"),
    ];

    /// <summary>The dispatch providers editor (#547), reached from F10: a provider list with Name /
    /// Executable / Extra-args fields and Add / Delete / Set-default buttons. Tab moves between the list,
    /// fields and buttons; Del arms an inline delete confirm on the selected provider; Save/Esc close.</summary>
    public static readonly IReadOnlyList<HelpItem> DispatchProviders =
    [
        new("↑/↓", "move", IsAction: false),
        new("Tab", "list / fields / buttons"),
        new("Del", "delete provider", Chord: "Delete"),
        new("F1", "ℹ"),
        new("Esc", "cancel"),
    ];

    /// <summary>The mentions &amp; comments feed screen (opened with Ctrl+E, #109). Enter opens the
    /// selected comment's task (#115); F3 toggles the mentions-only filter (#113/#114); F6 toggles the
    /// recent-activity source (#117); F12 toggles whether completed-task activity is included; Ctrl+E
    /// returns to the list.</summary>
    public static readonly IReadOnlyList<HelpItem> NotificationsFeed =
    [
        new("↑/↓", "move", IsAction: false),
        new("Enter", "open"),
        new("F3", "mentions only"),
        new("F5", "↻"),
        new("F6", "activity"),
        new("F12", "👁✅"),
        new("Ctrl+E", "list"),
        new("F1", "ℹ"),
        new("Esc", "back"),
    ];

    /// <summary>The background one-off run screen (#99): Esc cancels the in-flight run, then closes
    /// once it has finished.</summary>
    public static readonly IReadOnlyList<HelpItem> AgentRun =
    [
        new("↑/↓ PgUp/PgDn", "scroll", IsAction: false),
        new("F1", "ℹ"),
        new("Esc", "cancel/back"),
    ];

    /// <summary>The help screen itself (no F1 — it is the help).</summary>
    public static readonly IReadOnlyList<HelpItem> Help =
    [
        new("Esc/Enter", "close", Chord: "Esc"),
    ];

    /// <summary>The exit-confirmation modal (#299): a two-key yes/no, so no F1 (it would stack Help over
    /// a question) and no movement hints. Both launch modes render this same set, which is what makes the
    /// guard read identically on the dashboard's list root and single-task mode's launch-task root.</summary>
    public static readonly IReadOnlyList<HelpItem> ExitConfirm =
    [
        new("Y/↩", "yes, exit", Chord: "Y"),
        new("Esc/N", "no, stay", Chord: "Esc"),
    ];
}
