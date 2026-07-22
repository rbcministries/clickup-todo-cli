namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// One shortcut on the contextual help footer (#103): a <paramref name="Key"/> (a key or key-combo,
/// e.g. <c>Ctrl+B</c>, <c>↑/↓</c>) and the <paramref name="Label"/> for the action it triggers.
/// </summary>
public readonly record struct HelpItem(string Key, string Label);

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
    /// The trailing item shown when the full set doesn't fit (#H2/#104): <c>F1 Help + Shortcuts</c>
    /// opens the full list via the F1 <c>HelpScreen</c>, which #103 made reachable from every context.
    /// </summary>
    public static readonly HelpItem HelpFallback = new("F1", "Help + Shortcuts");

    /// <summary>
    /// Fits <paramref name="items"/> to <paramref name="width"/> display columns (#H2/#104). When the
    /// full line fits, returns <paramref name="items"/> unchanged. When it doesn't, returns the longest
    /// leading prefix that fits <em>alongside</em> a reserved trailing <see cref="HelpFallback"/>, with
    /// the fallback appended — so <c>F1 Help + Shortcuts</c> is always the last thing shown when
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
/// </summary>
public static class HelpItemSets
{
    /// <summary>The main task list. Matched the pre-#103 help line byte-for-byte until #290 rebound the
    /// Quick Updates launcher from <c>Space</c> to <c>Ctrl+U</c> (to agree with Task Detail), so the
    /// third item now reads <c>Ctrl+U quick update</c> instead of <c>␣ status</c>.</summary>
    public static readonly IReadOnlyList<HelpItem> MainList =
    [
        new("↑/↓", "move"),
        new("→|", "next section"),
        new("Ctrl+U", "quick update"),
        new("↩", "detail"),
        new("Ctrl+N", "new task"),
        new("Ctrl+B", "🌐"),
        new("Ctrl+P", "📌"),
        new("Ctrl+E", "feed"),
        new("F1", "help"),
        new("F2", "⚙"),
        new("F3", "filter/sort/group"),
        new("F4", "subtasks"),
        new("F5", "↻"),
        new("F6", "badges"),
        new("F12", "completed"),
        new("→/←", "expand/collapse"),
        new("Ctrl+→/←", "all"),
        new("Ctrl+Q", "quit"),
        new("type", "to search"),
    ];

    /// <summary>The task detail view (adds F1, which the detail screen previously did not handle).</summary>
    public static readonly IReadOnlyList<HelpItem> Detail =
    [
        new("Ctrl+←/→", "switch tab"),
        new("↑/↓ PgUp/PgDn", "scroll"),
        new("Ctrl+PgUp/PgDn", "order activity"),
        new("Ctrl+A", "dispatch to Claude"),
        new("Ctrl+N", "add comment"),
        new("Ctrl+E", "edit description"),
        new("Ctrl+B", "browser"),
        new("Ctrl+U", "quick update"),
        new("F5", "↻"),
        new("F1", "help"),
        new("Esc", "back"),
    ];

    /// <summary>The Task Detail description editor overlay (Ctrl+E, #217): a multi-line editor with
    /// Save/Cancel; Ctrl+Enter (or Tab→Save) saves, Esc cancels (confirming if there are unsaved edits),
    /// F1 opens Help.</summary>
    public static readonly IReadOnlyList<HelpItem> DetailDescriptionEditor =
    [
        new("Tab", "editor/Save/Cancel"),
        new("Ctrl+Enter", "save"),
        new("F1", "help"),
        new("Esc", "cancel"),
    ];

    /// <summary>The settings screen (F2).</summary>
    public static readonly IReadOnlyList<HelpItem> Settings =
    [
        new("Tab", "moves"),
        new("Space", "cycles buttons"),
        new("F1", "help"),
        new("Esc", "cancels"),
    ];

    /// <summary>The filter · sort · group screen (F3).</summary>
    public static readonly IReadOnlyList<HelpItem> FilterSortGroup =
    [
        new("Tab", "moves"),
        new("Enter", "in Value adds"),
        new("Del", "removes selected filter"),
        new("F1", "help"),
        new("Esc", "cancels"),
    ];

    /// <summary>The Quick Updates screen (Ctrl+U from both the main list and Task Detail, #156/#290):
    /// Tab cycles Status → Priority → Assignees, ↑/↓ move within a pane, Enter applies the highlighted
    /// status/priority (#157; assignee apply is #158). A fourth Lists pane (#242) is implemented but
    /// temporarily disabled — when re-enabled, restore "Lists" to the Tab item below.</summary>
    public static readonly IReadOnlyList<HelpItem> QuickUpdates =
    [
        new("Tab", "Status/Priority/Assignees"),
        new("↑/↓", "move"),
        new("Enter", "apply status/priority"),
        new("F1", "help"),
        new("Esc", "exit"),
    ];

    /// <summary>The New Task compose screen (Ctrl+N, #213/#240): Tab moves between
    /// Name/Description/Assignees/List and the buttons, Enter (or the default Save button) files the task
    /// in the primary (home) list, Esc cancels, F1 help.</summary>
    public static readonly IReadOnlyList<HelpItem> NewTask =
    [
        new("Tab", "Name/Descr/Assignees/List"),
        new("Enter/Save", "saves"),
        new("Esc", "cancels"),
        new("F1", "help"),
    ];

    /// <summary>The dispatch prompt-template editor (#100), reached from F2.</summary>
    public static readonly IReadOnlyList<HelpItem> PromptTemplateEditor =
    [
        new("Tab", "moves"),
        new("Ctrl+Alt+R", "reset to default"),
        new("F1", "help"),
        new("Esc", "cancel"),
    ];

    /// <summary>The mentions &amp; comments feed screen (opened with Ctrl+E, #109). Enter opens the
    /// selected comment's task (#115); F3 toggles the mentions-only filter (#113/#114); F6 toggles the
    /// recent-activity source (#117); F12 toggles whether completed-task activity is included; Ctrl+E
    /// returns to the list.</summary>
    public static readonly IReadOnlyList<HelpItem> NotificationsFeed =
    [
        new("↑/↓", "move"),
        new("Enter", "open"),
        new("F3", "mentions only"),
        new("F5", "↻"),
        new("F6", "activity"),
        new("F12", "completed"),
        new("Ctrl+E", "list"),
        new("F1", "help"),
        new("Esc", "back"),
    ];

    /// <summary>The background one-off run screen (#99): Esc cancels the in-flight run, then closes
    /// once it has finished.</summary>
    public static readonly IReadOnlyList<HelpItem> AgentRun =
    [
        new("↑/↓ PgUp/PgDn", "scroll"),
        new("F1", "help"),
        new("Esc", "cancel/back"),
    ];

    /// <summary>The help screen itself (no F1 — it is the help).</summary>
    public static readonly IReadOnlyList<HelpItem> Help =
    [
        new("Esc/Enter", "close"),
    ];
}
