using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The pure half of the Checklists tab (C, #456): row display text, tab title, empty-state line, and the
/// refresh-safe <see cref="ChecklistTabModel.Signature"/> / <see cref="ChecklistTabModel.AnchorSelection"/>
/// the glue leans on. All Terminal.Gui-free, so it's unit-tested here rather than through the PTY.
/// </summary>
public class ChecklistTabModelTests
{
    private static ChecklistRow Header(string checklistId, string name, int resolved, int total)
        => new(ChecklistRowKind.Header, 0, name, checklistId, null, false, resolved, total, null);

    private static ChecklistRow Item(
        string checklistId, string itemId, string name, bool resolved, int depth = 0, string? assignee = null)
        => new(ChecklistRowKind.Item, depth, name, checklistId, itemId, resolved, 0, 0, assignee);

    // ── TabTitle ──────────────────────────────────────────────────────────────

    [Fact]
    public void TabTitle_ShowsAggregateProgress()
        => Assert.Equal("Checklists (5/12)", ChecklistTabModel.TabTitle(new ChecklistProjection([], 2, 5, 12)));

    [Fact]
    public void TabTitle_NoItems_IsBare()
        => Assert.Equal("Checklists", ChecklistTabModel.TabTitle(new ChecklistProjection([], 1, 0, 0)));

    [Fact]
    public void TabTitle_Empty_IsBare()
        => Assert.Equal("Checklists", ChecklistTabModel.TabTitle(ChecklistProjection.Empty));

    // ── RenderRow ─────────────────────────────────────────────────────────────

    [Fact]
    public void RenderRow_Header_CarriesProgress()
        => Assert.Equal("Release  (2/3)", ChecklistTabModel.RenderRow(Header("c1", "Release", 2, 3)));

    [Fact]
    public void RenderRow_UnresolvedItem_UsesOpenBox()
        => Assert.Equal("[ ] Write the changelog", ChecklistTabModel.RenderRow(Item("c1", "i1", "Write the changelog", resolved: false)));

    [Fact]
    public void RenderRow_ResolvedItem_UsesTickedBox()
        => Assert.Equal("[x] Cut the tag", ChecklistTabModel.RenderRow(Item("c1", "i1", "Cut the tag", resolved: true)));

    [Fact]
    public void RenderRow_NestedItem_IndentsTwoSpacesPerLevel()
        => Assert.Equal("    [ ] Nested twice", ChecklistTabModel.RenderRow(Item("c1", "i1", "Nested twice", resolved: false, depth: 2)));

    [Fact]
    public void RenderRow_ItemWithAssignee_AppendsSuffix()
        => Assert.Equal("[ ] Review — Ada Lovelace", ChecklistTabModel.RenderRow(Item("c1", "i1", "Review", resolved: false, assignee: "Ada Lovelace")));

    [Fact]
    public void RenderRow_ItemWithBlankAssignee_HasNoSuffix()
        => Assert.Equal("[x] Ship", ChecklistTabModel.RenderRow(Item("c1", "i1", "Ship", resolved: true, assignee: "   ")));

    // ── Signature ─────────────────────────────────────────────────────────────

    [Fact]
    public void Signature_Empty_IsStable()
        => Assert.Equal(ChecklistTabModel.Signature(ChecklistProjection.Empty), ChecklistTabModel.Signature(ChecklistProjection.Empty));

    [Fact]
    public void Signature_UnchangedProjection_IsEqual()
    {
        var a = new ChecklistProjection([Header("c1", "Rel", 1, 2), Item("c1", "i1", "A", true), Item("c1", "i2", "B", false)], 1, 1, 2);
        var b = new ChecklistProjection([Header("c1", "Rel", 1, 2), Item("c1", "i1", "A", true), Item("c1", "i2", "B", false)], 1, 1, 2);
        Assert.Equal(ChecklistTabModel.Signature(a), ChecklistTabModel.Signature(b));
    }

    [Fact]
    public void Signature_ResolvedFlip_Differs()
    {
        var before = new ChecklistProjection([Item("c1", "i1", "A", resolved: false)], 1, 0, 1);
        var after = new ChecklistProjection([Item("c1", "i1", "A", resolved: true)], 1, 1, 1);
        Assert.NotEqual(ChecklistTabModel.Signature(before), ChecklistTabModel.Signature(after));
    }

    [Fact]
    public void Signature_RenamedItem_Differs()
    {
        var before = new ChecklistProjection([Item("c1", "i1", "A", false)], 1, 0, 1);
        var after = new ChecklistProjection([Item("c1", "i1", "A renamed", false)], 1, 0, 1);
        Assert.NotEqual(ChecklistTabModel.Signature(before), ChecklistTabModel.Signature(after));
    }

    [Fact]
    public void Signature_AssigneeChange_Differs()
    {
        var before = new ChecklistProjection([Item("c1", "i1", "A", false)], 1, 0, 1);
        var after = new ChecklistProjection([Item("c1", "i1", "A", false, assignee: "Ada")], 1, 0, 1);
        Assert.NotEqual(ChecklistTabModel.Signature(before), ChecklistTabModel.Signature(after));
    }

    [Fact]
    public void Signature_NoFieldBoundaryForgery()
    {
        // Two rows whose id/text would concatenate identically without a real delimiter must still differ.
        var a = new ChecklistProjection([Item("c1", "ab", "c", false)], 1, 0, 1);
        var b = new ChecklistProjection([Item("c1", "a", "bc", false)], 1, 0, 1);
        Assert.NotEqual(ChecklistTabModel.Signature(a), ChecklistTabModel.Signature(b));
    }

    // ── AnchorSelection ───────────────────────────────────────────────────────

    [Fact]
    public void AnchorSelection_KeepsCursorOnSameItemWhenRowsShift()
    {
        var oldRows = new List<ChecklistRow> { Header("c1", "Rel", 1, 2), Item("c1", "i1", "A", true), Item("c1", "i2", "B", false) };
        // A new checklist was inserted first, pushing i2 down two rows.
        var newRows = new List<ChecklistRow> { Header("c0", "New", 0, 1), Item("c0", "iX", "X", false), Header("c1", "Rel", 1, 2), Item("c1", "i1", "A", true), Item("c1", "i2", "B", false) };
        Assert.Equal(4, ChecklistTabModel.AnchorSelection(oldRows, oldIndex: 2, newRows)); // i2 followed
    }

    [Fact]
    public void AnchorSelection_HeaderAnchorsByChecklist()
    {
        var oldRows = new List<ChecklistRow> { Header("c1", "Rel", 1, 2), Header("c2", "QA", 0, 1) };
        var newRows = new List<ChecklistRow> { Header("c2", "QA", 0, 1), Header("c1", "Rel", 2, 2) };
        Assert.Equal(1, ChecklistTabModel.AnchorSelection(oldRows, oldIndex: 0, newRows)); // c1 header followed
    }

    [Fact]
    public void AnchorSelection_DeletedItem_ClampsOldIndex()
    {
        var oldRows = new List<ChecklistRow> { Header("c1", "Rel", 1, 2), Item("c1", "i1", "A", true), Item("c1", "i2", "B", false) };
        var newRows = new List<ChecklistRow> { Header("c1", "Rel", 1, 1), Item("c1", "i1", "A", true) };
        Assert.Equal(1, ChecklistTabModel.AnchorSelection(oldRows, oldIndex: 2, newRows)); // i2 gone → clamp to last
    }

    [Fact]
    public void AnchorSelection_NoNewRows_ReturnsZero()
        => Assert.Equal(0, ChecklistTabModel.AnchorSelection([Item("c1", "i1", "A", false)], oldIndex: 0, []));

    [Fact]
    public void AnchorSelection_OutOfRangeOldIndex_ClampsIntoNewRange()
    {
        var newRows = new List<ChecklistRow> { Item("c1", "i1", "A", false), Item("c1", "i2", "B", false) };
        Assert.Equal(1, ChecklistTabModel.AnchorSelection([], oldIndex: 9, newRows));
    }

    // ── SelectAfterDelete (E, #458) ───────────────────────────────────────────

    [Fact]
    public void SelectAfterDelete_PrefersTheNextSiblingInTheSameChecklist()
    {
        var oldRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 1, 3), Item("c1", "i1", "A", false), Item("c1", "i2", "B", false), Item("c1", "i3", "C", false),
        };
        // i2 deleted → i3 remains and is the next sibling.
        var newRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 1, 2), Item("c1", "i1", "A", false), Item("c1", "i3", "C", false),
        };
        Assert.Equal(2, ChecklistTabModel.SelectAfterDelete(oldRows, deletedIndex: 2, newRows)); // lands on i3
    }

    [Fact]
    public void SelectAfterDelete_FallsBackToPreviousSibling_WhenDeletingTheLast()
    {
        var oldRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 1, 2), Item("c1", "i1", "A", false), Item("c1", "i2", "B", false),
        };
        // i2 (the last item) deleted → previous item i1.
        var newRows = new List<ChecklistRow> { Header("c1", "Rel", 1, 1), Item("c1", "i1", "A", false) };
        Assert.Equal(1, ChecklistTabModel.SelectAfterDelete(oldRows, deletedIndex: 2, newRows)); // lands on i1
    }

    [Fact]
    public void SelectAfterDelete_SkipsTheDeletedSubtree_WhenChoosingTheNextRow()
    {
        var oldRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 0, 3),
            Item("c1", "i1", "Parent", false, depth: 0),
            Item("c1", "i1a", "Child", false, depth: 1),
            Item("c1", "i2", "Next", false, depth: 0),
        };
        // Deleting the parent i1 removes its child i1a too; the next row is i2.
        var newRows = new List<ChecklistRow> { Header("c1", "Rel", 0, 1), Item("c1", "i2", "Next", false) };
        Assert.Equal(1, ChecklistTabModel.SelectAfterDelete(oldRows, deletedIndex: 1, newRows)); // lands on i2, not into the gone subtree
    }

    [Fact]
    public void SelectAfterDelete_DeletingTheOnlyItem_LandsOnTheHeader()
    {
        var oldRows = new List<ChecklistRow> { Header("c1", "Rel", 0, 1), Item("c1", "i1", "A", false) };
        var newRows = new List<ChecklistRow> { Header("c1", "Rel", 0, 0) };
        Assert.Equal(0, ChecklistTabModel.SelectAfterDelete(oldRows, deletedIndex: 1, newRows)); // the header
    }

    [Fact]
    public void SelectAfterDelete_DoesNotJumpToAnotherChecklistsItem()
    {
        var oldRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 0, 1), Item("c1", "i1", "A", false),
            Header("c2", "QA", 0, 1), Item("c2", "j1", "X", false),
        };
        // Deleting c1's only item must land on c1's header, never c2's item.
        var newRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 0, 0), Header("c2", "QA", 0, 1), Item("c2", "j1", "X", false),
        };
        Assert.Equal(0, ChecklistTabModel.SelectAfterDelete(oldRows, deletedIndex: 1, newRows)); // c1 header, not j1
    }

    [Fact]
    public void SelectAfterDelete_NoNewRows_ReturnsZero()
        => Assert.Equal(0, ChecklistTabModel.SelectAfterDelete([Item("c1", "i1", "A", false)], deletedIndex: 0, []));

    // ── SelectAfterGroupDelete (F, #459) ──────────────────────────────────────

    [Fact]
    public void SelectAfterGroupDelete_PrefersTheNextGroupHeader()
    {
        var oldRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 0, 1), Item("c1", "i1", "A", false),
            Header("c2", "QA", 0, 1), Item("c2", "j1", "X", false),
            Header("c3", "Docs", 0, 0),
        };
        // Deleting c1 (header + its item) → the next group header c2 remains.
        var newRows = new List<ChecklistRow>
        {
            Header("c2", "QA", 0, 1), Item("c2", "j1", "X", false), Header("c3", "Docs", 0, 0),
        };
        Assert.Equal(0, ChecklistTabModel.SelectAfterGroupDelete(oldRows, deletedHeaderIndex: 0, newRows)); // c2 header
    }

    [Fact]
    public void SelectAfterGroupDelete_FallsBackToPreviousGroupHeader_WhenDeletingTheLast()
    {
        var oldRows = new List<ChecklistRow>
        {
            Header("c1", "Rel", 0, 1), Item("c1", "i1", "A", false),
            Header("c2", "QA", 0, 1), Item("c2", "j1", "X", false),
        };
        // Deleting the last group c2 → previous header c1.
        var newRows = new List<ChecklistRow> { Header("c1", "Rel", 0, 1), Item("c1", "i1", "A", false) };
        Assert.Equal(0, ChecklistTabModel.SelectAfterGroupDelete(oldRows, deletedHeaderIndex: 2, newRows)); // c1 header
    }

    [Fact]
    public void SelectAfterGroupDelete_DeletingTheOnlyGroup_LandsOnTheEmptyState()
    {
        var oldRows = new List<ChecklistRow> { Header("c1", "Rel", 0, 1), Item("c1", "i1", "A", false) };
        // The list is now empty → the glue renders a single empty-state row at index 0.
        var newRows = new List<ChecklistRow> { Item("empty", "", "No checklists on this task.", false) };
        Assert.Equal(0, ChecklistTabModel.SelectAfterGroupDelete(oldRows, deletedHeaderIndex: 0, newRows));
    }

    [Fact]
    public void SelectAfterGroupDelete_NoNewRows_ReturnsZero()
        => Assert.Equal(0, ChecklistTabModel.SelectAfterGroupDelete([Header("c1", "Rel", 0, 0)], deletedHeaderIndex: 0, []));

    [Fact]
    public void SelectAfterGroupDelete_OutOfRange_ClampsIntoNewRange()
    {
        var newRows = new List<ChecklistRow> { Header("c1", "Rel", 0, 0), Header("c2", "QA", 0, 0) };
        Assert.Equal(1, ChecklistTabModel.SelectAfterGroupDelete([], deletedHeaderIndex: 9, newRows));
    }

    // ── DeleteGroupPrompt (F, #459) ───────────────────────────────────────────

    [Fact]
    public void DeleteGroupPrompt_Plural_NamesTheGroupAndCount()
        => Assert.Equal("Delete checklist 'Release steps' and its 3 items? (Enter / Esc)",
            ChecklistTabModel.DeleteGroupPrompt("Release steps", 3));

    [Fact]
    public void DeleteGroupPrompt_Singular_UsesItem()
        => Assert.Equal("Delete checklist 'QA' and its 1 item? (Enter / Esc)",
            ChecklistTabModel.DeleteGroupPrompt("QA", 1));

    [Fact]
    public void DeleteGroupPrompt_Empty_OmitsTheItemClause()
        => Assert.Equal("Delete checklist 'Docs'? (Enter / Esc)",
            ChecklistTabModel.DeleteGroupPrompt("Docs", 0));

    // ── DeleteGroupMessage (F, #543 — the native ConfirmDialog wording) ────────

    [Fact]
    public void DeleteGroupMessage_Plural_NamesTheGroupAndCount_WithNoKeyHints()
        => Assert.Equal("Delete checklist 'Release steps' and its 3 items?\nThis can't be undone.",
            ChecklistTabModel.DeleteGroupMessage("Release steps", 3));

    [Fact]
    public void DeleteGroupMessage_Singular_UsesItem()
        => Assert.Equal("Delete checklist 'QA' and its 1 item?\nThis can't be undone.",
            ChecklistTabModel.DeleteGroupMessage("QA", 1));

    [Fact]
    public void DeleteGroupMessage_Empty_OmitsTheItemClause()
        => Assert.Equal("Delete checklist 'Docs'?\nThis can't be undone.",
            ChecklistTabModel.DeleteGroupMessage("Docs", 0));

    // The native ConfirmDialog carries its own buttons, so unlike DeleteGroupPrompt the message must not
    // append the inline "(Enter / Esc)" answer hint (which would read as a second, contradictory affordance).
    [Fact]
    public void DeleteGroupMessage_HasNoInlineKeyHints()
        => Assert.DoesNotContain("Enter / Esc", ChecklistTabModel.DeleteGroupMessage("Release steps", 3));
}
