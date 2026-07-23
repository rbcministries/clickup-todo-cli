namespace ClickUpTodo.Configuration;

/// <summary>
/// One cross-process change nudge (#294) — a <b>notification, not a replica</b>. After a write is
/// confirmed by ClickUp, the writer records which task changed so another running instance can
/// re-fetch it (nudge-then-fetch); the marker deliberately does not carry the new field values, the
/// consumer (#295) reads those from the API.
/// <para>
/// Markers are keyed by <see cref="TaskId"/> — one row per task, a re-edit supersedes the prior row —
/// so the table is bounded by the number of distinct tasks touched, not the number of writes.
/// </para>
/// </summary>
/// <param name="TaskId">The task that changed (the marker's key).</param>
/// <param name="Seq">
/// A local monotonic counter, allocated atomically per emission — the cursor a consumer scans against
/// to find "what's new since I last looked" (#295).
/// </param>
/// <param name="ServerDateUpdatedMs">
/// ClickUp's server-confirmed <c>date_updated</c> from the write response, as Unix epoch ms — lets a
/// consumer that already holds the task suppress a redundant fetch when its copy is already ≥ this
/// version. <see langword="null"/> when the write response doesn't carry it (e.g. a comment post,
/// whose response returns the comment's own date, not the task's), which simply means the consumer
/// can't suppress and always fetches — safe.
/// </param>
/// <param name="ChangedFields">
/// Advisory hint of which fields changed (e.g. <c>["status"]</c>) — the fetch gets everything, so this
/// is for diagnostics/logging only, never for correctness.
/// </param>
/// <param name="InstanceId">The writer's per-process id, so a reader can skip its own markers (#295).</param>
/// <param name="RecordedUtcMs">
/// A local wall-clock stamp (Unix epoch ms) set when the marker was recorded, used only for TTL aging —
/// ClickUp's clock would skew across replicas and a comment marker carries no server time, so a local
/// stamp is the reliable aging clock.
/// </param>
public sealed record ChangeMarker(
    string TaskId,
    long Seq,
    long? ServerDateUpdatedMs,
    IReadOnlyList<string> ChangedFields,
    string InstanceId,
    long RecordedUtcMs);

/// <summary>
/// Bounds for the <c>changes</c> marker table (#294). Keyed-by-task the table is largely self-bounding;
/// these caps back it up so pathological churn or a long-idle store can't grow it without limit. Both
/// are enforced piggybacked on the write path (cheap — already inside the write transaction).
/// </summary>
/// <param name="Ttl">
/// Markers older than this (by <see cref="ChangeMarker.RecordedUtcMs"/>) are dropped. Sized as a small
/// multiple of the full-resync interval (the task list resyncs fully every ~10 poll cycles, ~10 min at
/// the default cadence): once a resync cycle has elapsed every live tab has already converged via normal
/// polling, so the marker is dead weight.
/// </param>
/// <param name="MaxEntries">
/// Hard cap on rows — the newest <c>MaxEntries</c> by <see cref="ChangeMarker.Seq"/> are kept, older
/// ones trimmed on write. A backstop against pathological churn, not the primary bound.
/// </param>
public sealed record ChangeMarkerOptions(TimeSpan Ttl, int MaxEntries)
{
    /// <summary>The default bounds: a 30-minute TTL (≈3× the full-resync window) and 500 rows.</summary>
    public static readonly ChangeMarkerOptions Default = new(TimeSpan.FromMinutes(30), 500);
}
