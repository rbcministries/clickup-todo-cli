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
    NewTask,
    PromptTemplateEditor,
    NotificationsFeed,
    AgentRun,
    Help,
    ExitConfirm,
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
            [(ScreenContext.MainList, KeyAction.OpenInBrowser)] = "Ctrl+B",
            [(ScreenContext.MainList, KeyAction.TogglePin)] = "Ctrl+P",
            [(ScreenContext.MainList, KeyAction.Feed)] = "Ctrl+E",
            [(ScreenContext.MainList, KeyAction.Help)] = "F1",
            [(ScreenContext.MainList, KeyAction.Settings)] = "F2",
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

            // ── New Task ──────────────────────────────────────────────────────────────────────
            [(ScreenContext.NewTask, KeyAction.Help)] = "F1",
            [(ScreenContext.NewTask, KeyAction.Back)] = "Esc",

            // ── Prompt-template editor ────────────────────────────────────────────────────────
            [(ScreenContext.PromptTemplateEditor, KeyAction.Help)] = "F1",
            [(ScreenContext.PromptTemplateEditor, KeyAction.Back)] = "Esc",

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
}
