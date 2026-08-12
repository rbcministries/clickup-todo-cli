using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure unit tests for <see cref="ChecklistMove"/> (G, #569): the reorder/reparent ordering logic. Each
/// legal gesture is checked both on the <see cref="ChecklistMovePlan"/> it produces and by applying that
/// plan through <see cref="ChecklistItemEdits.Move"/> and re-projecting with <see cref="ChecklistArranger"/>
/// — so the computed <c>orderindex</c>/<c>parent</c> provably lands the item in the intended slot. Illegal
/// gestures (boundary no-ops, first-item indent, root outdent, reparent-under-descendant) return null with
/// no request. Uses the flat ParentId-pointer representation ClickUp actually returns.
/// </summary>
public sealed class ChecklistMoveTests
{
    private static TaskChecklistItem Item(string id, double? orderIndex = null, string? parentId = null)
        => new(id, id, false, orderIndex, parentId, null, null);

    private static TaskChecklist List(params TaskChecklistItem[] items)
        => new("c1", "checklist", 0, 0, 0, items);

    // a(0), b(1) → [b1(0), b2(1)], c(2)  — a flat list with ParentId pointers, in display order:
    //   a, b, b1, b2, c
    private static IReadOnlyList<TaskChecklist> Sample() =>
    [
        List(
            Item("a", 0),
            Item("b", 1),
            Item("b1", 0, parentId: "b"),
            Item("b2", 1, parentId: "b"),
            Item("c", 2)),
    ];

    private static IReadOnlyList<(string Id, int Depth)> Rows(IReadOnlyList<TaskChecklist> cls)
        => ChecklistArranger.Project(cls).Rows
            .Where(r => !r.IsHeader)
            .Select(r => (r.ItemId!, r.Depth))
            .ToList();

    private static IReadOnlyList<(string Id, int Depth)> Apply(IReadOnlyList<TaskChecklist> cls, ChecklistMovePlan plan)
        => Rows(ChecklistItemEdits.Move(cls, plan.ChecklistId, plan.ItemId, plan.NewParentId, plan.NewOrderIndex, plan.ClearParent));

    // ── boundary no-ops (illegal, null, no request) ─────────────────────────────────────────────────

    [Fact]
    public void Up_OnFirstRoot_IsIllegal()
        => Assert.Null(ChecklistMove.Plan(Sample(), "c1", "a", ChecklistMoveKind.Up));

    [Fact]
    public void Down_OnLastRoot_IsIllegal()
        => Assert.Null(ChecklistMove.Plan(Sample(), "c1", "c", ChecklistMoveKind.Down));

    [Fact]
    public void Up_OnFirstChild_IsIllegal()
        => Assert.Null(ChecklistMove.Plan(Sample(), "c1", "b1", ChecklistMoveKind.Up));

    [Fact]
    public void Down_OnLastChild_IsIllegal()
        => Assert.Null(ChecklistMove.Plan(Sample(), "c1", "b2", ChecklistMoveKind.Down));

    [Fact]
    public void Indent_OnFirstItemInGroup_IsIllegal()
        => Assert.Null(ChecklistMove.Plan(Sample(), "c1", "a", ChecklistMoveKind.Indent));

    [Fact]
    public void Outdent_OnTopLevelItem_IsIllegal()
        => Assert.Null(ChecklistMove.Plan(Sample(), "c1", "a", ChecklistMoveKind.Outdent));

    [Fact]
    public void Plan_ForMissingItemOrChecklist_IsNull()
    {
        Assert.Null(ChecklistMove.Plan(Sample(), "c1", "nope", ChecklistMoveKind.Up));
        Assert.Null(ChecklistMove.Plan(Sample(), "cX", "a", ChecklistMoveKind.Up));
        Assert.Null(ChecklistMove.Plan(null, "c1", "a", ChecklistMoveKind.Up));
    }

    // ── up / down (parent unchanged) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Up_MovesRootAboveItsPredecessor_ParentUntouched()
    {
        var plan = ChecklistMove.Plan(Sample(), "c1", "b", ChecklistMoveKind.Up);

        Assert.NotNull(plan);
        Assert.Null(plan!.Value.NewParentId);
        Assert.False(plan.Value.ClearParent);           // parent left untouched
        Assert.True(plan.Value.NewOrderIndex < 0);      // below a's orderindex (0)

        // b now sorts before a; its children ride along.
        Assert.Equal(
            [("b", 0), ("b1", 1), ("b2", 1), ("a", 0), ("c", 0)],
            Apply(Sample(), plan.Value));
    }

    [Fact]
    public void Down_MovesRootBelowItsSuccessor()
    {
        var plan = ChecklistMove.Plan(Sample(), "c1", "a", ChecklistMoveKind.Down);

        Assert.NotNull(plan);
        Assert.Null(plan!.Value.NewParentId);
        // a lands between b(1) and c(2): the root order becomes b, a, c.
        Assert.Equal(
            [("b", 0), ("b1", 1), ("b2", 1), ("a", 0), ("c", 0)],
            Apply(Sample(), plan.Value));
    }

    [Fact]
    public void Down_MovesChildPastSibling_WithinTheGroup()
    {
        var plan = ChecklistMove.Plan(Sample(), "c1", "b1", ChecklistMoveKind.Down);

        Assert.NotNull(plan);
        Assert.Null(plan!.Value.NewParentId); // stays under b
        Assert.Equal(
            [("a", 0), ("b", 0), ("b2", 1), ("b1", 1), ("c", 0)],
            Apply(Sample(), plan.Value));
    }

    // ── indent (reparent under preceding sibling) ───────────────────────────────────────────────────

    [Fact]
    public void Indent_ReparentsUnderPrecedingSibling_AsLastChild()
    {
        var plan = ChecklistMove.Plan(Sample(), "c1", "c", ChecklistMoveKind.Indent);

        Assert.NotNull(plan);
        Assert.Equal("b", plan!.Value.NewParentId);
        Assert.False(plan.Value.ClearParent);

        // c becomes b's last child, after b2.
        Assert.Equal(
            [("a", 0), ("b", 0), ("b1", 1), ("b2", 1), ("c", 1)],
            Apply(Sample(), plan.Value));
    }

    // ── outdent (reparent under grandparent) ────────────────────────────────────────────────────────

    [Fact]
    public void Outdent_ToRoot_ClearsParent_AndLandsAfterFormerParent()
    {
        var plan = ChecklistMove.Plan(Sample(), "c1", "b1", ChecklistMoveKind.Outdent);

        Assert.NotNull(plan);
        Assert.Null(plan!.Value.NewParentId);
        Assert.True(plan.Value.ClearParent);            // grandparent is root ⇒ explicit null parent

        // b1 becomes a top-level item just after b; b2 remains b's child.
        Assert.Equal(
            [("a", 0), ("b", 0), ("b2", 1), ("b1", 0), ("c", 0)],
            Apply(Sample(), plan.Value));
    }

    [Fact]
    public void Outdent_ToNonRootGrandparent_SetsGrandparentAsNewParent()
    {
        // p(0) → c(0) → g(0): three levels.
        IReadOnlyList<TaskChecklist> cls =
        [
            List(
                Item("p", 0),
                Item("c", 0, parentId: "p"),
                Item("g", 0, parentId: "c")),
        ];

        var plan = ChecklistMove.Plan(cls, "c1", "g", ChecklistMoveKind.Outdent);

        Assert.NotNull(plan);
        Assert.Equal("p", plan!.Value.NewParentId);     // grandparent, not root
        Assert.False(plan.Value.ClearParent);

        // g rises to be p's child, after c.
        Assert.Equal(
            [("p", 0), ("c", 1), ("g", 1)],
            Apply(cls, plan.Value));
    }

    // ── reparent-under-descendant legality guard ────────────────────────────────────────────────────

    [Fact]
    public void IsLegalReparentTarget_RejectsSelfAndDescendants_AllowsAncestorAndRoot()
    {
        // p → c → g.
        IReadOnlyList<TaskChecklist> cls =
        [
            List(
                Item("p", 0),
                Item("c", 0, parentId: "p"),
                Item("g", 0, parentId: "c")),
        ];

        Assert.False(ChecklistMove.IsLegalReparentTarget(cls, "c1", "p", "p")); // self
        Assert.False(ChecklistMove.IsLegalReparentTarget(cls, "c1", "p", "c")); // direct child
        Assert.False(ChecklistMove.IsLegalReparentTarget(cls, "c1", "p", "g")); // deeper descendant
        Assert.True(ChecklistMove.IsLegalReparentTarget(cls, "c1", "g", "p"));  // ancestor is a fine target
        Assert.True(ChecklistMove.IsLegalReparentTarget(cls, "c1", "c", null)); // to top level
        Assert.False(ChecklistMove.IsLegalReparentTarget(cls, "c1", "c", "nope")); // missing target
        Assert.False(ChecklistMove.IsLegalReparentTarget(cls, "c1", "nope", "p")); // missing item
    }
}
