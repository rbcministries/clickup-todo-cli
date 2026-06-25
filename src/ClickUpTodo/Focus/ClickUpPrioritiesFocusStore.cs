namespace ClickUpTodo.Focus;

/// <summary>
/// Placeholder for a future <see cref="IFocusStore"/> backed by ClickUp <b>Personal Priorities</b>
/// (the rebrand of LineUp), which has the same semantics as our local pin list but server-side and
/// shared with the ClickUp UI.
/// <para>
/// This is intentionally <b>not implemented</b> and not wired up anywhere: the public ClickUp API
/// does not yet expose Personal Priorities (no read/write endpoint, and the task payload carries no
/// "on my Priorities list" flag — there is an open Public-API feature request for exactly this). It
/// exists so the extension point is explicit; swap <see cref="LocalFocusStore"/> for this once the
/// endpoint ships. Until then, every member throws.
/// </para>
/// </summary>
public sealed class ClickUpPrioritiesFocusStore : IFocusStore
{
    private const string NotAvailable =
        "ClickUp Personal Priorities is not yet exposed by the public API; use LocalFocusStore. See issue #22.";

    public ValueTask<IReadOnlySet<string>> GetPinnedAsync(CancellationToken ct = default)
        => throw new NotSupportedException(NotAvailable);

    public bool IsPinned(string taskId) => throw new NotSupportedException(NotAvailable);

    public ValueTask<bool> ToggleAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException(NotAvailable);
}
