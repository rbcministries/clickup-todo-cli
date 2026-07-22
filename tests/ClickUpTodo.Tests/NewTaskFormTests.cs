using System.Globalization;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure New Task screen validator (#213/#215/#240): name required + trimmed, empty
/// description omitted, assignee ids de-duped / non-positive dropped with order preserved, the optional
/// Priority level (pass-through, clamped to the four canonical levels) and Due date (parsed via the
/// shared date convention; blank omitted; invalid blocks Save), plus the required primary list (#240)
/// and the cursor-vs-personal list seed resolution.
/// </summary>
public sealed class NewTaskFormTests
{
    // A valid primary list id for the cases that aren't exercising the list requirement itself.
    private const string List = "list-1";

    // The epoch-ms the shared parser assigns to yyyy-MM-dd (UTC midnight), mirroring TaskView.
    private static long UtcMidnightMs(string date)
        => new DateTimeOffset(
            DateOnly.Parse(date, CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .ToUnixTimeMilliseconds();

    [Fact]
    public void TryBuild_ValidName_BuildsRequest()
    {
        var ok = NewTaskForm.TryBuild("Write the report", null, [], null, null, List, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("Write the report", request!.Name);
        Assert.Null(request.Description);
        Assert.Empty(request.Assignees);
        // The optional fields stay unset when not supplied.
        Assert.Null(request.PriorityLevel);
        Assert.Null(request.DueDateMs);
    }

    [Fact]
    public void TryBuild_TrimsName()
    {
        var ok = NewTaskForm.TryBuild("  Trimmed  ", null, [], null, null, List, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("Trimmed", request!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void TryBuild_BlankName_Fails(string? name)
    {
        var ok = NewTaskForm.TryBuild(name, "a description", [1], null, null, List, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(NewTaskForm.NameRequiredError, error);
    }

    [Fact]
    public void TryBuild_TrimsDescription_AndKeepsIt()
    {
        var ok = NewTaskForm.TryBuild("Name", "  some notes  ", [], null, null, List, out var request, out _);

        Assert.True(ok);
        Assert.Equal("some notes", request!.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuild_BlankDescription_OmittedAsNull(string? description)
    {
        var ok = NewTaskForm.TryBuild("Name", description, [], null, null, List, out var request, out _);

        Assert.True(ok);
        Assert.Null(request!.Description);
    }

    [Fact]
    public void TryBuild_DedupesAssignees_PreservingOrder()
    {
        var ok = NewTaskForm.TryBuild("Name", null, [3, 1, 3, 2, 1], null, null, List, out var request, out _);

        Assert.True(ok);
        Assert.Equal(new long[] { 3, 1, 2 }, request!.Assignees);
    }

    [Fact]
    public void TryBuild_DropsNonPositiveAssigneeIds()
    {
        var ok = NewTaskForm.TryBuild("Name", null, [0, -5, 7, -1, 9], null, null, List, out var request, out _);

        Assert.True(ok);
        Assert.Equal(new long[] { 7, 9 }, request!.Assignees);
    }

    // ── Priority ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TryBuild_CanonicalPriorityLevel_PassesThrough(int level)
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], level, null, List, out var request, out _);

        Assert.True(ok);
        Assert.Equal(level, request!.PriorityLevel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void TryBuild_NonCanonicalPriorityLevel_ClearedToNull(int? level)
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], level, null, List, out var request, out _);

        Assert.True(ok);
        Assert.Null(request!.PriorityLevel);
    }

    // ── Due date ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryBuild_IsoDate_ParsedToUtcMidnightEpochMs()
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], null, "2026-07-15", List, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(UtcMidnightMs("2026-07-15"), request!.DueDateMs);
    }

    [Fact]
    public void TryBuild_DueDateTrimmedBeforeParse()
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], null, "  2026-07-15  ", List, out var request, out _);

        Assert.True(ok);
        Assert.Equal(UtcMidnightMs("2026-07-15"), request!.DueDateMs);
    }

    [Fact]
    public void TryBuild_RawEpochMs_PassesThrough()
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], null, "1752537600000", List, out var request, out _);

        Assert.True(ok);
        Assert.Equal(1752537600000L, request!.DueDateMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuild_BlankDueDate_OmittedAsNull(string? dueDate)
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], null, dueDate, List, out var request, out _);

        Assert.True(ok);
        Assert.Null(request!.DueDateMs);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-40")]
    [InlineData("next tuesday")]
    public void TryBuild_InvalidDueDate_FailsAndBlocks(string dueDate)
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], null, dueDate, List, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(NewTaskForm.DueDateInvalidError, error);
    }

    [Fact]
    public void TryBuild_NameRequiredTakesPrecedenceOverInvalidDueDate()
    {
        var ok = NewTaskForm.TryBuild("", null, [], null, "not-a-date", List, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(NewTaskForm.NameRequiredError, error);
    }

    // ── Primary list requirement (#240) ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuild_BlankPrimaryListId_Fails(string? primaryListId)
    {
        var ok = NewTaskForm.TryBuild("Name", null, [], null, null, primaryListId, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(NewTaskForm.ListRequiredError, error);
    }

    [Fact]
    public void TryBuild_NameRequiredTakesPrecedenceOverMissingList()
    {
        var ok = NewTaskForm.TryBuild("", null, [], null, null, null, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(NewTaskForm.NameRequiredError, error);
    }

    [Fact]
    public void TryBuild_MissingListTakesPrecedenceOverInvalidDueDate()
    {
        // Name is valid, so the list check (which precedes the due-date parse) is what fails.
        var ok = NewTaskForm.TryBuild("Name", null, [], null, "not-a-date", "  ", out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(NewTaskForm.ListRequiredError, error);
    }

    [Fact]
    public void TryBuild_AllOptionalFields_BuildsCompleteRequest()
    {
        var ok = NewTaskForm.TryBuild(
            "Ship it", "  release notes  ", [5, 5, 8], 1, "2026-08-01", List, out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("Ship it", request!.Name);
        Assert.Equal("release notes", request.Description);
        Assert.Equal(new long[] { 5, 8 }, request.Assignees);
        Assert.Equal(1, request.PriorityLevel);
        Assert.Equal(UtcMidnightMs("2026-08-01"), request.DueDateMs);
    }

    // ── List seed resolution (#240) ──────────────────────────────────────────────

    [Fact]
    public void ResolveListSeed_OwnTaskRow_SeedsCursorList()
    {
        var seed = NewTaskForm.ResolveListSeed(
            cursorListId: "list-42", cursorListName: "Q3 Website Refresh",
            cursorIsContextParent: false, cursorIsForeignSubtask: false,
            personalListId: "plist", personalListName: "Personal Tasks");

        Assert.Equal("list-42", seed.Id);
        Assert.Equal("Q3 Website Refresh", seed.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveListSeed_BlankCursorList_FallsBackToPersonal(string? cursorListId)
    {
        var seed = NewTaskForm.ResolveListSeed(
            cursorListId: cursorListId, cursorListName: "ignored",
            cursorIsContextParent: false, cursorIsForeignSubtask: false,
            personalListId: "plist", personalListName: "Personal Tasks");

        Assert.Equal("plist", seed.Id);
        Assert.Equal("Personal Tasks", seed.Name);
    }

    [Fact]
    public void ResolveListSeed_ContextParentCursor_FallsBackToPersonal()
    {
        var seed = NewTaskForm.ResolveListSeed(
            cursorListId: "list-42", cursorListName: "Q3 Website Refresh",
            cursorIsContextParent: true, cursorIsForeignSubtask: false,
            personalListId: "plist", personalListName: "Personal Tasks");

        Assert.Equal("plist", seed.Id);
        Assert.Equal("Personal Tasks", seed.Name);
    }

    [Fact]
    public void ResolveListSeed_ForeignSubtaskCursor_FallsBackToPersonal()
    {
        var seed = NewTaskForm.ResolveListSeed(
            cursorListId: "list-42", cursorListName: "Q3 Website Refresh",
            cursorIsContextParent: false, cursorIsForeignSubtask: true,
            personalListId: "plist", personalListName: "Personal Tasks");

        Assert.Equal("plist", seed.Id);
        Assert.Equal("Personal Tasks", seed.Name);
    }

    [Fact]
    public void ResolveListSeed_NullCursorListName_SeedsEmptyName()
    {
        // A cursor task can carry a list id with no name; the seed keeps the id and an empty name
        // rather than throwing.
        var seed = NewTaskForm.ResolveListSeed(
            cursorListId: "list-42", cursorListName: null,
            cursorIsContextParent: false, cursorIsForeignSubtask: false,
            personalListId: "plist", personalListName: "Personal Tasks");

        Assert.Equal("list-42", seed.Id);
        Assert.Equal(string.Empty, seed.Name);
    }
}
