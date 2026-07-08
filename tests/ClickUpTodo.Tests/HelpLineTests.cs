using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

public sealed class HelpLineTests
{
    [Fact]
    public void Format_JoinsKeyAndLabelWithMiddotSeparator()
    {
        var items = new HelpItem[] { new("Esc", "back"), new("F1", "help") };

        Assert.Equal("Esc back · F1 help", HelpLine.Format(items));
    }

    [Fact]
    public void Format_EmptySet_IsEmptyString()
        => Assert.Equal("", HelpLine.Format([]));

    [Fact]
    public void Format_SingleItem_HasNoSeparator()
        => Assert.Equal("Esc/Enter close", HelpLine.Format(HelpItemSets.Help));

    // The main-list footer text must stay byte-for-byte what it was before #103, so the default
    // (list-active) footer — and the tui-validate baseline — is unchanged.
    [Fact]
    public void Format_MainList_ReproducesThePreExistingHelpLine()
    {
        const string expected =
            "↑/↓ move · →| next section · ␣ status · ↩ detail · Ctrl+B 🌐 · Ctrl+P 📌 · Ctrl+R ↻ · "
            + "F1 help · F2 ⚙ · F3 filter/sort/group · F4 subtasks · →/← expand/collapse · Ctrl+Q quit · "
            + "type to search";

        Assert.Equal(expected, HelpLine.Format(HelpItemSets.MainList));
    }

    [Fact]
    public void ForActiveScreen_PrefersScreenItems_WhenPresent()
    {
        var result = HelpLine.ForActiveScreen(HelpItemSets.Detail, HelpItemSets.MainList);

        Assert.Same(HelpItemSets.Detail, result);
    }

    [Fact]
    public void ForActiveScreen_FallsBackToList_WhenNoScreen()
    {
        var result = HelpLine.ForActiveScreen(null, HelpItemSets.MainList);

        Assert.Same(HelpItemSets.MainList, result);
    }

    [Fact]
    public void ForActiveScreen_FallsBackToList_WhenScreenSetIsEmpty()
    {
        var result = HelpLine.ForActiveScreen([], HelpItemSets.MainList);

        Assert.Same(HelpItemSets.MainList, result);
    }

    public static readonly TheoryData<IReadOnlyList<HelpItem>> AllSets = new()
    {
        HelpItemSets.MainList,
        HelpItemSets.Detail,
        HelpItemSets.Settings,
        HelpItemSets.FilterSortGroup,
        HelpItemSets.StatusPicker,
        HelpItemSets.Help,
    };

    [Theory]
    [MemberData(nameof(AllSets))]
    public void EverySet_IsNonEmpty(IReadOnlyList<HelpItem> set)
        => Assert.NotEmpty(set);

    // The screens (not the list, not Help itself) each offer F1 → Help so the master shortcut list is
    // one keypress away everywhere (#103), and each ends with an Esc/close affordance.
    public static readonly TheoryData<IReadOnlyList<HelpItem>> ScreenSets = new()
    {
        HelpItemSets.Detail,
        HelpItemSets.Settings,
        HelpItemSets.FilterSortGroup,
        HelpItemSets.StatusPicker,
    };

    [Theory]
    [MemberData(nameof(ScreenSets))]
    public void EveryScreenSet_OffersF1Help(IReadOnlyList<HelpItem> set)
        => Assert.Contains(set, i => i.Key == "F1" && i.Label == "help");

    [Theory]
    [MemberData(nameof(ScreenSets))]
    public void EveryScreenSet_EndsWithAnEscItem(IReadOnlyList<HelpItem> set)
        => Assert.StartsWith("Esc", set[^1].Key);

    [Fact]
    public void HelpSet_DoesNotOfferF1_SinceItIsTheHelp()
        => Assert.DoesNotContain(HelpItemSets.Help, i => i.Key == "F1");
}
