namespace ClickUpTodo.Services;

/// <summary>
/// The single-task launch mode's view predicates for the nudge-channel consumer (#377, part of the
/// multi-tab epic #292). The single-task tab (<c>--task</c>, #296) holds exactly one task, so the
/// dashboard consumer's "in view" test collapses to "is this the launch task" and its held-version
/// lookup collapses to that one task's <c>date_updated</c>.
/// <para>
/// Kept <b>pure</b> and host-free — the launch task id is fixed and the current held version is read
/// through an injected delegate the host refreshes — so the policy is unit-testable in CI without a
/// Terminal.Gui driver, mirroring why <see cref="ChangeMarkerConsumer"/> is pure. Not thread-safe; the
/// host drives it on the UI thread, the same thread it runs the consumer scan on.
/// </para>
/// </summary>
public sealed class SingleTaskNudgePolicy(string launchTaskId, Func<long?> heldVersion)
{
    private readonly string _taskId = launchTaskId ?? string.Empty;
    private readonly Func<long?> _heldVersion = heldVersion;

    /// <summary>A task is "in view" for the nudge scan iff it is the launch task — the single-task tab
    /// shows exactly one task, so any other id is out of view (its marker advances the cursor without a
    /// fetch, #295 edge case 2).</summary>
    public bool IsInView(string taskId) => string.Equals(taskId, _taskId, StringComparison.Ordinal);

    /// <summary>The <c>date_updated</c> (epoch ms) currently held for the launch task, so the consumer can
    /// suppress a redundant fetch when our copy is already at or beyond a marker's server time (#295).
    /// Read live through the injected supplier so a refresh since launch is reflected. Returns
    /// <see langword="null"/> for any other id (unknown ⇒ never suppressed) or when the held version is
    /// unknown.</summary>
    public long? HeldVersion(string taskId) => IsInView(taskId) ? _heldVersion() : null;
}
