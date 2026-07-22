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

    // --- pinnedTaskIds element-level set-union (#335) -------------------------------------------
    // Unlike every other field, pinnedTaskIds three-way set-merges its elements: additions on either
    // side (vs. baseline) are unioned; a genuine unpin (baseline element removed on a side) is honored
    // and not resurrected by the union.

    private static string[] Pins(JsonObject merged)
        => merged["pinnedTaskIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public void PinnedTaskIds_TwoTabsPinDifferentTasks_BothSurvive()
    {
        // baseline empty; this tab pinned X, the other tab pinned Y (already on disk). Both must stick.
        var merged = Merge(
            """{"pinnedTaskIds":[]}""",
            """{"pinnedTaskIds":["X"]}""",
            """{"pinnedTaskIds":["Y"]}""");

        Assert.Equal(["X", "Y"], Pins(merged)); // current's order first, then disk-only additions
    }

    [Fact]
    public void PinnedTaskIds_ThisSideUnpins_OtherIdle_UnpinSticks()
    {
        // baseline had Z; this tab unpinned it; the other tab left it. The unpin must win, not resurrect.
        var merged = Merge(
            """{"pinnedTaskIds":["Z"]}""",
            """{"pinnedTaskIds":[]}""",
            """{"pinnedTaskIds":["Z"]}""");

        Assert.Empty(Pins(merged));
    }

    [Fact]
    public void PinnedTaskIds_DiskSideUnpins_ThisIdle_UnpinSticks()
    {
        // Symmetric: the other tab unpinned Z (gone from disk); this tab didn't touch it. Stays unpinned.
        var merged = Merge(
            """{"pinnedTaskIds":["Z"]}""",
            """{"pinnedTaskIds":["Z"]}""",
            """{"pinnedTaskIds":[]}""");

        Assert.Empty(Pins(merged));
    }

    [Fact]
    public void PinnedTaskIds_ConcurrentAddAndUnpinOfDifferentIds_BothHonored()
    {
        // baseline [A]; this tab adds B (keeping A); the other tab unpins A. Result: A gone, B added.
        var merged = Merge(
            """{"pinnedTaskIds":["A"]}""",
            """{"pinnedTaskIds":["A","B"]}""",
            """{"pinnedTaskIds":[]}""");

        Assert.Equal(["B"], Pins(merged));
    }

    [Fact]
    public void PinnedTaskIds_NoConcurrentChange_IsIdempotent()
    {
        // Re-reading and merging our own just-written document changes nothing.
        var merged = Merge(
            """{"pinnedTaskIds":["A","B"]}""",
            """{"pinnedTaskIds":["A","B"]}""",
            """{"pinnedTaskIds":["A","B"]}""");

        Assert.Equal(["A", "B"], Pins(merged));
    }

    [Fact]
    public void PinnedTaskIds_PreservesCurrentOrder_ThenAppendsDiskOnlyAdditions()
    {
        // baseline [A]; this tab reorders to [B, A] and adds C; the other tab adds D. Current order
        // is kept for its own ids, disk-only additions (D) append last.
        var merged = Merge(
            """{"pinnedTaskIds":["A"]}""",
            """{"pinnedTaskIds":["B","A","C"]}""",
            """{"pinnedTaskIds":["A","D"]}""");

        Assert.Equal(["B", "A", "C", "D"], Pins(merged));
    }

    [Fact]
    public void PinnedTaskIds_UnionDoesNotAffectOtherFieldsLwwBehaviour()
    {
        // pinnedTaskIds unions; a sibling array (some other field) still merges whole-field LWW.
        var baseline = """{"pinnedTaskIds":[],"other":["a"]}""";
        var current = """{"pinnedTaskIds":["X"],"other":["a"]}""";
        var onDisk = """{"pinnedTaskIds":["Y"],"other":["a","b"]}""";

        var merged = Merge(baseline, current, onDisk);

        Assert.Equal(["X", "Y"], Pins(merged));                                              // unioned
        Assert.Equal(["a", "b"], merged["other"]!.AsArray().Select(n => n!.GetValue<string>())); // LWW → disk
    }

    [Fact]
    public void PinnedTaskIds_MissingOnASide_TreatedAsEmptySet()
    {
        // The key is absent on disk (a legacy doc). This tab's pin is an addition and survives.
        var merged = Merge(
            """{"pinnedTaskIds":[]}""",
            """{"pinnedTaskIds":["X"]}""",
            """{}""");

        Assert.Equal(["X"], Pins(merged));
    }
}
