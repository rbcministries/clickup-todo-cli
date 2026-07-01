using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="SectionLayout.BuildTodoSection"/> — how F3 grouping and the F4 nested
/// subtasks view compose (#57): each group is arranged independently, so a subtask nests only when it
/// shares its parent's group, a cross-group child stays flat, and a context parent injects per group.
/// </summary>
public sealed class SectionLayoutTests
{
    private static TaskItem Task(string id, string? parent = null)
        => new() { Id = id, Name = id, ParentId = parent };

    private static readonly Dictionary<string, TaskItem> NoContext = new();

    private static IReadOnlyList<LayoutRow> Build(
        IReadOnlyList<TaskGroup> groups, bool grouped, bool nest,
        string? ungroupedTasksHeader = null, IReadOnlyDictionary<string, TaskItem>? context = null)
        => SectionLayout.BuildTodoSection(groups, context ?? NoContext, grouped, nest, ungroupedTasksHeader);

    private static IEnumerable<string> TaskIds(IEnumerable<LayoutRow> rows)
        => rows.Where(r => !r.IsHeader).Select(r => r.Task!.Id);

    [Fact]
    public void Ungrouped_NoPinnedSection_EmitsTasksOnlyNoHeader()
    {
        var groups = new[] { new TaskGroup(null, new[] { Task("a"), Task("b") }) };

        var rows = Build(groups, grouped: false, nest: false, ungroupedTasksHeader: null);

        Assert.All(rows, r => Assert.False(r.IsHeader));
        Assert.Equal(["a", "b"], TaskIds(rows));
    }

    [Fact]
    public void Ungrouped_WithPinnedSection_EmitsSingleTasksHeaderThenTasks()
    {
        var groups = new[] { new TaskGroup(null, new[] { Task("a"), Task("b") }) };

        var rows = Build(groups, grouped: false, nest: false, ungroupedTasksHeader: "─ TASKS (2) ─");

        Assert.True(rows[0].IsHeader);
        Assert.Equal("─ TASKS (2) ─", rows[0].HeaderText);
        Assert.Equal(["a", "b"], TaskIds(rows));
        Assert.Single(rows, r => r.IsHeader); // exactly one header
    }

    [Fact]
    public void Grouped_EmitsUppercaseHeaderWithCountPerGroup()
    {
        var groups = new[]
        {
            new TaskGroup("To Do", new[] { Task("a"), Task("b") }),
            new TaskGroup("Done", new[] { Task("c") }),
        };

        var rows = Build(groups, grouped: true, nest: false, ungroupedTasksHeader: "─ TASKS (3) ─");

        // The ungrouped header is never used when grouping; per-group headers are uppercased with counts.
        var headers = rows.Where(r => r.IsHeader).Select(r => r.HeaderText!).ToArray();
        Assert.Equal(["─ TO DO (2) ─", "─ DONE (1) ─"], headers);
        Assert.Equal(["a", "b", "c"], TaskIds(rows));
    }

    [Fact]
    public void GroupedAndNested_ParentAndChildInSameGroup_ChildNestsUnderParent()
    {
        var groups = new[] { new TaskGroup("In Progress", new[] { Task("p"), Task("c", parent: "p") }) };

        var rows = Build(groups, grouped: true, nest: true);

        var tasks = rows.Where(r => !r.IsHeader).ToArray();
        Assert.Equal(["p", "c"], tasks.Select(r => r.Task!.Id));
        Assert.Equal([0, 1], tasks.Select(r => r.Depth));
    }

    [Fact]
    public void GroupedAndNested_ParentInDifferentGroup_ChildRendersFlat()
    {
        // group-by-status: parent "p" is In Progress, its child "c" is To Do → different groups.
        var groups = new[]
        {
            new TaskGroup("In Progress", new[] { Task("p") }),
            new TaskGroup("To Do", new[] { Task("c", parent: "p") }),
        };

        var rows = Build(groups, grouped: true, nest: true);

        // c stays flat (depth 0) in its own group; it is not pulled under p (which is elsewhere).
        var c = Assert.Single(rows, r => !r.IsHeader && r.Task!.Id == "c");
        Assert.Equal(0, c.Depth);
        Assert.False(c.IsContextParent);
        // p is a top-level anchor in its own group.
        var p = Assert.Single(rows, r => !r.IsHeader && r.Task!.Id == "p");
        Assert.Equal(0, p.Depth);
        Assert.Equal(["IN PROGRESS", "p", "TO DO", "c"],
            rows.Select(r => r.IsHeader ? StripHeader(r.HeaderText!) : r.Task!.Id));
    }

    [Fact]
    public void GroupedAndNested_ContextParent_InjectedAsHeaderWithinChildsGroup()
    {
        // The child's parent "P" is not assigned to the user (absent from every group) but resolvable.
        var groups = new[]
        {
            new TaskGroup("To Do", new[] { Task("x") }),
            new TaskGroup("Done", new[] { Task("c", parent: "P") }),
        };
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Build(groups, grouped: true, nest: true, context: context);

        // P is injected inside the "Done" group (where its child is), as a context-parent task row.
        var p = Assert.Single(rows, r => !r.IsHeader && r.Task!.Id == "P");
        Assert.True(p.IsContextParent);
        Assert.Equal(0, p.Depth);
        var c = Assert.Single(rows, r => !r.IsHeader && r.Task!.Id == "c");
        Assert.Equal(1, c.Depth); // nested under the injected header
        Assert.Equal(["TO DO", "x", "DONE", "P", "c"],
            rows.Select(r => r.IsHeader ? StripHeader(r.HeaderText!) : r.Task!.Id));
    }

    [Fact]
    public void GroupedAndNested_GroupHeaderCount_ExcludesInjectedContextParent()
    {
        var groups = new[] { new TaskGroup("Done", new[] { Task("c", parent: "P") }) };
        var context = new Dictionary<string, TaskItem> { ["P"] = Task("P") };

        var rows = Build(groups, grouped: true, nest: true, context: context);

        // Header counts the real group member (1: the child), not the injected context-parent header.
        Assert.Equal("─ DONE (1) ─", rows[0].HeaderText);
    }

    [Fact]
    public void Grouped_NotNested_SubtasksStayFlatEvenWhenParentSharesGroup()
    {
        // With nesting off, grouping alone never indents — parity with the pre-#57 behaviour.
        var groups = new[] { new TaskGroup("In Progress", new[] { Task("p"), Task("c", parent: "p") }) };

        var rows = Build(groups, grouped: true, nest: false);

        Assert.All(rows.Where(r => !r.IsHeader), r => Assert.Equal(0, r.Depth));
        Assert.Equal(["p", "c"], TaskIds(rows));
    }

    // Reduce a "─ LABEL (n) ─" header down to its label for order assertions.
    private static string StripHeader(string header)
        => header.Trim('─', ' ').Split(" (")[0];
}
