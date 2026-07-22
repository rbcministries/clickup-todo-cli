using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="WorkspaceMember.DisplayName"/> (#323) — the guaranteed-non-blank,
/// human-friendly name the @-mention picker renders. Verifies the username → email-local-part →
/// <c>User {Id}</c> fallback tiers.
/// </summary>
public sealed class WorkspaceMemberTests
{
    [Fact]
    public void DisplayName_PrefersSpacedUsername()
        => Assert.Equal("Ben Seymour", new WorkspaceMember(42, "Ben Seymour", "ben@example.com").DisplayName);

    [Fact]
    public void DisplayName_TrimsUsername()
        => Assert.Equal("Ben Seymour", new WorkspaceMember(42, "  Ben Seymour  ", null).DisplayName);

    [Fact]
    public void DisplayName_FallsBackToEmailLocalPart_WhenUsernameBlank()
        => Assert.Equal("teammate", new WorkspaceMember(7, null, "teammate@example.com").DisplayName);

    [Fact]
    public void DisplayName_FallsBackToEmailLocalPart_WhenUsernameWhitespace()
        => Assert.Equal("teammate", new WorkspaceMember(7, "   ", "teammate@example.com").DisplayName);

    [Fact]
    public void DisplayName_FallsBackToUserId_WhenNoNameOrEmail()
        => Assert.Equal("User 5", new WorkspaceMember(5, null, null).DisplayName);

    [Fact]
    public void DisplayName_FallsBackToUserId_WhenEmailHasEmptyLocalPart()
        => Assert.Equal("User 8", new WorkspaceMember(8, null, "@nolocal.example").DisplayName);

    [Fact]
    public void DisplayName_IsNotPartOfRecordEquality()
    {
        // Computed property → excluded from record equality, so existing value-equality assertions
        // (e.g. MapMembers tests) keep holding regardless of DisplayName.
        Assert.Equal(new WorkspaceMember(1, "A", null), new WorkspaceMember(1, "A", null));
    }
}
