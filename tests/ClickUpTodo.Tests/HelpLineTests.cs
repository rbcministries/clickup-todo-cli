using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Input;
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

    // Pins the full main-list footer. Quick Updates launches with Ctrl+U (standardized to match Task
    // Detail, #290); F5 is the refresh key (icon ↻; Ctrl+R is its undisplayed alias) and Ctrl+E opens
    // the feed — the List ↔ Feed navigation key.
    [Fact]
    public void Format_MainList_RendersTheFullFooter()
    {
        const string expected =
            "↑/↓ move · →| next section · Ctrl+U quick update · ↩ detail · Ctrl+O 🗁 by ID · Ctrl+↩ new tab · Ctrl+N ➕ · Ctrl+B 🌐 · Ctrl+P 📌 · Ctrl+E 🔔 · "
            + "F1 ℹ · F10 ⚙ · F3 ⧩ ▼▲ ⛚ · F4 subtasks · F5 ↻ · F6 badges · F12 👁✅ · "
            + "→/← expand/collapse · Ctrl+→/← all · Ctrl+Q quit · type to search";

        Assert.Equal(expected, HelpLine.Format(HelpItemSets.MainList));
    }

    [Fact]
    public void MainList_CarriesCtrlOQuickOpen()
        => Assert.Contains(new HelpItem("Ctrl+O", "🗁 by ID"), HelpItemSets.MainList);

    [Fact]
    public void Format_QuickOpen_RendersOpenHelpCancel()
        => Assert.Equal(
            "Enter/Open open · F1 ℹ · Esc cancel",
            HelpLine.Format(HelpItemSets.QuickOpen));

    // The New Task action is on Ctrl+N; its label tightened to the ➕ glyph in #343 (key hint kept).
    [Fact]
    public void MainList_CarriesCtrlNNewTask()
        => Assert.Contains(new HelpItem("Ctrl+N", "➕"), HelpItemSets.MainList);

    [Fact]
    public void MainList_CarriesCtrlEnterNewTab_ReRaisingCtrlEnter()
    {
        // #301: the glyph key `Ctrl+↩` re-raises the parseable `Ctrl+Enter` chord when the footer item
        // is clicked (#289), converging on the same OnListKey handler as the keypress.
        var item = HelpItemSets.MainList.Single(i => i.Label == "new tab");
        Assert.Equal("Ctrl+↩", item.Key);
        Assert.Equal("Ctrl+Enter", item.ActionKey);
        Assert.True(item.IsAction);
    }

    // #384 — the detail view's Ctrl+Enter opens the current task in its own terminal tab, the detail
    // counterpart of the main list's #301 gesture. It rides the same glyph key `Ctrl+↩` re-raising the
    // parseable `Ctrl+Enter` chord when the footer item is clicked (#289), and — like the list — uses the
    // same key as the list so the two surfaces stay consistent.
    [Fact]
    public void DetailWithTaskTree_CarriesCtrlEnterNewTab_ReRaisingCtrlEnter()
    {
        var item = HelpItemSets.DetailWithTaskTree.Single(i => i.Label == "new tab");
        Assert.Equal("Ctrl+↩", item.Key);
        Assert.Equal("Ctrl+Enter", item.ActionKey);
        Assert.True(item.IsAction);
        // Same key as the list's new-tab gesture, so the shortcut doesn't drift across surfaces.
        Assert.Equal(HelpItemSets.MainList.Single(i => i.Label == "new tab").ActionKey, item.ActionKey);
    }

    // The gesture is scoped to the dashboard-hosted detail (the one with the Task Tree tab). Single-task
    // launch mode uses the leaner Detail set and neither advertises nor fires it — so a single-task tab
    // never offers "open another tab of myself".
    [Fact]
    public void Detail_SingleTaskMode_DoesNotCarryNewTab()
        => Assert.DoesNotContain(HelpItemSets.Detail, i => i.Label == "new tab");

    // #290 — the "quick update" action must use one shortcut everywhere. It launches Quick Updates from
    // both the main list and Task Detail, so both help sets must advertise the same key (Ctrl+U).
    [Fact]
    public void QuickUpdate_UsesCtrlU_OnBothListAndDetail()
    {
        var listKey = HelpItemSets.MainList.Single(i => i.Label == "quick update").Key;
        var detailKey = HelpItemSets.Detail.Single(i => i.Label == "quick update").Key;

        Assert.Equal("Ctrl+U", listKey);
        Assert.Equal(listKey, detailKey);
    }

    // #353 — quick-open (Ctrl+O) is now offered from Task Detail too, using the same key as the list so
    // the footer stays consistent across both surfaces.
    [Fact]
    public void QuickOpen_UsesCtrlO_OnBothListAndDetail()
    {
        var listKey = HelpItemSets.MainList.Single(i => i.Label == "🗁 by ID").Key;
        var detailKey = HelpItemSets.Detail.Single(i => i.Label == "🗁 by ID").Key;

        Assert.Equal("Ctrl+O", listKey);
        Assert.Equal(listKey, detailKey);
    }

    // #415 — the Task Tree tab's F6 badge cycle mirrors the main list's F6, so the detail set shown while
    // that tab exists advertises the same key/label. The base Detail set (single-task mode, no tree tab)
    // does not, since there is no badge list there.
    [Fact]
    public void Badges_UseF6_OnBothListAndTaskTreeDetail()
    {
        var listItem = HelpItemSets.MainList.Single(i => i.Label == "badges");
        var treeItem = HelpItemSets.DetailWithTaskTree.Single(i => i.Label == "badges");

        Assert.Equal("F6", listItem.Key);
        Assert.Equal(listItem.Key, treeItem.Key);
        Assert.DoesNotContain(HelpItemSets.Detail, i => i.Key == "F6");
    }

    // #436 — while an overlay editor (comment composer Ctrl+N / description editor Ctrl+E) is open the
    // footer must show only that overlay's keys. Otherwise the full command footer stays up and, since
    // footer hints are clickable, a click re-raises an inert chord into the composer (e.g. Ctrl+Enter →
    // Post). DetailFooter is the pure selector the (view-bound, CI-untestable) HelpItems property calls.
    [Fact]
    public void DetailFooter_CommentComposerOpen_ShowsComposerKeys()
        => Assert.Same(
            HelpItemSets.DetailCommentComposer,
            HelpItemSets.DetailFooter(commentComposerVisible: true, descriptionEditorVisible: false, replyPickerVisible: false, hasTaskTree: true));

    [Fact]
    public void DetailFooter_DescriptionEditorOpen_ShowsEditorKeys()
        => Assert.Same(
            HelpItemSets.DetailDescriptionEditor,
            HelpItemSets.DetailFooter(commentComposerVisible: false, descriptionEditorVisible: true, replyPickerVisible: false, hasTaskTree: true));

    // #330 — while the reply-target picker (Ctrl+T) is open the footer shows only its keys, for the same
    // #436 reason as the composer/editor overlays.
    [Fact]
    public void DetailFooter_ReplyPickerOpen_ShowsPickerKeys()
        => Assert.Same(
            HelpItemSets.DetailReplyPicker,
            HelpItemSets.DetailFooter(commentComposerVisible: false, descriptionEditorVisible: false, replyPickerVisible: true, hasTaskTree: true));

    // The composer branch wins if both flags are somehow set — the overlays are mutually exclusive, but
    // the selector must be deterministic rather than depend on that invariant holding.
    [Fact]
    public void DetailFooter_ComposerTakesPrecedenceOverDescription()
        => Assert.Same(
            HelpItemSets.DetailCommentComposer,
            HelpItemSets.DetailFooter(commentComposerVisible: true, descriptionEditorVisible: true, replyPickerVisible: false, hasTaskTree: false));

    // The composer also wins over the reply picker (a picked target hides the picker before the composer
    // opens, so both are never truly set at once — but the selector stays deterministic regardless).
    [Fact]
    public void DetailFooter_ComposerTakesPrecedenceOverReplyPicker()
        => Assert.Same(
            HelpItemSets.DetailCommentComposer,
            HelpItemSets.DetailFooter(commentComposerVisible: true, descriptionEditorVisible: false, replyPickerVisible: true, hasTaskTree: false));

    [Fact]
    public void DetailFooter_NoOverlay_WithTaskTree_ShowsTaskTreeSet()
        => Assert.Same(
            HelpItemSets.DetailWithTaskTree,
            HelpItemSets.DetailFooter(commentComposerVisible: false, descriptionEditorVisible: false, replyPickerVisible: false, hasTaskTree: true));

    [Fact]
    public void DetailFooter_NoOverlay_NoTaskTree_ShowsBaseDetailSet()
        => Assert.Same(
            HelpItemSets.Detail,
            HelpItemSets.DetailFooter(commentComposerVisible: false, descriptionEditorVisible: false, replyPickerVisible: false, hasTaskTree: false));

    // #325: while the @-mention picker is overlaid on the composer, the footer shows only the picker's
    // keys — and the picker branch wins over the composer's (it sits on top of it).
    [Fact]
    public void DetailFooter_MentionPickerOpen_ShowsPickerKeys()
        => Assert.Same(
            HelpItemSets.DetailMentionPicker,
            HelpItemSets.DetailFooter(
                commentComposerVisible: true, descriptionEditorVisible: false, replyPickerVisible: false,
                hasTaskTree: true, mentionPickerVisible: true));

    [Fact]
    public void Format_DetailMentionPicker_RendersPickerKeys()
        => Assert.Equal(
            "type search · ↑↓ move · Enter mention · Esc back",
            HelpLine.Format(HelpItemSets.DetailMentionPicker));

    // The composer footer advertises only the composer's own keys (mirrors the description editor's set):
    // Ctrl+Enter/Tab→Post, Esc cancel, F1 Help, and the #325 @ mention-trigger — matching the composer
    // FrameView title and OnCommentKey.
    [Fact]
    public void Format_DetailCommentComposer_RendersComposerKeys()
        => Assert.Equal(
            "Tab editor/Post/Cancel · @ mention · Ctrl+Enter post · F1 ℹ · Esc cancel",
            HelpLine.Format(HelpItemSets.DetailCommentComposer));

    // #326 — the description editor footer mirrors the composer's, carrying the same @ mention-trigger
    // hint (IsAction:false — a typed character, not a re-raisable chord); a description mention is plain
    // literal text (#321), so the footer wording is identical to the composer's aside from Save vs Post.
    [Fact]
    public void Format_DetailDescriptionEditor_RendersEditorKeys()
        => Assert.Equal(
            "Tab editor/Save/Cancel · @ mention · Ctrl+Enter save · F1 ℹ · Esc cancel",
            HelpLine.Format(HelpItemSets.DetailDescriptionEditor));

    // #290 — refresh is standardized on F5 (icon ↻) across every screen that can refresh, so the footer
    // never drifts. (Ctrl+R remains an undisplayed alias in each handler.)
    [Theory]
    [MemberData(nameof(RefreshableSets))]
    public void Refresh_UsesF5_OnEveryRefreshableScreen(IReadOnlyList<HelpItem> set)
        => Assert.Contains(new HelpItem("F5", "↻"), set);

    public static readonly TheoryData<IReadOnlyList<HelpItem>> RefreshableSets = new()
    {
        HelpItemSets.MainList,
        HelpItemSets.Detail,
        HelpItemSets.NotificationsFeed,
    };

    [Fact]
    public void Format_NewTask_RendersMoveSaveCancelHelp()
        => Assert.Equal(
            "Tab Name/Descr/Assignees/List · Enter/Save saves · Esc cancels · F1 ℹ",
            HelpLine.Format(HelpItemSets.NewTask));

    [Fact]
    public void Format_DispatchProviders_RendersMoveTabDeleteHelpAndCancel()
        => Assert.Equal(
            "↑/↓ move · Tab list / fields / buttons · Del delete provider · F1 ℹ · Esc cancel",
            HelpLine.Format(HelpItemSets.DispatchProviders));

    [Fact]
    public void Format_NotificationsFeed_RendersMoveMentionsHelpAndBack()
        => Assert.Equal(
            "↑/↓ move · Enter open · F3 mentions only · F5 ↻ · F6 activity · F12 👁✅ · Ctrl+E list · F1 ℹ · Esc back",
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
        HelpItemSets.DetailWithTaskTree,
        HelpItemSets.Settings,
        HelpItemSets.FilterSortGroup,
        HelpItemSets.QuickUpdates,
        HelpItemSets.QuickOpen,
        HelpItemSets.NotificationsFeed,
        HelpItemSets.DispatchProviders,
        HelpItemSets.Help,
        HelpItemSets.ExitConfirm,
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
        HelpItemSets.DetailWithTaskTree,
        HelpItemSets.Settings,
        HelpItemSets.FilterSortGroup,
        HelpItemSets.QuickUpdates,
        HelpItemSets.QuickOpen,
        HelpItemSets.NotificationsFeed,
        HelpItemSets.DispatchProviders,
    };

    [Theory]
    [MemberData(nameof(ScreenSets))]
    public void EveryScreenSet_OffersF1Help(IReadOnlyList<HelpItem> set)
        => Assert.Contains(set, i => i.Key == "F1" && i.Label == "ℹ");

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
        // Far wider than the full main-list footer at any width.
        var result = HelpLine.Fit(HelpItemSets.MainList, width: 1000, Cols);

        Assert.Same(HelpItemSets.MainList, result);
    }

    [Fact]
    public void Fit_EmptySet_ReturnsEmpty()
        => Assert.Empty(HelpLine.Fit([], width: 5, Cols));

    [Fact]
    public void Fit_Truncates_KeepingLeadingPrefixThenFallbackLast()
    {
        // At a width too narrow for the full footer, Fit keeps the longest leading prefix of the set
        // that still fits alongside the reserved F1 fallback, with the fallback appended last. The exact
        // prefix length turns on the (glyph-dependent) column widths of the leading items — since #343
        // the fallback is the short "F1 ℹ" glyph rather than "F1 Help + Shortcuts" — so this pins the
        // truncation *invariants* rather than a hard count: fallback last, a genuine leading prefix,
        // within width, and maximal (the next item would overflow).
        const int width = 70;
        var result = HelpLine.Fit(HelpItemSets.MainList, width, Cols);
        var kept = result.Take(result.Count - 1).ToList();

        Assert.Equal(HelpLine.HelpFallback, result[^1]);
        Assert.True(Cols(HelpLine.Format(result)) <= width);
        Assert.NotEmpty(kept);
        Assert.Equal(HelpItemSets.MainList.Take(kept.Count), kept);
        // Maximal: appending the next set item (with the fallback still reserved) would overflow.
        List<HelpItem> oneMore = [.. kept, HelpItemSets.MainList[kept.Count], HelpLine.HelpFallback];
        Assert.True(Cols(HelpLine.Format(oneMore)) > width);
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
        // Too narrow to fit even the first item beside the reserved F1 fallback, so only the fallback
        // survives (the "only F1 fits" case).
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

    // ── #289: action/movement classification, click chords, and hit-testing ──────────────────

    [Fact]
    public void HelpItem_TwoArgConstruction_IsAnActionWithNoExplicitChord()
    {
        // The pre-#289 two-argument form must still build (and equal) an action item with no chord —
        // this is what keeps record equality/construction working in tests (the label here is arbitrary).
        var item = new HelpItem("Ctrl+N", "new task");

        Assert.True(item.IsAction);
        Assert.Null(item.Chord);
        Assert.Equal(new HelpItem("Ctrl+N", "new task", IsAction: true, Chord: null), item);
    }

    [Fact]
    public void ActionKey_FallsBackToKey_WhenNoChord()
        => Assert.Equal("Ctrl+N", new HelpItem("Ctrl+N", "new task").ActionKey);

    [Fact]
    public void ActionKey_UsesChord_WhenTheDisplayKeyIsAGlyphOrCompound()
    {
        Assert.Equal("Space", new HelpItem("␣", "status", Chord: "Space").ActionKey);
        Assert.Equal("Delete", new HelpItem("Del", "removes selected filter", Chord: "Delete").ActionKey);
    }

    [Fact]
    public void MainList_MovementHintsAreNonClickable_AndLead()
    {
        // The arrow/next-section glyphs and the "type to search" affordance are movement/informational:
        // non-clickable, and the two cursor hints still lead the set.
        Assert.False(HelpItemSets.MainList[0].IsAction); // ↑/↓ move
        Assert.False(HelpItemSets.MainList[1].IsAction); // →| next section
        Assert.False(HelpItemSets.MainList.Single(i => i.Key == "→/←").IsAction);
        Assert.False(HelpItemSets.MainList.Single(i => i.Key == "Ctrl+→/←").IsAction);
        Assert.False(HelpItemSets.MainList.Single(i => i.Key == "type").IsAction);
    }

    [Fact]
    public void MainList_ActionHintsAreClickable()
    {
        Assert.True(HelpItemSets.MainList.Single(i => i.Key == "Ctrl+N").IsAction);
        Assert.True(HelpItemSets.MainList.Single(i => i.Key == "F6").IsAction);
        // Quick Updates launches from the list via Ctrl+U (standardized in #290); its key parses
        // directly, while the glyph-keyed "detail" action carries an explicit re-raiseable chord.
        Assert.True(HelpItemSets.MainList.Single(i => i.Key == "Ctrl+U").IsAction);
        Assert.Equal("Ctrl+U", HelpItemSets.MainList.Single(i => i.Label == "quick update").ActionKey);
        Assert.Equal("Enter", HelpItemSets.MainList.Single(i => i.Key == "↩").ActionKey);
    }

    [Fact]
    public void HelpFallback_IsAClickableActionThatOpensHelp()
    {
        Assert.True(HelpLine.HelpFallback.IsAction);
        Assert.True(Key.TryParse(HelpLine.HelpFallback.ActionKey, out var k));
        Assert.Equal(Key.F1, k);
    }

    // Every action item that can appear on the footer — across all sets and the truncation fallback —
    // must re-raise a *parseable* key, or clicking it would silently do nothing. This pins the chord
    // annotations (e.g. ␣→Space, Del→Delete, Enter/Save→Enter) against Terminal.Gui's own parser.
    public static readonly TheoryData<HelpItem> AllActionItems = BuildActionItems();

    private static TheoryData<HelpItem> BuildActionItems()
    {
        var data = new TheoryData<HelpItem>();
        IReadOnlyList<HelpItem>[] sets =
        [
            HelpItemSets.MainList, HelpItemSets.Detail, HelpItemSets.DetailWithTaskTree,
            HelpItemSets.DetailDescriptionEditor, HelpItemSets.DetailCommentComposer,
            HelpItemSets.DetailMentionPicker, HelpItemSets.DetailReplyPicker,
            HelpItemSets.DetailChecklistItemEditor, HelpItemSets.DetailChecklistAssigneePicker,
            HelpItemSets.Settings, HelpItemSets.FilterSortGroup, HelpItemSets.QuickUpdates,
            HelpItemSets.QuickOpen, HelpItemSets.NewTask, HelpItemSets.PromptTemplateEditor,
            HelpItemSets.DispatchProviders,
            HelpItemSets.NotificationsFeed, HelpItemSets.AgentRun, HelpItemSets.Help,
            HelpItemSets.ExitConfirm,
            [HelpLine.HelpFallback],
        ];
        foreach (var item in sets.SelectMany(s => s).Where(i => i.IsAction).DistinctBy(i => i.ActionKey))
            data.Add(item);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllActionItems))]
    public void EveryActionItem_ReRaisesAParseableKey(HelpItem item)
        => Assert.True(Key.TryParse(item.ActionKey, out _), $"'{item.ActionKey}' should parse");

    [Fact]
    public void ColumnRanges_MapsEachItemToItsSpan_WithSeparatorsAsGaps()
    {
        // "Esc back · F1 help": item 0 at cols 0..8, the " · " gap at 8..11, item 1 at cols 11..18.
        var items = new HelpItem[] { new("Esc", "back"), new("F1", "help") };

        var ranges = HelpLine.ColumnRanges(items, Cols);

        Assert.Equal([(0, 8), (11, 18)], ranges);
    }

    [Fact]
    public void ColumnRanges_EmptySet_IsEmpty()
        => Assert.Empty(HelpLine.ColumnRanges([], Cols));

    [Theory]
    [InlineData(-1, -1)] // before the start
    [InlineData(0, 0)]   // first column of item 0
    [InlineData(7, 0)]   // last column of item 0
    [InlineData(8, -1)]  // on the separator
    [InlineData(10, -1)] // still on the separator
    [InlineData(11, 1)]  // first column of item 1
    [InlineData(17, 1)]  // last column of item 1
    [InlineData(18, -1)] // past the end
    public void HitTest_ResolvesTheItemUnderTheColumn_OrMinusOne(int column, int expected)
    {
        var items = new HelpItem[] { new("Esc", "back"), new("F1", "help") };

        Assert.Equal(expected, HelpLine.HitTest(items, column, Cols));
    }

    [Fact]
    public void HitTest_IsColumnAware_ForWideGlyphs()
    {
        // "中 x" renders as 4 columns (the wide glyph is 2), not the 3 chars it contains. A click at
        // column 3 is still on the item; only a char-count measure would wrongly fall off its end.
        IReadOnlyList<HelpItem> items = [new("中", "x")];

        Assert.Equal(0, HelpLine.HitTest(items, 3, Cols));
        Assert.Equal(-1, HelpLine.HitTest(items, 4, Cols));
    }
}
