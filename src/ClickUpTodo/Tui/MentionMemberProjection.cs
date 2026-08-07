using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// Projects the assignee-frequency pool's <see cref="TaskAssignee"/> candidates into the
/// <see cref="WorkspaceMember"/> shape the @-mention picker (#324) consumes — the single mapping the
/// comment composer's <c>memberMatch</c> / <c>memberTopFrequent</c> seams need (#325/#473).
/// <para>
/// The picker renders and keys on <see cref="WorkspaceMember.DisplayName"/>, which falls back through
/// <see cref="WorkspaceMember.Username"/> first, so a candidate's name is carried as the username
/// (<c>Email = null</c>). Pure and Terminal.Gui-free so it is unit-testable and shared by both hosts
/// (<see cref="TodoApp"/> and <see cref="SingleTaskApp"/>) rather than re-inlined per seam per host.
/// </para>
/// </summary>
public static class MentionMemberProjection
{
    /// <summary>Maps each <see cref="TaskAssignee"/> to a <see cref="WorkspaceMember"/> whose
    /// <see cref="WorkspaceMember.DisplayName"/> is the assignee's name (name ⇒ username, no email).
    /// Order is preserved, so the pool's frequency ranking survives the projection.</summary>
    public static IReadOnlyList<WorkspaceMember> ToMembers(IReadOnlyList<TaskAssignee> candidates)
        => candidates.Select(a => new WorkspaceMember(a.Id, a.Name, null)).ToList();
}
