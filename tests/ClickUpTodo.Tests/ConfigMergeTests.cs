using System.Text.Json.Nodes;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure three-way merge for the settings document (#293) — <see cref="ConfigMerge"/>. Each top-level
/// property is one field: a field this process changed since it loaded wins; an untouched field defers
/// to the freshly-read on-disk value (which may carry another tab's change). The stateful glue (baseline
/// tracking, re-read, persist) is covered by <see cref="ConfigStoreTests"/>.
/// </summary>
public sealed class ConfigMergeTests
{
    private static JsonObject Merge(string baseline, string current, string onDisk)
        => JsonNode.Parse(ConfigMerge.ThreeWay(baseline, current, onDisk))!.AsObject();

    [Fact]
    public void ChangedHereWins_UntouchedDefersToDisk()
    {
        // This process changed "badge"; the other tab changed "refresh". Both survive.
        var baseline = """{"refresh":60,"badge":"Icons"}""";
        var current = """{"refresh":60,"badge":"Text"}""";
        var onDisk = """{"refresh":30,"badge":"Icons"}""";

        var merged = Merge(baseline, current, onDisk);

        Assert.Equal(30, merged["refresh"]!.GetValue<int>());       // untouched here → disk value kept
        Assert.Equal("Text", merged["badge"]!.GetValue<string>());  // changed here → our value wins
    }

    [Fact]
    public void BothChangedSameField_CurrentWins()
    {
        // Genuine last-writer-wins: this process explicitly set 45, so it overrides the disk's 30.
        var merged = Merge("""{"refresh":60}""", """{"refresh":45}""", """{"refresh":30}""");

        Assert.Equal(45, merged["refresh"]!.GetValue<int>());
    }

    [Fact]
    public void NestedObjectAndArray_MergeAsAWholeFieldUnit()
    {
        // We changed the nested "view"; the other tab changed the "pins" array. Each is one field.
        var baseline = """{"view":{"sort":"name"},"pins":["a"]}""";
        var current = """{"view":{"sort":"created"},"pins":["a"]}""";
        var onDisk = """{"view":{"sort":"name"},"pins":["a","b"]}""";

        var merged = Merge(baseline, current, onDisk);

        Assert.Equal("created", merged["view"]!["sort"]!.GetValue<string>()); // our nested change wins
        Assert.Equal(["a", "b"], merged["pins"]!.AsArray().Select(n => n!.GetValue<string>())); // disk's array kept
    }

    [Fact]
    public void KeyOnlyInCurrent_IsKept()
    {
        // A field this process has but disk lacks (differs from an absent baseline ⇒ changed here).
        var merged = Merge("""{}""", """{"newField":"x"}""", """{}""");

        Assert.Equal("x", merged["newField"]!.GetValue<string>());
    }

    [Fact]
    public void KeyOnlyOnDisk_IsPreserved()
    {
        // A field only the other tab has (absent here and in baseline ⇒ not changed here) survives.
        var merged = Merge("""{}""", """{}""", """{"otherField":"y"}""");

        Assert.Equal("y", merged["otherField"]!.GetValue<string>());
    }
}
