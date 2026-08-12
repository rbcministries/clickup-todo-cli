using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>What the host should do for a single Quick Updates List-pane toggle (#242/#365).</summary>
public enum ListApplyAction
{
    /// <summary>Write the add. Adding a task to an additional list keeps every value and merely exposes
    /// that list's fields, so an add can never lose data — it always proceeds without a prompt.</summary>
    WriteAdd,

    /// <summary>Write the remove. Reached when the remove strands no set Custom Field value, or when the
    /// user has already confirmed a stranding remove (the pane was <c>armed</c>).</summary>
    WriteRemove,

    /// <summary>Refuse the remove: it targets the task's <b>home</b> list, which is a <i>move</i> (a
    /// different operation, out of scope here) — flash and leave the membership unchanged.</summary>
    BlockHomeRemove,

    /// <summary>The remove would hide set Custom Field values that only the removed list defines. Don't
    /// write yet: flash which values would be lost and <b>arm</b> the pane so a second remove confirms.
    /// The host returns the unchanged membership so the embedded selector re-shows the row.</summary>
    ArmRemoveConfirmation,
}

/// <summary>The planned action plus the status-line message to flash (null when none).</summary>
public sealed record ListApplyDecision(ListApplyAction Action, string? Message);

/// <summary>
/// Pure, terminal-free decision for a Quick Updates List-pane add/remove (#242/#365), factored out of
/// <c>TodoApp.ApplyListAsync</c> so the branching is unit-tested without a terminal (mirrors the
/// pure-logic split of <see cref="Screens.QuickUpdatesModel"/> / <see cref="ListSelectorModel"/>).
/// <para>The field/status hazard itself — <i>which</i> values a remove would strand — is computed by
/// <see cref="Services.ListMembershipMigration.StrandedFieldsOnRemove"/> from the task's values and the
/// per-list field definitions; this planner takes that result (<paramref name="strandedFieldNames"/>)
/// and the home-list id and picks the action. Add is always safe; removing the home list is blocked (it's
/// a move); a remove that strands set values arms a confirmation the first time and writes the second.</para>
/// <para><b>Why arm/confirm on the status line rather than a modal.</b> The TUI is deliberately a
/// single-screen, no-<c>MessageBox</c> model — notable outcomes surface through the non-interactive
/// <c>Flash</c> line (tied to the #3 single-focusable-pane constraint). A two-step "press remove again to
/// confirm" keeps that model and composes cleanly with the selector's reconcile-from-server contract: the
/// arming turn returns the membership unchanged, so the row reappears and no data is touched until the
/// confirming turn. See <c>docs/plans/completed/list-change-field-status-migration.md</c>.</para>
/// </summary>
public static class ListMembershipApplyPlanner
{
    /// <summary>The flash shown when the user tries to remove a task's home list from the pane.</summary>
    public const string HomeRemoveMessage =
        "Can't remove a task's home list here — that's a move, not yet supported.";

    /// <summary>
    /// Decide the action for a toggle of <paramref name="list"/>.
    /// </summary>
    /// <param name="kind">The toggle the selector performed (<see cref="ToggleKind.Added"/> /
    /// <see cref="ToggleKind.Removed"/>).</param>
    /// <param name="list">The toggled list.</param>
    /// <param name="homeListId">The task's home list id (never removable here); null/blank when unknown.</param>
    /// <param name="strandedFieldNames">The set Custom Field values a remove of <paramref name="list"/>
    /// would hide (from <see cref="Services.ListMembershipMigration.StrandedFieldsOnRemove"/>); empty for
    /// an add or a remove that strands nothing.</param>
    /// <param name="armed">Whether this exact remove was already flagged once and is awaiting confirmation.</param>
    public static ListApplyDecision Plan(
        ToggleKind kind,
        NamedEntity list,
        string? homeListId,
        IReadOnlyList<string> strandedFieldNames,
        bool armed)
    {
        if (kind == ToggleKind.Added)
            return new(ListApplyAction.WriteAdd, null);

        // From here it's a remove. The home list is never removable from this pane (removing it is a
        // move — a different endpoint, out of scope): block it before any preflight matters.
        if (!string.IsNullOrWhiteSpace(homeListId)
            && string.Equals(list.Id, homeListId, StringComparison.Ordinal))
            return new(ListApplyAction.BlockHomeRemove, HomeRemoveMessage);

        // Nothing stranded, or the user already confirmed → write the remove.
        if (strandedFieldNames is null || strandedFieldNames.Count == 0 || armed)
            return new(ListApplyAction.WriteRemove, null);

        // First stranding remove → arm and warn; the host returns the unchanged membership so the row
        // re-shows, and a second remove of the same list confirms.
        return new(ListApplyAction.ArmRemoveConfirmation, StrandWarning(list.Name, strandedFieldNames));
    }

    /// <summary>The confirmation flash naming the values a stranding remove would hide.</summary>
    public static string StrandWarning(string listName, IReadOnlyList<string> fields)
        => $"Removing '{listName}' hides these Custom Field values: {string.Join(", ", fields)}. Press remove again to confirm.";
}
