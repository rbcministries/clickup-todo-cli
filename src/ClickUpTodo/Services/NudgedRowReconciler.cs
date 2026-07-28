using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// The pure ordering decision behind the cross-tab nudge list-row reconcile (#376/#295): given the row
/// already on screen (<paramref name="existing"/>) and the authoritative full <see cref="TaskItem"/>
/// just fetched for a nudged task (<paramref name="fresh"/>, from
/// <see cref="TaskService.GetTaskItemAsync"/>), decide the record to fold onto the row — or that this
/// fetch should be dropped.
/// <para>
/// Nudged fetches are fire-and-forget and can overlap across marker-poll ticks (or race the delta
/// poll), so an older in-flight fetch resolving last must not clobber a newer version already on the
/// row. This is the row-path analogue of the detail path's <c>_refreshingDetail</c> / Quick Updates'
/// commit-generation guards, extracted here so it is unit-testable without a terminal.
/// </para>
/// </summary>
public static class NudgedRowReconciler
{
    /// <summary>
    /// Returns the <see cref="TaskItem"/> to apply to the row <b>wholesale</b>, or <c>null</c> when the
    /// fetch is a stale out-of-order result that should be discarded.
    /// <list type="bullet">
    /// <item>When both sides carry an <see cref="TaskItem.UpdatedMs"/> and <paramref name="fresh"/>'s is
    /// strictly older, the fetch is stale ⇒ <c>null</c>. A missing stamp on either side can't be ordered
    /// ⇒ apply (best-effort).</item>
    /// <item>When <paramref name="fresh"/> carries no <see cref="TaskItem.UpdatedMs"/>, the returned
    /// record inherits <paramref name="existing"/>'s so the row's activity stamp never regresses to null
    /// (a later stale fetch can still be ordered out).</item>
    /// <item>Otherwise <paramref name="fresh"/> is returned unchanged — the full-fidelity replacement.</item>
    /// </list>
    /// </summary>
    public static TaskItem? Reconcile(TaskItem existing, TaskItem fresh)
    {
        if (fresh.UpdatedMs is long fu && existing.UpdatedMs is long eu && fu < eu)
            return null;

        return fresh.UpdatedMs is null
            ? fresh with { UpdatedMs = existing.UpdatedMs }
            : fresh;
    }
}
