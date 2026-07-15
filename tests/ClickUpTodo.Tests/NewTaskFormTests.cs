using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure New Task screen validator (#213): name required + trimmed, empty description
/// omitted, assignee ids de-duped / non-positive dropped with order preserved.
/// </summary>
public sealed class NewTaskFormTests
{
    [Fact]
    public void TryBuild_ValidName_BuildsRequest()
    {
        var ok = NewTaskForm.TryBuild("Write the report", null, [], out var request, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal("Write the report", request!.Name);
        Assert.Null(request.Description);
        Assert.Empty(request.Assignees);
        // Fields this screen doesn't surface stay unset (they're #215's job).
        Assert.Null(request.PriorityLevel);
        Assert.Null(request.DueDateMs);
    }

    [Fact]
    public void TryBuild_TrimsName()
    {
        var ok = NewTaskForm.TryBuild("  Trimmed  ", null, [], out var request, out var error);

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
        var ok = NewTaskForm.TryBuild(name, "a description", [1], out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.Equal(NewTaskForm.NameRequiredError, error);
    }

    [Fact]
    public void TryBuild_TrimsDescription_AndKeepsIt()
    {
        var ok = NewTaskForm.TryBuild("Name", "  some notes  ", [], out var request, out _);

        Assert.True(ok);
        Assert.Equal("some notes", request!.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuild_BlankDescription_OmittedAsNull(string? description)
    {
        var ok = NewTaskForm.TryBuild("Name", description, [], out var request, out _);

        Assert.True(ok);
        Assert.Null(request!.Description);
    }

    [Fact]
    public void TryBuild_DedupesAssignees_PreservingOrder()
    {
        var ok = NewTaskForm.TryBuild("Name", null, [3, 1, 3, 2, 1], out var request, out _);

        Assert.True(ok);
        Assert.Equal(new long[] { 3, 1, 2 }, request!.Assignees);
    }

    [Fact]
    public void TryBuild_DropsNonPositiveAssigneeIds()
    {
        var ok = NewTaskForm.TryBuild("Name", null, [0, -5, 7, -1, 9], out var request, out _);

        Assert.True(ok);
        Assert.Equal(new long[] { 7, 9 }, request!.Assignees);
    }
}
