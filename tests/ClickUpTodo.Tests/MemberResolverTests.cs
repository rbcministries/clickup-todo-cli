using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="MemberResolver"/> (#323) — the name → <c>userId</c> resolution that the
/// @-mention author path depends on. Covers the picker id path and the exact, case-insensitive,
/// no-fuzzy name match against display name / username / email.
/// </summary>
public sealed class MemberResolverTests
{
    private static readonly IReadOnlyList<WorkspaceMember> Roster =
    [
        new(42, "Ben Seymour", "ben@example.com"),
        new(7, null, "teammate@example.com"),
        new(9, "Sam Doe", null),
    ];

    // ── Picker path: a chosen member resolves to its own id, no matching ──

    [Fact]
    public void ResolveId_Member_ReturnsMemberId()
        => Assert.Equal(42, MemberResolver.ResolveId(new WorkspaceMember(42, "Ben Seymour", null)));

    // ── Name path: exact match on the spaced display name ──

    [Fact]
    public void ResolveId_ByDisplayName_ReturnsId()
        => Assert.Equal(42, MemberResolver.ResolveId(Roster, "Ben Seymour"));

    [Fact]
    public void ResolveId_ByDisplayName_IsCaseInsensitive()
        => Assert.Equal(42, MemberResolver.ResolveId(Roster, "ben seymour"));

    [Fact]
    public void ResolveId_ByDisplayName_TrimsWhitespace()
        => Assert.Equal(9, MemberResolver.ResolveId(Roster, "  Sam Doe  "));

    // ── Name path: username and email are also accepted match keys ──

    [Fact]
    public void ResolveId_ByEmail_ReturnsId()
        => Assert.Equal(42, MemberResolver.ResolveId(Roster, "BEN@example.com"));

    [Fact]
    public void ResolveId_ByEmailLocalPartDisplayName_ReturnsId()
    {
        // Member #7 has no username; its DisplayName falls back to the email local part.
        Assert.Equal(7, MemberResolver.ResolveId(Roster, "teammate"));
    }

    // ── No fragile matching: a near-miss must NOT resolve ──

    [Fact]
    public void ResolveId_PartialName_ReturnsNull()
        => Assert.Null(MemberResolver.ResolveId(Roster, "Ben"));

    [Fact]
    public void ResolveId_Unknown_ReturnsNull()
        => Assert.Null(MemberResolver.ResolveId(Roster, "Nobody Here"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveId_BlankName_ReturnsNull(string? name)
        => Assert.Null(MemberResolver.ResolveId(Roster, name));

    [Fact]
    public void ResolveId_EmptyRoster_ReturnsNull()
        => Assert.Null(MemberResolver.ResolveId([], "Ben Seymour"));

    // ── Determinism: on a duplicate-name workspace the first roster match wins ──

    [Fact]
    public void ResolveId_DuplicateNames_FirstMatchWins()
    {
        IReadOnlyList<WorkspaceMember> dupes =
        [
            new(100, "Alex Kim", null),
            new(200, "Alex Kim", null),
        ];
        Assert.Equal(100, MemberResolver.ResolveId(dupes, "Alex Kim"));
    }

    // ── A member with a zero id (unresolvable) is never returned ──

    [Fact]
    public void ResolveId_ZeroIdMember_IsSkipped()
    {
        IReadOnlyList<WorkspaceMember> roster = [new(0, "Ghost User", null)];
        Assert.Null(MemberResolver.ResolveId(roster, "Ghost User"));
    }
}
