using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

public sealed class StatusExclusionTests
{
    private static TaskItem Task(string id, string? status) => new() { Id = id, Name = id, StatusName = status };

    [Fact]
    public void ExcludeByStatus_RemovesMatches_CaseInsensitively()
    {
        TaskItem[] tasks =
        [
            Task("1", "to do"),
            Task("2", "Won't Do"),
            Task("3", "in progress"),
            Task("4", "CANCELLED"),
        ];

        var kept = TaskService.ExcludeByStatus(tasks, ["won't do", "cancelled"]).Select(t => t.Id).ToList();

        Assert.Equal(["1", "3"], kept);
    }

    [Fact]
    public void ExcludeByStatus_KeepsTasksWithNoStatus()
    {
        TaskItem[] tasks = [Task("1", null), Task("2", "cancelled")];

        var kept = TaskService.ExcludeByStatus(tasks, ["cancelled"]).Select(t => t.Id).ToList();

        Assert.Equal(["1"], kept);
    }

    [Fact]
    public void ExcludeByStatus_EmptyExclusionList_KeepsEverything()
    {
        TaskItem[] tasks = [Task("1", "cancelled"), Task("2", "to do")];

        var kept = TaskService.ExcludeByStatus(tasks, []).Select(t => t.Id).ToList();

        Assert.Equal(["1", "2"], kept);
    }
}
