using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The consumer half of the cross-process nudge channel (#295) — the read-side of nudge-then-fetch
/// (the producer is #294). Scans the <see cref="IChangeMarkerStore"/>'s markers for another instance's
/// confirmed edits and reports which tasks to re-fetch, so a running instance can refresh <b>just that
/// task</b> (a per-task fetch, never a full working-set resync) without echoing its own writes.
/// <para>
/// <b>State is a cursor, not a list.</b> Per-process state is a single monotonic <see cref="Cursor"/>
/// plus this process's <c>InstanceId</c> — "I've processed everything with <c>Seq ≤ Cursor</c>." A
/// monotonic cursor subsumes per-item tracking: a re-edited task upserts a marker with a <i>higher</i>
/// <see cref="ChangeMarker.Seq"/> above the cursor, so it's naturally re-picked-up with no in-memory
/// change-list to age out.
/// </para>
/// <para>
/// This type is <b>pure</b> — markers and the two view predicates are passed in, no store or clock is
/// held — so its policy (cursor advancement, self-filtering, in-view/version suppression) is unit-
/// testable in isolation and the same core serves both callers (startup init + the periodic scan). It
/// is not thread-safe; the host drives it on a single (UI) thread.
/// </para>
/// </summary>
public sealed class ChangeMarkerConsumer(string instanceId)
{
    private readonly string _instanceId = instanceId ?? string.Empty;

    /// <summary>The high-water mark: every marker with <see cref="ChangeMarker.Seq"/> at or below this
    /// has been processed. Starts at 0; a fresh tab moves it to the current max via
    /// <see cref="Initialize"/>, and every <see cref="Advance"/> carries it past every marker it sees.</summary>
    public long Cursor { get; private set; }

    /// <summary>
    /// Fresh-tab init (#295 edge case 1): set the cursor to the current max <see cref="ChangeMarker.Seq"/>
    /// so a newly launched tab — which just did a full load and already holds everything — does <b>not</b>
    /// replay the whole <c>changes</c> table as "new" and fire a burst of redundant per-task fetches. An
    /// empty store leaves the cursor at 0. Idempotent for a given marker set.
    /// </summary>
    public void Initialize(IReadOnlyList<ChangeMarker> markers)
    {
        var max = 0L;
        foreach (var m in markers)
            if (m.Seq > max)
                max = m.Seq;
        Cursor = max;
    }

    /// <summary>
    /// Scan for markers newer than the cursor and return the distinct task ids to re-fetch, advancing the
    /// cursor past <b>every</b> marker seen. For each marker with <see cref="ChangeMarker.Seq"/> greater
    /// than <see cref="Cursor"/>, in ascending order:
    /// <list type="bullet">
    /// <item>the cursor advances past it unconditionally (so out-of-view and own markers don't linger);</item>
    /// <item>a marker written by this process (<c>InstanceId</c> match) is skipped — no self-echo;</item>
    /// <item>a marker whose task isn't in view is skipped (#295 edge case 2) — it'll render from the
    /// working set on the normal poll cadence if it later comes into view;</item>
    /// <item>a marker is suppressed when it carries a server time and the caller already holds a version at
    /// or beyond it (<paramref name="heldVersion"/> ≥ <see cref="ChangeMarker.ServerDateUpdatedMs"/>) —
    /// the redundant-fetch guard; a marker with no server time (e.g. a comment) always fetches;</item>
    /// <item>otherwise the task id is emitted, coalesced to first-seen order so several markers for one
    /// task (or a re-picked-up higher-<c>Seq</c> row) map to a single fetch.</item>
    /// </list>
    /// </summary>
    /// <param name="markers">The current markers. Ordered by <see cref="ChangeMarker.Seq"/> ascending by
    /// the store contract; sorted defensively here so an out-of-order source can't skip a row.</param>
    /// <param name="isInView">Whether a task id is currently displayed (working set or open detail).</param>
    /// <param name="heldVersion">The <c>date_updated</c> (epoch ms) the caller already holds for a task,
    /// or <see langword="null"/> when unknown — in which case the fetch is never suppressed.</param>
    public IReadOnlyList<string> Advance(
        IReadOnlyList<ChangeMarker> markers,
        Func<string, bool> isInView,
        Func<string, long?> heldVersion)
    {
        var ordered = markers.OrderBy(m => m.Seq);
        var toFetch = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursor = Cursor;

        foreach (var m in ordered)
        {
            if (m.Seq <= Cursor)
                continue; // already processed on a prior scan.
            if (m.Seq > cursor)
                cursor = m.Seq; // advance past every marker we see — own, out-of-view, or fetched.

            if (string.Equals(m.InstanceId, _instanceId, StringComparison.Ordinal))
                continue; // our own write — nothing to reconcile.
            if (string.IsNullOrEmpty(m.TaskId) || !isInView(m.TaskId))
                continue; // out of view: cursor advanced, no fetch (edge case 2).

            // Suppress a redundant fetch only when we can prove our copy is already current: the marker
            // carries a server time and what we hold is at or beyond it. A comment marker (no server time)
            // or an unknown held version always fetches — safe.
            if (m.ServerDateUpdatedMs is long server
                && heldVersion(m.TaskId) is long held
                && held >= server)
                continue;

            if (seen.Add(m.TaskId))
                toFetch.Add(m.TaskId);
        }

        Cursor = cursor;
        return toFetch;
    }
}
