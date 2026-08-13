using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure main-list <c>Ctrl+N</c> sibling-vs-child classifier (contextual chords G,
/// #544). The Terminal.Gui glue (the native <c>ChoiceDialog</c>, the New-task/New-subtask open) is
/// verified by build + <c>tui-validate</c> per the repo's TUI rule; this locks the decision it delegates:
/// which cursors prompt, and which parent the Sub-add carries.
/// </summary>
public sealed class TaskAddChoiceModelTests
{
    [Fact]
    public void ForCursor_OwnHighlightedTask_PromptsWithThatParent()
    {
        var choice = TaskAddChoiceModel.ForCursor("t1", "Write the docs", isContextParent: false, isForeignSubtask: false);

        Assert.True(choice.Prompt);
        Assert.Equal("t1", choice.ParentTaskId);
        Assert.Equal("Write the docs", choice.ParentTaskName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForCursor_NoHighlightedTask_AddsSiblingWithoutPrompt(string? id)
    {
        var choice = TaskAddChoiceModel.ForCursor(id, highlightedTaskName: null, isContextParent: false, isForeignSubtask: false);

        Assert.False(choice.Prompt);
        Assert.Null(choice.ParentTaskId);
        Assert.Null(choice.ParentTaskName);
    }

    [Fact]
    public void ForCursor_ContextParentRow_AddsSiblingWithoutPrompt()
    {
        // A #46 context parent isn't the user's own work to file a subtask under.
        var choice = TaskAddChoiceModel.ForCursor("p1", "Some parent", isContextParent: true, isForeignSubtask: false);

        Assert.False(choice.Prompt);
        Assert.Null(choice.ParentTaskId);
    }

    [Fact]
    public void ForCursor_ForeignSubtaskRow_AddsSiblingWithoutPrompt()
    {
        // A #70/#179 foreign subtask isn't the user's own work either.
        var choice = TaskAddChoiceModel.ForCursor("s1", "Foreign subtask", isContextParent: false, isForeignSubtask: true);

        Assert.False(choice.Prompt);
        Assert.Null(choice.ParentTaskId);
    }

    [Fact]
    public void ForCursor_PreservesParentNameVerbatim_IncludingBlank()
    {
        // The name is display-only (the dialog message); a blank name still yields a prompt keyed on the id.
        var choice = TaskAddChoiceModel.ForCursor("t9", "", isContextParent: false, isForeignSubtask: false);

        Assert.True(choice.Prompt);
        Assert.Equal("t9", choice.ParentTaskId);
        Assert.Equal("", choice.ParentTaskName);
    }
}
