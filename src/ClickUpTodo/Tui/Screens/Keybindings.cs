namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The screen contexts a keybinding can belong to (#355). Each maps to one <see cref="HelpItemSets"/>
/// set and — as screens are migrated — one <see cref="ClickUpTodo.Tui.KeybindingDispatcher"/>. Kept a
/// plain enum (no launch-mode dimension yet; that is #296, deliberately out of scope) so the table
/// stays a pure lookup.
/// </summary>
public enum ScreenContext
{
    MainList,
    Detail,
    DetailDescriptionEditor,
    Settings,
    FilterSortGroup,
    QuickUpdates,
    QuickOpen,
    RenameTask,
    NewTask,
    PromptTemplateEditor,
    DispatchProviders,
    NotificationsFeed,
    AgentRun,
    Help,
    ExitConfirm,
}

/// <summary>
/// The front-most Task Detail tab, as a keybinding sub-context (contextual chords C, #540; the model
/// recorded in slice A, <c>docs/plans/contextual-chord-model.md</c>). It is what lets one chord mean
/// different things per tab — <c>Ctrl+N</c> adds a checklist item on <see cref="Checklists"/> but opens
/// the comment composer on the others — while the base <see cref="Keybindings"/> map stays the single
/// source of each action's token. <see cref="Default"/> covers Stream / Description / Other and is the
/// fallback for any tab without an override. Tab-scoped and orthogonal to the punted launch-mode
/// dimension (#296); kept Detail-specific until a second screen needs sub-contexts.
/// </summary>
public enum DetailSubContext
{
    Default,
    Comments,
    Checklists,
    TaskTree,
}

/// <summary>
/// The command / navigation actions the central keybinding table governs (#355). These are the
/// cross-screen and per-context <em>command</em> shortcuts — the ones that appear on the contextual
/// help footer. Per-form focus keys (in-form <c>Tab</c>/<c>Space</c>, the New-Task/editor <c>Save</c>
/// whose key legitimately differs by form, <c>Ctrl+Alt+R</c> reset) and undisplayed aliases
/// (<c>Ctrl+R</c> for refresh, <c>Ctrl+C</c>/<c>Esc</c> quit on the list) stay in their screens'
/// handlers and are intentionally absent here.
/// </summary>
public enum KeyAction
{
    // Recurring across contexts — the anti-drift core (generalizes #290).
    Help,
    Back,
    Refresh,
    QuickUpdate,
    Feed,
    OpenInBrowser,
    ToggleCompleted,
    Open,

    // Main list commands.
    OpenDetail,
    OpenInNewTab,
    NewTask,
    RenameTask,
    QuickOpen,
    TogglePin,
    Settings,
    FilterSortGroup,
    CycleSubtasks,
    CycleBadges,
    Quit,

    // Task Detail commands.
    DispatchToClaude,
    AddComment,
    ReplyToComment,
    EditDescription,
    ToggleChecklistItem,
    AddChecklistItem,
    EditChecklistItem,
    DeleteChecklistItem,
    MoveChecklistItemUp,
    MoveChecklistItemDown,
    OutdentChecklistItem,
    IndentChecklistItem,
    NewChecklist,

    // Quick Updates.
    Apply,

    // Exit confirmation (#299): the affirmative answer. Its "no" is the recurring Back/Esc above.
    Confirm,

    // Notifications feed.
    MentionsOnly,
    ActivitySource,
}

/// <summary>
/// The single source of truth mapping <c>(context, action) → key</c> (#355). Both the dispatch side
/// (<see cref="ClickUpTodo.Tui.KeybindingDispatcher"/>) and the display side (<see cref="HelpItemSets"/>,
/// which asserts against this table in tests) consult it, so a shortcut and its footer label are
/// defined once and cannot drift.
/// <para>
/// Values are the parseable key tokens — identical to the matching <see cref="HelpItem.ActionKey"/>
/// and to what <c>Terminal.Gui</c>'s <c>Key.TryParse</c> accepts (e.g. <c>"Ctrl+U"</c>, <c>"F5"</c>,
/// <c>"Enter"</c>, <c>"Esc"</c>). Kept free of Terminal.Gui so the table itself is a pure, unit-testable
/// lookup (mirrors <see cref="HelpLine"/>); parsing to a key happens in the dispatcher.
/// </para>
/// </summary>
public static class Keybindings
{
    private static readonly IReadOnlyDictionary<(ScreenContext Context, KeyAction Action), string> Map =
        new Dictionary<(ScreenContext, KeyAction), string>
        {
            // ── Main list ─────────────────────────────────────────────────────────────────────
            [(ScreenContext.MainList, KeyAction.QuickUpdate)] = "Ctrl+U",
            [(ScreenContext.MainList, KeyAction.OpenDetail)] = "Enter",
            [(ScreenContext.MainList, KeyAction.OpenInNewTab)] = "Ctrl+Enter",
            [(ScreenContext.MainList, KeyAction.QuickOpen)] = "Ctrl+O",
            [(ScreenContext.MainList, KeyAction.NewTask)] = "Ctrl+N",
            // Contextual chords H (#545): F2 renames the highlighted task's title in place — B (#539)
            // freed F2 (Settings → F10) precisely for the rename slices. The main list has no ambiguous
            // tabs, so this is a direct MainList binding (contextual-chord-model.md §5-H); the write goes
            // through the SetTaskNameAsync facade E (#542) landed.
            [(ScreenContext.MainList, KeyAction.RenameTask)] = "F2",
            [(ScreenContext.MainList, KeyAction.OpenInBrowser)] = "Ctrl+B",
            [(ScreenContext.MainList, KeyAction.TogglePin)] = "Ctrl+P",
            [(ScreenContext.MainList, KeyAction.Feed)] = "Ctrl+E",
            [(ScreenContext.MainList, KeyAction.Help)] = "F1",
            [(ScreenContext.MainList, KeyAction.Settings)] = "F10",
            [(ScreenContext.MainList, KeyAction.FilterSortGroup)] = "F3",
            [(ScreenContext.MainList, KeyAction.CycleSubtasks)] = "F4",
            [(ScreenContext.MainList, KeyAction.Refresh)] = "F5",
            [(ScreenContext.MainList, KeyAction.CycleBadges)] = "F6",
            [(ScreenContext.MainList, KeyAction.ToggleCompleted)] = "F12",
            [(ScreenContext.MainList, KeyAction.Quit)] = "Ctrl+Q",

            // ── Task Detail ───────────────────────────────────────────────────────────────────
            [(ScreenContext.Detail, KeyAction.DispatchToClaude)] = "Ctrl+A",
            [(ScreenContext.Detail, KeyAction.AddComment)] = "Ctrl+N",
            [(ScreenContext.Detail, KeyAction.ReplyToComment)] = "Ctrl+T",
            [(ScreenContext.Detail, KeyAction.EditDescription)] = "Ctrl+E",
            [(ScreenContext.Detail, KeyAction.ToggleChecklistItem)] = "Space",
            // Contextual chords C (#540): the "new" chord is now shared with AddComment (both "Ctrl+N")
            // and disambiguated by the front Task Detail tab (see DetailSubContext / ResolveDetail below).
            // Retargeted from the #458 stopgap F7, which is now unbound.
            [(ScreenContext.Detail, KeyAction.AddChecklistItem)] = "Ctrl+N",
            // Contextual chords D (#541): rename moves off the #458 stopgap F8 to the conventional F2
            // (= Rename, #290). F8 is now unbound; the checklist item / group rename is F2 on the
            // Checklists tab (see DetailSubContext / ResolveDetail below). No collision — F2 is otherwise
            // RenameTask in MainList only (slice H, #545), a different ScreenContext, and EditChecklistItem
            // is the sole F2-bound action in any Detail sub-context. After C/D/F no F7/F8/F9 binding remains.
            // The action reads Edit (not Rename) because the F2 surface edits the item's name + assignee
            // (#572) — contextual-chord-model §3/§5-D; the token (F2) and sub-context slot are unchanged (#601).
            [(ScreenContext.Detail, KeyAction.EditChecklistItem)] = "F2",
            // Contextual chords F (#543): delete moves off the #458 stopgap F9 to the conventional
            // Delete key, behind a confirmation. F9 is now unbound; the checklist item / group delete
            // is Delete on the Checklists tab (see DetailSubContext / ResolveDetail below).
            [(ScreenContext.Detail, KeyAction.DeleteChecklistItem)] = "Delete",
            [(ScreenContext.Detail, KeyAction.MoveChecklistItemUp)] = "Shift+CursorUp",
            [(ScreenContext.Detail, KeyAction.MoveChecklistItemDown)] = "Shift+CursorDown",
            [(ScreenContext.Detail, KeyAction.OutdentChecklistItem)] = "Shift+CursorLeft",
            [(ScreenContext.Detail, KeyAction.IndentChecklistItem)] = "Shift+CursorRight",
            [(ScreenContext.Detail, KeyAction.NewChecklist)] = "Ctrl+G",
            [(ScreenContext.Detail, KeyAction.OpenInBrowser)] = "Ctrl+B",
            [(ScreenContext.Detail, KeyAction.QuickUpdate)] = "Ctrl+U",
            [(ScreenContext.Detail, KeyAction.Refresh)] = "F5",
            [(ScreenContext.Detail, KeyAction.Help)] = "F1",
            [(ScreenContext.Detail, KeyAction.Back)] = "Esc",

            // ── Task Detail description editor overlay ──────────────────────────────────────────
            [(ScreenContext.DetailDescriptionEditor, KeyAction.Help)] = "F1",
            [(ScreenContext.DetailDescriptionEditor, KeyAction.Back)] = "Esc",

            // ── Settings ────────────────────────────────────────────────────────────────────────
            [(ScreenContext.Settings, KeyAction.Help)] = "F1",
            [(ScreenContext.Settings, KeyAction.Back)] = "Esc",

            // ── Filter · Sort · Group ───────────────────────────────────────────────────────────
            [(ScreenContext.FilterSortGroup, KeyAction.Help)] = "F1",
            [(ScreenContext.FilterSortGroup, KeyAction.Back)] = "Esc",

            // ── Quick Updates ─────────────────────────────────────────────────────────────────
            [(ScreenContext.QuickUpdates, KeyAction.Apply)] = "Enter",
            [(ScreenContext.QuickUpdates, KeyAction.Help)] = "F1",
            [(ScreenContext.QuickUpdates, KeyAction.Back)] = "Esc",

            // ── Quick Open ────────────────────────────────────────────────────────────────────
            [(ScreenContext.QuickOpen, KeyAction.Open)] = "Enter",
            [(ScreenContext.QuickOpen, KeyAction.Help)] = "F1",
            [(ScreenContext.QuickOpen, KeyAction.Back)] = "Esc",

            // ── Rename task overlay (contextual chords H, #545) ───────────────────────────────
            // A single-line title-rename modal launched by F2 from the main list. Like the other editor
            // screens (New Task, description editor) the table binds only Help/Back; the Save key is Enter,
            // handled in the screen (a per-form focus key, intentionally not in the table — see KeyAction).
            [(ScreenContext.RenameTask, KeyAction.Help)] = "F1",
            [(ScreenContext.RenameTask, KeyAction.Back)] = "Esc",

            // ── New Task ──────────────────────────────────────────────────────────────────────
            [(ScreenContext.NewTask, KeyAction.Help)] = "F1",
            [(ScreenContext.NewTask, KeyAction.Back)] = "Esc",

            // ── Prompt-template editor ────────────────────────────────────────────────────────
            [(ScreenContext.PromptTemplateEditor, KeyAction.Help)] = "F1",
            [(ScreenContext.PromptTemplateEditor, KeyAction.Back)] = "Esc",

            // ── Dispatch providers editor (#547) ──────────────────────────────────────────────
            [(ScreenContext.DispatchProviders, KeyAction.Help)] = "F1",
            [(ScreenContext.DispatchProviders, KeyAction.Back)] = "Esc",

            // ── Notifications feed ────────────────────────────────────────────────────────────
            [(ScreenContext.NotificationsFeed, KeyAction.Open)] = "Enter",
            [(ScreenContext.NotificationsFeed, KeyAction.MentionsOnly)] = "F3",
            [(ScreenContext.NotificationsFeed, KeyAction.Refresh)] = "F5",
            [(ScreenContext.NotificationsFeed, KeyAction.ActivitySource)] = "F6",
            [(ScreenContext.NotificationsFeed, KeyAction.ToggleCompleted)] = "F12",
            [(ScreenContext.NotificationsFeed, KeyAction.Feed)] = "Ctrl+E",
            [(ScreenContext.NotificationsFeed, KeyAction.Help)] = "F1",
            [(ScreenContext.NotificationsFeed, KeyAction.Back)] = "Esc",

            // ── Agent run ─────────────────────────────────────────────────────────────────────
            [(ScreenContext.AgentRun, KeyAction.Help)] = "F1",
            [(ScreenContext.AgentRun, KeyAction.Back)] = "Esc",

            // ── Help (it is the help; no Help action, only Back to close) ──────────────────────
            [(ScreenContext.Help, KeyAction.Back)] = "Esc",

            // ── Exit confirmation (#299) ──────────────────────────────────────────────────────
            // Y confirms; Esc (Back, as everywhere) cancels. Enter/N are the undisplayed aliases and
            // stay in the screen's handler, like the other alias keys. No Help action: a two-key yes/no
            // shouldn't stack Help over itself.
            [(ScreenContext.ExitConfirm, KeyAction.Confirm)] = "Y",
            [(ScreenContext.ExitConfirm, KeyAction.Back)] = "Esc",
        };

    /// <summary>Every <c>(context, action) → token</c> entry, for cross-checking against the footer.</summary>
    public static IReadOnlyDictionary<(ScreenContext Context, KeyAction Action), string> All => Map;

    /// <summary>The key token bound to <paramref name="action"/> in <paramref name="context"/>.
    /// Throws <see cref="KeyNotFoundException"/> if the context does not bind the action.</summary>
    public static string Token(ScreenContext context, KeyAction action)
        => Map.TryGetValue((context, action), out var token)
            ? token
            : throw new KeyNotFoundException($"No keybinding for {action} in {context}.");

    /// <summary>The key token bound to <paramref name="action"/> in <paramref name="context"/>, or
    /// <c>false</c> when the context does not bind it.</summary>
    public static bool TryToken(ScreenContext context, KeyAction action, out string token)
        => Map.TryGetValue((context, action), out token!);

    /// <summary>The actions bound in <paramref name="context"/>.</summary>
    public static IEnumerable<KeyAction> ActionsFor(ScreenContext context)
        => Map.Keys.Where(k => k.Context == context).Select(k => k.Action);

    // ── Task Detail sub-context activation (contextual chords C, #540) ──────────────────────────────
    //
    // The base Map above owns the *token for an action*; this layer owns *which action is live per Task
    // Detail tab*. It only needs to list the actions whose live-ness depends on the front tab — the ones
    // that share a token with another action (today just Ctrl+N: AddComment vs AddChecklistItem) or are
    // physically scoped to one tab (the checklist chords, already guarded to the Checklists ListView in
    // TaskDetailScreen). Context-wide Detail actions (DispatchToClaude, ReplyToComment, EditDescription,
    // OpenInBrowser, QuickUpdate, Refresh, Help, Back) resolve unconditionally and are folded in by
    // DetailBindings rather than repeated per tab.
    private static readonly IReadOnlyList<KeyAction> DetailContextWideActions =
    [
        KeyAction.DispatchToClaude,
        KeyAction.ReplyToComment,
        KeyAction.EditDescription,
        KeyAction.OpenInBrowser,
        KeyAction.QuickUpdate,
        KeyAction.Refresh,
        KeyAction.Help,
        KeyAction.Back,
    ];

    private static readonly IReadOnlyDictionary<DetailSubContext, IReadOnlyList<KeyAction>> DetailTabActions =
        new Dictionary<DetailSubContext, IReadOnlyList<KeyAction>>
        {
            [DetailSubContext.Comments] = [KeyAction.AddComment],
            [DetailSubContext.Checklists] =
            [
                KeyAction.AddChecklistItem,
                KeyAction.EditChecklistItem,
                KeyAction.DeleteChecklistItem,
                KeyAction.ToggleChecklistItem,
                KeyAction.MoveChecklistItemUp,
                KeyAction.MoveChecklistItemDown,
                KeyAction.OutdentChecklistItem,
                KeyAction.IndentChecklistItem,
                KeyAction.NewChecklist,
            ],
            [DetailSubContext.TaskTree] = [KeyAction.AddComment],
            [DetailSubContext.Default] = [KeyAction.AddComment],
        };

    /// <summary>The tab-scoped actions live in <paramref name="sub"/> (falling back to
    /// <see cref="DetailSubContext.Default"/>'s set for a tab with no explicit entry).</summary>
    private static IReadOnlyList<KeyAction> DetailTabActionsFor(DetailSubContext sub)
        => DetailTabActions.TryGetValue(sub, out var actions) ? actions : DetailTabActions[DetailSubContext.Default];

    /// <summary>
    /// The <see cref="KeyAction"/> the front-most Task Detail tab binds <paramref name="token"/> to, or
    /// <c>null</c> when the tab binds nothing to it (contextual chords C, #540). Resolves against the
    /// sub-context's live tab-scoped actions plus the context-wide ones, each mapped back to its
    /// base-<see cref="Map"/> token. Both the hand-rolled dispatch in <c>TaskDetailScreen.OnKey</c> and
    /// the per-tab footer (<see cref="HelpItemSets.DetailFooter"/>) consult this seam so a chord and its
    /// footer label can't drift.
    /// <para>
    /// Anti-collision invariant: within one sub-context no token maps to two live actions (so
    /// <c>Ctrl+N</c> → exactly <see cref="KeyAction.AddChecklistItem"/> on the Checklists tab and exactly
    /// <see cref="KeyAction.AddComment"/> elsewhere). <see cref="KeybindingsTests"/> pins it.
    /// </para>
    /// </summary>
    public static KeyAction? ResolveDetail(DetailSubContext sub, string token)
    {
        foreach (var (action, bound) in DetailBindings(sub))
            if (bound == token)
                return action;
        return null;
    }

    /// <summary>
    /// The live <c>(action, token)</c> pairs for Task Detail sub-context <paramref name="sub"/>
    /// (contextual chords C, #540): the tab-scoped actions live on that tab plus the context-wide Detail
    /// actions, each resolved to its base-<see cref="Map"/> token. This is what the per-tab footer
    /// renders and what the #355 cross-check asserts the footer against.
    /// </summary>
    public static IEnumerable<(KeyAction Action, string Token)> DetailBindings(DetailSubContext sub)
    {
        foreach (var action in DetailTabActionsFor(sub))
            yield return (action, Token(ScreenContext.Detail, action));
        foreach (var action in DetailContextWideActions)
            yield return (action, Token(ScreenContext.Detail, action));
    }
}
