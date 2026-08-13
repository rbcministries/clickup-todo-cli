using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// The pure merge/validity rules behind the agent registry (#494) — no clock, no store. Covers the
/// seed-wins precedence and the agent-id/non-blank-name ingest filter that keep humans and empty rows out
/// of an agent directory.
/// </summary>
public sealed class AgentDirectoryTests
{
    private static AgentDirectoryEntry Seeded(long id, string name) => new(id, name, null, AgentEntrySource.Seeded);
    private static AgentDirectoryEntry Discovered(long id, string name) => new(id, name, null, AgentEntrySource.Discovered);

    [Theory]
    [InlineData(-5, "Rio", true)]
    [InlineData(-1, "System agent", true)]
    [InlineData(5, "Human", false)]   // positive id is a human, not an agent
    [InlineData(0, "Zero", false)]    // zero is never an agent
    [InlineData(-5, "", false)]       // blank name is dropped
    [InlineData(-5, "   ", false)]    // whitespace name is dropped
    public void IsValid_KeepsOnlyNamedAgents(long id, string name, bool expected)
        => Assert.Equal(expected, AgentDirectory.IsValid(id, name));

    [Fact]
    public void Merge_SeededFirst_ThenDiscovered()
    {
        var merged = AgentDirectory.Merge(
            [Seeded(-1, "Alpha"), Seeded(-2, "Bravo")],
            [Discovered(-3, "Charlie")]);

        Assert.Equal([-1, -2, -3], merged.Select(e => e.Id));
        Assert.Equal(
            [AgentEntrySource.Seeded, AgentEntrySource.Seeded, AgentEntrySource.Discovered],
            merged.Select(e => e.Source));
    }

    [Fact]
    public void Merge_SeedWinsOnIdCollision_DiscoveredDropped()
    {
        var merged = AgentDirectory.Merge(
            [Seeded(-1, "Pinned name")],
            [Discovered(-1, "Discovered name"), Discovered(-2, "Other")]);

        Assert.Equal([-1, -2], merged.Select(e => e.Id));
        var pinned = merged.Single(e => e.Id == -1);
        Assert.Equal("Pinned name", pinned.Name);           // the seed's name, not the discovered one
        Assert.Equal(AgentEntrySource.Seeded, pinned.Source);
    }

    [Fact]
    public void Merge_EmptyLayers_EmptyResult()
        => Assert.Empty(AgentDirectory.Merge([], []));

    [Fact]
    public void Merge_DiscoveredOnly_Preserved()
    {
        var merged = AgentDirectory.Merge([], [Discovered(-3, "Charlie"), Discovered(-4, "Delta")]);
        Assert.Equal([-3, -4], merged.Select(e => e.Id));
    }
}
