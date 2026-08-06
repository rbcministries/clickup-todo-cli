using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

public sealed class ListMembershipApplyPlannerTests
{
    private static NamedEntity List(string id, string name = "A list") => new(id, name);

    // ── Add is always safe ───────────────────────────────────────────────────────

    [Fact]
    public void Add_AlwaysWrites_NoMessage()
    {
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Added, List("l2"), homeListId: "home", strandedFieldNames: [], armed: false);

        Assert.Equal(ListApplyAction.WriteAdd, d.Action);
        Assert.Null(d.Message);
    }

    [Fact]
    public void Add_IsSafe_EvenWhenStrandedNamesSuppliedOrArmed()
    {
        // An add never consults the strand set (adding only exposes fields, never hides them).
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Added, List("l2"), homeListId: "home", strandedFieldNames: ["Notes"], armed: true);

        Assert.Equal(ListApplyAction.WriteAdd, d.Action);
    }

    // ── Home-list remove is blocked ──────────────────────────────────────────────

    [Fact]
    public void Remove_OfHomeList_IsBlocked_WithMessage()
    {
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Removed, List("home"), homeListId: "home", strandedFieldNames: [], armed: false);

        Assert.Equal(ListApplyAction.BlockHomeRemove, d.Action);
        Assert.Equal(ListMembershipApplyPlanner.HomeRemoveMessage, d.Message);
    }

    [Fact]
    public void Remove_OfHomeList_IsBlocked_EvenWhenArmedOrStranding()
    {
        // The home guard wins over the arm/strand branches — a home remove must never write.
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Removed, List("home"), homeListId: "home", strandedFieldNames: ["Notes"], armed: true);

        Assert.Equal(ListApplyAction.BlockHomeRemove, d.Action);
    }

    [Fact]
    public void Remove_HomeMatch_IsOrdinal_CaseSensitive()
    {
        // Ids are compared ordinally (ClickUp list ids are case-sensitive opaque strings), so a
        // case-different id is a different, removable, additional list — not the home list.
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Removed, List("HOME"), homeListId: "home", strandedFieldNames: [], armed: false);

        Assert.Equal(ListApplyAction.WriteRemove, d.Action);
    }

    // ── Additional-list remove: strand-free proceeds; stranding arms then confirms ─

    [Fact]
    public void Remove_Additional_StrandsNothing_WritesSilently()
    {
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Removed, List("l2"), homeListId: "home", strandedFieldNames: [], armed: false);

        Assert.Equal(ListApplyAction.WriteRemove, d.Action);
        Assert.Null(d.Message);
    }

    [Fact]
    public void Remove_Additional_WouldStrand_FirstTime_ArmsAndWarns()
    {
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Removed, List("l2", "Q3 Website Refresh"),
            homeListId: "home", strandedFieldNames: ["Estimate", "Stage"], armed: false);

        Assert.Equal(ListApplyAction.ArmRemoveConfirmation, d.Action);
        Assert.NotNull(d.Message);
        // The warning names the list and every stranded field so nothing is hidden silently.
        Assert.Contains("Q3 Website Refresh", d.Message);
        Assert.Contains("Estimate", d.Message);
        Assert.Contains("Stage", d.Message);
        Assert.Contains("Press remove again", d.Message);
    }

    [Fact]
    public void Remove_Additional_WouldStrand_WhenArmed_Writes()
    {
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Removed, List("l2"), homeListId: "home",
            strandedFieldNames: ["Estimate"], armed: true);

        Assert.Equal(ListApplyAction.WriteRemove, d.Action);
        Assert.Null(d.Message);
    }

    [Fact]
    public void Remove_Additional_NoHomeKnown_StillArmsOnStrand()
    {
        // A blank/unknown home id can't match, so the remove falls through to the strand branch.
        var d = ListMembershipApplyPlanner.Plan(
            ToggleKind.Removed, List("l2"), homeListId: null, strandedFieldNames: ["Notes"], armed: false);

        Assert.Equal(ListApplyAction.ArmRemoveConfirmation, d.Action);
    }

    [Fact]
    public void StrandWarning_JoinsFieldNames()
        => Assert.Equal(
            "Removing 'My List' hides these Custom Field values: A, B, C. Press remove again to confirm.",
            ListMembershipApplyPlanner.StrandWarning("My List", ["A", "B", "C"]));
}
