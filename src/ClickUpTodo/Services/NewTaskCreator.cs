using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// The outcome of a multi-list New Task create (#241): the created task plus any <b>additional</b> lists
/// that couldn't be added afterwards. The task itself always exists once this is produced — a failed
/// additional-list add never discards it (see <see cref="NewTaskCreator"/>). <see cref="AllListsSucceeded"/>
/// is the single-list happy path and the multi-list all-added path; a non-empty
/// <see cref="FailedAdditionalLists"/> is the partial-failure case the host surfaces to the user.
/// </summary>
public sealed record NewTaskCreateResult(TaskItem Created, IReadOnlyList<NamedEntity> FailedAdditionalLists)
{
    /// <summary>True when every additional list was added (or there were none) — nothing to report.</summary>
    public bool AllListsSucceeded => FailedAdditionalLists.Count == 0;
}

/// <summary>
/// Pure, Terminal.Gui-free orchestration for creating a task across multiple lists (#241), factored out of
/// <see cref="Tui.Screens.NewTaskScreen"/> so the create-then-add sequence is unit-tested against faked
/// facade delegates (mirrors <see cref="Tui.Screens.NewTaskForm"/>). A new task is POSTed to exactly one
/// primary/home list (#209); the "Tasks in Multiple Lists" feature (#237) adds it to any further selected
/// lists afterwards.
/// <para>
/// Failure model: the primary create can throw (the task doesn't exist — the caller keeps the form open);
/// once it succeeds the task is created and is never rolled back, so a failing <em>additional</em> add is
/// caught and recorded in <see cref="NewTaskCreateResult.FailedAdditionalLists"/> while the remaining adds
/// still run. Cancellation (<see cref="OperationCanceledException"/>) is rethrown rather than recorded as a
/// list failure.
/// </para>
/// </summary>
public static class NewTaskCreator
{
    /// <summary>
    /// Creates the task in <paramref name="primary"/> from <paramref name="request"/>, then adds it to each
    /// additional selected list. The additional lists are <paramref name="selection"/> minus
    /// <paramref name="primary"/> (matched by id), de-duped and with blank ids dropped, in selection order —
    /// so the single-list path (only the primary selected) issues <b>no</b> add calls, and the primary is
    /// never re-added even when it isn't <c>selection[0]</c>. Runs on the caller's thread (the screen calls
    /// it from an off-UI-thread <c>Task.Run</c>).
    /// </summary>
    /// <param name="primary">The primary/home create target (its id is the create endpoint's path parameter).</param>
    /// <param name="selection">The full ordered list selection (may include the primary).</param>
    /// <param name="request">The validated task fields to create.</param>
    /// <param name="createAsync">Creates the task in the given list and returns it mapped — the create facade (#209).</param>
    /// <param name="addToListAsync">Adds the created task to an additional list — the membership write (#237).</param>
    public static async Task<NewTaskCreateResult> CreateAsync(
        NamedEntity primary,
        IReadOnlyList<NamedEntity> selection,
        NewTaskRequest request,
        Func<string, NewTaskRequest, CancellationToken, Task<TaskItem>> createAsync,
        Func<string, string, CancellationToken, Task> addToListAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(createAsync);
        ArgumentNullException.ThrowIfNull(addToListAsync);

        // Additional = the selection minus the primary, de-duped, blank ids dropped, order preserved.
        var additional = new List<NamedEntity>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { primary.Id };
        foreach (var list in selection)
        {
            if (!string.IsNullOrWhiteSpace(list.Id) && seen.Add(list.Id))
                additional.Add(list);
        }

        // The primary POST creates the task; a failure here propagates (task not created — form stays open).
        var created = await createAsync(primary.Id, request, ct).ConfigureAwait(false);

        // The task now exists and is never rolled back: catch per-list add failures, keep going.
        var failed = new List<NamedEntity>();
        foreach (var list in additional)
        {
            try
            {
                await addToListAsync(created.Id, list.Id, ct).ConfigureAwait(false);
            }
            // Only a cancellation of *our* token (the screen tore down / the user cancelled) unwinds — the
            // task is created, so bailing is correct there. An ambient HTTP timeout also surfaces as an
            // OperationCanceledException (a Kiota HttpClient TaskCanceledException, not wrapped by Guard);
            // treat that like any other add failure so the created task isn't discarded and a retry can't
            // create a duplicate.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failed.Add(list);
            }
        }

        return new NewTaskCreateResult(created, failed);
    }
}
