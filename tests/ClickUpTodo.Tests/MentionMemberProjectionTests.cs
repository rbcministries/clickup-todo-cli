using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure <see cref="MentionMemberProjection"/> (#473): the shared
/// <see cref="TaskAssignee"/> → <see cref="WorkspaceMember"/> mapping the comment composer's mention
/// seams feed into the picker, used by both <c>TodoApp</c> (#325) and <c>SingleTaskApp</c>. The picker
/// renders/keys on <see cref="WorkspaceMember.DisplayName"/>, so the mapping must carry the name as the
/// username (no email) and preserve id, name, and — crucially — the pool's frequency order.
/// </summary>
public sealed class MentionMemberProjectionTests
{
    [Fact]
    public void ToMembers_MapsIdAndNameToDisplayName()
    {
        var members = MentionMemberProjection.ToMembers([new TaskAssignee(101, "Ada Lovelace")]);

        var member = Assert.Single(members);
        Assert.Equal(101, member.Id);
        Assert.Equal("Ada Lovelace", member.Username);
        Assert.Null(member.Email);
        // DisplayName is what the picker shows/keys on — it must fall through to the carried name.
        Assert.Equal("Ada Lovelace", member.DisplayName);
    }

    [Fact]
    public void ToMembers_PreservesOrder()
    {
        // The cache hands back candidates already ranked by frequency; the projection must not reorder.
        var ranked = new[]
        {
            new TaskAssignee(3, "Grace Hopper"),
            new TaskAssignee(1, "Ada Lovelace"),
            new TaskAssignee(2, "Alan Turing"),
        };

        var members = MentionMemberProjection.ToMembers(ranked);

        Assert.Equal([3, 1, 2], members.Select(m => m.Id));
        Assert.Equal(["Grace Hopper", "Ada Lovelace", "Alan Turing"], members.Select(m => m.DisplayName));
    }

    [Fact]
    public void ToMembers_Empty_ReturnsEmpty()
        => Assert.Empty(MentionMemberProjection.ToMembers([]));
}
