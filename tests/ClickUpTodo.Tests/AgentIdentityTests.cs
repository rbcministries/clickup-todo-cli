using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The single "is this id a Super Agent?" predicate (#494) — negative ⇒ agent, zero/positive ⇒ human or
/// system. Pinned here because the whole registry (and #495's picker) keys off it, and it deliberately
/// diverges from the <c>&gt; 0</c> guards human-facing code uses (which drop non-positive ids).
/// </summary>
public sealed class AgentIdentityTests
{
    [Theory]
    [InlineData(-10466700, true)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(542, false)]
    public void IsAgentId_ClassifiesBySign(long id, bool expected)
        => Assert.Equal(expected, AgentIdentity.IsAgentId(id));

    [Fact]
    public void IsAgentId_Nullable_NullIsNeverAgent()
    {
        Assert.False(AgentIdentity.IsAgentId((long?)null));
        Assert.True(AgentIdentity.IsAgentId((long?)-5));
        Assert.False(AgentIdentity.IsAgentId((long?)5));
    }
}
