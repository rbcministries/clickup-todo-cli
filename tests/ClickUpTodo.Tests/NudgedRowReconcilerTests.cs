using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure ordering decision behind the cross-tab nudge list-row reconcile (#376):
/// drop a stale out-of-order fetch, apply a newer/equal/unorderable one, and never let the row's
/// activity stamp regress to null when the fetch omitted it.
/// </summary>
public sealed class NudgedRowReconcilerTests
{
    private static TaskItem Task(string id, long? updatedMs, string? status = "to do") =>
        new() { Id = id, Name = id, StatusName = status, UpdatedMs = updatedMs };

    [Fact]
    public void Reconcile_StaleFetch_IsDropped()
    {
        var existing = Task("t1", updatedMs: 2000, status: "in progress");
        var fresh = Task("t1", updatedMs: 1000, status: "to do"); // older activity stamp

        Assert.Null(NudgedRowReconciler.Reconcile(existing, fresh));
    }

    [Fact]
    public void Reconcile_NewerFetch_IsAppliedWholesale()
    {
        var existing = Task("t1", updatedMs: 1000, status: "to do");
        var fresh = Task("t1", updatedMs: 2000, status: "in progress");

        var result = NudgedRowReconciler.Reconcile(existing, fresh);

        Assert.Same(fresh, result); // returned unchanged — the full-fidelity replacement
    }

    [Fact]
    public void Reconcile_EqualStamp_IsApplied()
    {
        var existing = Task("t1", updatedMs: 1000, status: "to do");
        var fresh = Task("t1", updatedMs: 1000, status: "blocked");

        var result = NudgedRowReconciler.Reconcile(existing, fresh);

        Assert.Same(fresh, result);
    }

    [Fact]
    public void Reconcile_ExistingHasNoStamp_CannotOrder_IsApplied()
    {
        var existing = Task("t1", updatedMs: null);
        var fresh = Task("t1", updatedMs: 1000, status: "in progress");

        var result = NudgedRowReconciler.Reconcile(existing, fresh);

        Assert.Same(fresh, result);
    }

    [Fact]
    public void Reconcile_FreshHasNoStamp_InheritsExistingStamp()
    {
        var existing = Task("t1", updatedMs: 1500);
        var fresh = Task("t1", updatedMs: null, status: "in progress");

        var result = NudgedRowReconciler.Reconcile(existing, fresh);

        Assert.NotNull(result);
        Assert.Equal("in progress", result!.StatusName);   // still the fresh record's fields
        Assert.Equal(1500, result.UpdatedMs);               // but the stamp never regresses to null
    }

    [Fact]
    public void Reconcile_BothHaveNoStamp_IsApplied()
    {
        var existing = Task("t1", updatedMs: null);
        var fresh = Task("t1", updatedMs: null, status: "done");

        var result = NudgedRowReconciler.Reconcile(existing, fresh);

        Assert.NotNull(result);
        Assert.Equal("done", result!.StatusName);
        Assert.Null(result.UpdatedMs);
    }
}
