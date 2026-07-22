namespace ClickUpTodo.Configuration;

/// <summary>
/// The cross-process nudge channel (#294) — the producer surface. After a write is confirmed by
/// ClickUp, the facade records a <see cref="ChangeMarker"/> here so other running instances can
/// re-fetch the changed task (nudge-then-fetch). The consumer that scans these markers is #295.
/// <para>
/// Implementations must be safe to call from any thread and <b>must never throw</b>: a nudge is a
/// best-effort notification riding on an already-succeeded user edit, so a store failure (contention,
/// full disk) is swallowed, exactly as #293 established for the shared <c>state.db</c>.
/// </para>
/// </summary>
public interface IChangeMarkerStore
{
    /// <summary>This process's id, stamped on every marker it records so a consumer can skip its own
    /// writes (#295). Stable for the store's lifetime.</summary>
    string InstanceId { get; }

    /// <summary>
    /// Record (upsert, keyed by task) a change marker for a confirmed write. A re-edit of the same task
    /// supersedes its prior row. Allocates a fresh monotonic <see cref="ChangeMarker.Seq"/> atomically
    /// and trims the table to its bounds. Never throws — a persistence failure is swallowed.
    /// </summary>
    /// <param name="taskId">The task that changed.</param>
    /// <param name="serverDateUpdatedMs">The write response's confirmed <c>date_updated</c> (epoch ms),
    /// or <see langword="null"/> when the response didn't carry it.</param>
    /// <param name="changedFields">Advisory list of changed field names (diagnostics only).</param>
    void Record(string taskId, long? serverDateUpdatedMs, IReadOnlyList<string> changedFields);

    /// <summary>All current markers, ordered by <see cref="ChangeMarker.Seq"/> ascending. The base the
    /// consumer's cursor scan (#295) builds on; also the assertion surface for tests. Empty when none.</summary>
    IReadOnlyList<ChangeMarker> ReadAll();
}
