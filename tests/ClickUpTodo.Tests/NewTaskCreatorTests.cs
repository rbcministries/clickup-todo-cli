using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the multi-list New Task create orchestration (#241): create in the primary/home list,
/// then add to each additional selected list. Drives <see cref="NewTaskCreator.CreateAsync"/> against faked
/// facade delegates (no Terminal.Gui, no network), asserting the create target, the additional-add set and
/// order, the single-list no-add path, primary exclusion / de-dup, and the partial-failure model (a failed
/// additional add is recorded without discarding the created task or aborting the rest).
/// </summary>
public sealed class NewTaskCreatorTests
{
    private static NamedEntity L(string id, string? name = null) => new(id, name ?? $"List {id}");

    private static readonly NewTaskRequest Request = new() { Name = "Ship it" };

    // A create delegate that records the target list id and returns a task in that list.
    private static Func<string, NewTaskRequest, CancellationToken, Task<TaskItem>> Creator(
        List<string> createdIn, string taskId = "task-1")
        => (listId, _, _) =>
        {
            createdIn.Add(listId);
            return Task.FromResult(new TaskItem { Id = taskId, Name = "Ship it", ListId = listId });
        };

    // An add delegate that records (taskId, listId) pairs; throws for any list id in failFor.
    private static Func<string, string, CancellationToken, Task> Adder(
        List<(string TaskId, string ListId)> added, ISet<string>? failFor = null)
        => (taskId, listId, _) =>
        {
            // Mirrors the caught "Tasks in Multiple Lists disabled" (ClickUp OV_016) add failure the facade
            // surfaces; the orchestrator catches any exception, so the concrete type is immaterial here.
            if (failFor is not null && failFor.Contains(listId))
                throw new ClickUpApiException(400, "AddTaskToList", new InvalidOperationException("OV_016"));
            added.Add((taskId, listId));
            return Task.CompletedTask;
        };

    [Fact]
    public async Task SingleList_CreatesInPrimary_AndIssuesNoAddCalls()
    {
        var createdIn = new List<string>();
        var added = new List<(string, string)>();

        var result = await NewTaskCreator.CreateAsync(
            L("home"), [L("home")], Request, Creator(createdIn), Adder(added));

        Assert.Equal(["home"], createdIn);
        Assert.Empty(added);
        Assert.True(result.AllListsSucceeded);
        Assert.Empty(result.FailedAdditionalLists);
        Assert.Equal("task-1", result.Created.Id);
    }

    [Fact]
    public async Task MultipleLists_CreatesInPrimary_ThenAddsTheRestInOrder()
    {
        var createdIn = new List<string>();
        var added = new List<(string TaskId, string ListId)>();

        var result = await NewTaskCreator.CreateAsync(
            L("home"), [L("home"), L("b"), L("c")], Request, Creator(createdIn), Adder(added));

        Assert.Equal(["home"], createdIn);                       // created once, in the primary
        Assert.Equal([("task-1", "b"), ("task-1", "c")], added); // added to the other two, in order
        Assert.True(result.AllListsSucceeded);
    }

    [Fact]
    public async Task PrimaryExcludedFromAdds_EvenWhenNotFirstInSelection()
    {
        // Primary "home" appears after "a" in the ordered selection (e.g. the seeded home was removed then
        // re-picked, so it fell to a later slot); it must still never be re-added.
        var createdIn = new List<string>();
        var added = new List<(string TaskId, string ListId)>();

        var result = await NewTaskCreator.CreateAsync(
            L("home"), [L("a"), L("home"), L("b")], Request, Creator(createdIn), Adder(added));

        Assert.Equal(["home"], createdIn);
        Assert.Equal([("task-1", "a"), ("task-1", "b")], added);
        Assert.DoesNotContain(added, x => x.ListId == "home");
        Assert.True(result.AllListsSucceeded);
    }

    [Fact]
    public async Task DuplicateAndBlankSelectionIds_AreDedupedAndDropped()
    {
        var createdIn = new List<string>();
        var added = new List<(string TaskId, string ListId)>();

        var result = await NewTaskCreator.CreateAsync(
            L("home"),
            [L("home"), L("b"), L("b"), L("  "), L(""), L("c")],
            Request, Creator(createdIn), Adder(added));

        Assert.Equal([("task-1", "b"), ("task-1", "c")], added);
        Assert.True(result.AllListsSucceeded);
    }

    [Fact]
    public async Task FailedAdditionalAdd_IsRecorded_TaskStillCreated_RemainingAddsStillRun()
    {
        var createdIn = new List<string>();
        var added = new List<(string TaskId, string ListId)>();
        var failFor = new HashSet<string> { "b" };

        var result = await NewTaskCreator.CreateAsync(
            L("home"), [L("home"), L("b", "Backlog"), L("c")], Request,
            Creator(createdIn), Adder(added, failFor));

        // The task exists (primary create succeeded) and the create wasn't rolled back.
        Assert.Equal("task-1", result.Created.Id);
        Assert.Equal(["home"], createdIn);
        // "b" failed but "c" (after it) still ran — no early abort.
        Assert.Equal([("task-1", "c")], added);
        Assert.False(result.AllListsSucceeded);
        var failed = Assert.Single(result.FailedAdditionalLists);
        Assert.Equal("b", failed.Id);
        Assert.Equal("Backlog", failed.Name);
    }

    [Fact]
    public async Task AllAdditionalAddsFail_AllRecorded_InOrder()
    {
        var createdIn = new List<string>();
        var added = new List<(string, string)>();
        var failFor = new HashSet<string> { "b", "c" };

        var result = await NewTaskCreator.CreateAsync(
            L("home"), [L("home"), L("b"), L("c")], Request, Creator(createdIn), Adder(added, failFor));

        Assert.Empty(added);
        Assert.Equal(["b", "c"], result.FailedAdditionalLists.Select(l => l.Id));
        // The created task survives even when every additional add fails (never rolled back).
        Assert.Equal("task-1", result.Created.Id);
    }

    [Fact]
    public async Task PrimaryCreateFailure_Propagates_AndNoAddsAttempted()
    {
        var added = new List<(string, string)>();
        Func<string, NewTaskRequest, CancellationToken, Task<TaskItem>> failingCreate =
            (_, _, _) => throw new ClickUpApiException(500, "CreateTask", new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<ClickUpApiException>(() => NewTaskCreator.CreateAsync(
            L("home"), [L("home"), L("b")], Request, failingCreate, Adder(added)));

        Assert.Empty(added);
    }

    [Fact]
    public async Task Cancellation_DuringAdds_IsRethrown_NotRecordedAsFailure()
    {
        var createdIn = new List<string>();
        using var cts = new CancellationTokenSource();
        Func<string, string, CancellationToken, Task> cancelingAdd = (_, _, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => NewTaskCreator.CreateAsync(
            L("home"), [L("home"), L("b")], Request, Creator(createdIn), cancelingAdd, cts.Token));
    }

    [Fact]
    public async Task AmbientTimeoutDuringAdd_WhenOurTokenNotCancelled_IsRecordedAsFailure_NotRethrown()
    {
        // A Kiota HttpClient timeout surfaces as a TaskCanceledException (an OperationCanceledException)
        // even though our own token was never cancelled. The task is already created, so it must be
        // recorded as a failed add — not unwound (which would keep the form open and risk a duplicate).
        var createdIn = new List<string>();
        var added = new List<(string TaskId, string ListId)>();
        Func<string, string, CancellationToken, Task> timingOutAdd = (taskId, listId, _) =>
        {
            if (listId == "b")
                throw new TaskCanceledException("The request timed out.");
            added.Add((taskId, listId));
            return Task.CompletedTask;
        };

        var result = await NewTaskCreator.CreateAsync(
            L("home"), [L("home"), L("b", "Backlog"), L("c")], Request,
            Creator(createdIn), timingOutAdd, CancellationToken.None);

        Assert.Equal("task-1", result.Created.Id);
        Assert.Equal([("task-1", "c")], added);            // the add after the timeout still ran
        var failed = Assert.Single(result.FailedAdditionalLists);
        Assert.Equal("b", failed.Id);
    }
}
