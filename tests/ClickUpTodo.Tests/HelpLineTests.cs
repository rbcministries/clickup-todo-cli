using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Text;

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

    // Pins the full main-list footer. F5 is the refresh key (icon ↻; Ctrl+R is its undisplayed alias)
    // and Ctrl+E opens the feed — the List ↔ Feed navigation key.
    [Fact]
    public void Format_MainList_RendersTheFullFooter()
    {
        const string expected =
            "↑/↓ move · →| next section · ␣ status · ↩ detail · Ctrl+N new task · Ctrl+B 🌐 · Ctrl+P 📌 · Ctrl+E feed · "
            + "F1 help · F2 ⚙ · F3 filter/sort/group · F4 subtasks · F5 ↻ · F6 badges · F12 completed · "
            + "→/← expand/collapse · Ctrl+→/← all · Ctrl+Q quit · type to search";

        Assert.Equal(expected, HelpLine.Format(HelpItemSets.MainList));
    }

    [Fact]
    public void MainList_CarriesCtrlNNewTask()
        => Assert.Contains(new HelpItem("Ctrl+N", "new task"), HelpItemSets.MainList);

    [Fact]
    public void Format_NewTask_RendersMoveSaveCancelHelp()
        => Assert.Equal(
            "Tab Name/Descr/Assignees/List · Enter/Save saves · Esc cancels · F1 help",
            HelpLine.Format(HelpItemSets.NewTask));

    [Fact]
    public void Format_NotificationsFeed_RendersMoveMentionsHelpAndBack()
        => Assert.Equal(
            "↑/↓ move · Enter open · F3 mentions only · F5 ↻ · F6 activity · F12 completed · Ctrl+E list · F1 help · Esc back",
            HelpLine.Format(HelpItemSets.NotificationsFeed));

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
        HelpItemSets.QuickUpdates,
        HelpItemSets.NotificationsFeed,
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
        HelpItemSets.QuickUpdates,
        HelpItemSets.NotificationsFeed,
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

    // ── #H2 / #104: responsive width fitting ─────────────────────────────────────────────────

    /// <summary>Column-aware measure matching the host (Terminal.Gui's grapheme-aware GetColumns).</summary>
    private static int Cols(string s) => s.GetColumns();

    /// <summary>Naïve char-count measure — the wrong one; kept only to contrast against <see cref="Cols"/>.</summary>
    private static int Chars(string s) => s.Length;

    [Fact]
    public void Fit_ReturnsSetUnchanged_WhenEverythingFits()
    {
        // Far wider than the 205-column full main-list footer.
        var result = HelpLine.Fit(HelpItemSets.MainList, width: 1000, Cols);

        Assert.Same(HelpItemSets.MainList, result);
    }

    [Fact]
    public void Fit_EmptySet_ReturnsEmpty()
        => Assert.Empty(HelpLine.Fit([], width: 5, Cols));

    [Fact]
    public void Fit_Truncates_KeepingLeadingPrefixThenFallbackLast()
    {
        // At 70 columns the main-list footer fits its first four items plus the reserved fallback
        // ("↑/↓ move · →| next section · ␣ status · ↩ detail · F1 Help + Shortcuts" = 70 cols).
        var result = HelpLine.Fit(HelpItemSets.MainList, width: 70, Cols);

        Assert.Equal(HelpLine.HelpFallback, result[^1]);
        Assert.Equal(HelpItemSets.MainList.Take(4), result.Take(result.Count - 1));
        Assert.True(Cols(HelpLine.Format(result)) <= 70);
    }

    [Fact]
    public void Fit_ShowsMoreItems_AsWidthGrows()
    {
        var narrow = HelpLine.Fit(HelpItemSets.MainList, width: 70, Cols);
        var wide = HelpLine.Fit(HelpItemSets.MainList, width: 94, Cols);

        // Both end with the fallback (still truncating), and the wider line carries strictly more.
        Assert.Equal(HelpLine.HelpFallback, narrow[^1]);
        Assert.Equal(HelpLine.HelpFallback, wide[^1]);
        Assert.True(wide.Count > narrow.Count);
        Assert.True(Cols(HelpLine.Format(wide)) <= 94);
    }

    [Fact]
    public void Fit_VeryNarrow_ShowsOnlyTheFallback()
    {
        // Narrower than the 19-column fallback itself: it's still returned (and clipped by the host).
        var result = HelpLine.Fit(HelpItemSets.MainList, width: 10, Cols);

        HelpItem[] onlyFallback = [HelpLine.HelpFallback];
        Assert.Equal(onlyFallback, result);
    }

    [Fact]
    public void Fit_NeverDuplicatesF1_TheFallbackSubsumesTheScreenF1Item()
    {
        // 120 columns truncates past the main list's own "F1 help" item (index 7), which must be
        // dropped so the trailing "F1 Help + Shortcuts" fallback is the only F1 on the line.
        var result = HelpLine.Fit(HelpItemSets.MainList, width: 120, Cols);

        Assert.Equal(HelpLine.HelpFallback, result[^1]);
        Assert.Single(result, i => i.Key == "F1");
    }

    [Fact]
    public void Fit_UsesColumnWidth_NotCharCount_ForWideGlyphs()
    {
        // "中" is a wide glyph: 2 display columns but a single UTF-16 char. This footer renders as
        // "中中 中中" — 9 columns but only 5 chars — so a char-count measure and a column measure
        // disagree at width 6, and the fit must follow the column measure.
        IReadOnlyList<HelpItem> items = [new("中中", "中中")];

        var byChars = HelpLine.Fit(items, width: 6, Chars);
        var byColumns = HelpLine.Fit(items, width: 6, Cols);

        // Char-count wrongly thinks it fits (5 ≤ 6) and leaves the line unchanged...
        Assert.Same(items, byChars);
        // ...while the column measure correctly truncates (9 > 6) down to just the fallback.
        HelpItem[] onlyFallback = [HelpLine.HelpFallback];
        Assert.Equal(onlyFallback, byColumns);
    }
}
