using ClickUpTodo.Configuration;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests for the pure link-activation dispatcher (#318): the (span, modifiers) → <see cref="LinkAction"/>
/// mapping shared with the keyboard path (#319), and the two coordinate helpers a click hit test needs.
/// </summary>
public sealed class LinkActivationTests
{
    private static LinkSpan Web(int start = 0, int length = 10, string url = "https://example.com/x")
        => new(start, length, LinkKind.Web, url);

    private static LinkSpan Task(int start = 0, int length = 10, bool customId = false)
        => new(start, length, LinkKind.Task, "https://app.clickup.com/t/abc123", "abc123", customId);

    // ── Resolve ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_PlainClickOnTaskLink_OpensTaskDetail()
        => Assert.Equal(LinkAction.OpenTaskDetail, LinkActivator.Resolve(Task(), ctrl: false));

    [Fact]
    public void Resolve_PlainClickOnWebLink_OpensBrowser()
        => Assert.Equal(LinkAction.OpenInBrowser, LinkActivator.Resolve(Web(), ctrl: false));

    [Fact]
    public void Resolve_CtrlClickOnTaskLink_OpensBrowser()
        => Assert.Equal(LinkAction.OpenInBrowser, LinkActivator.Resolve(Task(), ctrl: true));

    [Fact]
    public void Resolve_CtrlClickOnWebLink_OpensBrowser()
        => Assert.Equal(LinkAction.OpenInBrowser, LinkActivator.Resolve(Web(), ctrl: true));

    [Fact]
    public void Resolve_CustomIdTaskLink_IsStillAnInAppOpen()
    {
        // A custom-id link (/t/{teamId}/{customId}) resolves to a task the same way a pasted custom id
        // does (#353) — the host owns that lookup, so the action here is unchanged.
        Assert.Equal(LinkAction.OpenTaskDetail, LinkActivator.Resolve(Task(customId: true), ctrl: false));
        Assert.Equal(LinkAction.OpenInBrowser, LinkActivator.Resolve(Task(customId: true), ctrl: true));
    }

    [Fact]
    public void Resolve_CtrlIsTheDefaultBrowserGesture_WhenTheDestinationIsUnset()
    {
        // The #318 behaviour is preserved by the default parameter: with no destination supplied,
        // Ctrl+click on a task link still opens the browser (the setting defaults to Browser).
        foreach (var span in new[] { Web(), Task(), Task(customId: true) })
            Assert.Equal(LinkAction.OpenInBrowser, LinkActivator.Resolve(span, ctrl: true));
    }

    // ── Resolve: #320 configurable Ctrl+Click destination + Shift inversion ────

    [Theory]
    [InlineData(TaskLinkCtrlClickDestination.Browser, false, LinkAction.OpenInBrowser)]
    [InlineData(TaskLinkCtrlClickDestination.Browser, true, LinkAction.OpenTaskInNewTab)]
    [InlineData(TaskLinkCtrlClickDestination.NewTerminalTab, false, LinkAction.OpenTaskInNewTab)]
    [InlineData(TaskLinkCtrlClickDestination.NewTerminalTab, true, LinkAction.OpenInBrowser)]
    public void Resolve_CtrlOnTaskLink_FollowsTheDestination_AndShiftInvertsIt(
        TaskLinkCtrlClickDestination destination, bool shift, LinkAction expected)
        => Assert.Equal(expected, LinkActivator.Resolve(Task(), ctrl: true, shift, destination));

    [Fact]
    public void Resolve_CustomIdTaskLink_FollowsTheSameCtrlDestinationMatrix()
    {
        // A custom-id link is not special here — the host owns resolving the id (#353), so the action
        // is chosen by the same modifiers/destination as a plain-id task link.
        Assert.Equal(LinkAction.OpenTaskInNewTab,
            LinkActivator.Resolve(Task(customId: true), ctrl: true, shift: false, TaskLinkCtrlClickDestination.NewTerminalTab));
        Assert.Equal(LinkAction.OpenInBrowser,
            LinkActivator.Resolve(Task(customId: true), ctrl: true, shift: true, TaskLinkCtrlClickDestination.NewTerminalTab));
    }

    [Theory]
    [InlineData(TaskLinkCtrlClickDestination.Browser)]
    [InlineData(TaskLinkCtrlClickDestination.NewTerminalTab)]
    public void Resolve_WebLink_AlwaysOpensBrowser_WhateverTheModifiersOrDestination(TaskLinkCtrlClickDestination destination)
    {
        foreach (var ctrl in new[] { false, true })
            foreach (var shift in new[] { false, true })
                Assert.Equal(LinkAction.OpenInBrowser, LinkActivator.Resolve(Web(), ctrl, shift, destination));
    }

    [Theory]
    [InlineData(TaskLinkCtrlClickDestination.Browser)]
    [InlineData(TaskLinkCtrlClickDestination.NewTerminalTab)]
    public void Resolve_PlainClickOnTaskLink_IsAlwaysInApp_RegardlessOfDestinationOrShift(TaskLinkCtrlClickDestination destination)
    {
        // Without Ctrl the gesture is an in-app open; the destination and a (pane-refused) Shift never
        // apply. This is the plain-click / Enter path #319 shares.
        Assert.Equal(LinkAction.OpenTaskDetail, LinkActivator.Resolve(Task(), ctrl: false, shift: false, destination));
        Assert.Equal(LinkAction.OpenTaskDetail, LinkActivator.Resolve(Task(), ctrl: false, shift: true, destination));
    }

    // ── SpanAt ───────────────────────────────────────────────────────────────

    [Fact]
    public void SpanAt_HitsTheFirstCharacterOfASpan()
    {
        var spans = new[] { Web(start: 5, length: 4) };
        Assert.Equal(spans[0], LinkActivator.SpanAt(spans, 5));
    }

    [Fact]
    public void SpanAt_HitsTheLastCharacterOfASpan()
    {
        var spans = new[] { Web(start: 5, length: 4) };   // covers 5..8
        Assert.Equal(spans[0], LinkActivator.SpanAt(spans, 8));
    }

    [Fact]
    public void SpanAt_ExclusiveEndIsNotAHit()
    {
        // The offset just past a link is where a click right of the text clamps to; treating it as a hit
        // is exactly the false positive the pane's guards exist to prevent.
        var spans = new[] { Web(start: 5, length: 4) };
        Assert.Null(LinkActivator.SpanAt(spans, 9));
    }

    [Fact]
    public void SpanAt_MissesBeforeTheFirstSpanAndInGaps()
    {
        var spans = new[] { Web(start: 5, length: 3), Web(start: 20, length: 3) };
        Assert.Null(LinkActivator.SpanAt(spans, 0));
        Assert.Null(LinkActivator.SpanAt(spans, 4));
        Assert.Null(LinkActivator.SpanAt(spans, 10));
        Assert.Null(LinkActivator.SpanAt(spans, 100));
    }

    [Fact]
    public void SpanAt_FindsTheRightSpanAmongSeveral()
    {
        var first = Web(start: 0, length: 5);
        var second = Task(start: 10, length: 6);
        var third = Web(start: 30, length: 2);
        var spans = new[] { first, second, third };

        Assert.Equal(first, LinkActivator.SpanAt(spans, 3));
        Assert.Equal(second, LinkActivator.SpanAt(spans, 12));
        Assert.Equal(third, LinkActivator.SpanAt(spans, 31));
    }

    [Fact]
    public void SpanAt_NegativeOffsetAndEmptyListAreMisses()
    {
        Assert.Null(LinkActivator.SpanAt(new[] { Web() }, -1));
        Assert.Null(LinkActivator.SpanAt(Array.Empty<LinkSpan>(), 0));
    }

    [Fact]
    public void SpanAt_AgreesWithTheExtractorOnRealText()
    {
        // Anchor the offset convention to the model that produces the spans, not to hand-written numbers.
        const string line = "see https://app.clickup.com/t/abc123 and https://example.com/x now";
        var spans = TaskLinkExtractor.Extract(line);
        var taskUrlStart = line.IndexOf("https://app.clickup.com", StringComparison.Ordinal);
        var webUrlStart = line.IndexOf("https://example.com", StringComparison.Ordinal);

        Assert.Equal(LinkKind.Task, LinkActivator.SpanAt(spans, taskUrlStart)!.Value.Kind);
        Assert.Equal(LinkKind.Web, LinkActivator.SpanAt(spans, webUrlStart)!.Value.Kind);
        Assert.Null(LinkActivator.SpanAt(spans, 0));                    // "see"
        Assert.Null(LinkActivator.SpanAt(spans, line.Length - 1));       // "now"
    }

    // ── CharOffsetAtCell ─────────────────────────────────────────────────────

    [Fact]
    public void CharOffsetAtCell_IsIdentityForSingleCharGraphemes()
    {
        var graphemes = "abcdef".Select(c => c.ToString()).ToArray();
        for (var i = 0; i <= graphemes.Length; i++)
            Assert.Equal(i, LinkActivator.CharOffsetAtCell(graphemes, i));
    }

    [Fact]
    public void CharOffsetAtCell_SkipsSurrogatePairsByTheirCharLength()
    {
        // "ab 😀😀 h…" — each emoji is one cell but two UTF-16 chars, so the cell index of 'h' (6) maps
        // to char offset 8. This is the divergence Terminal.Gui's cell-indexed click position introduces.
        var graphemes = new[] { "a", "b", " ", "\U0001F600", "\U0001F600", " ", "h" };
        Assert.Equal(0, LinkActivator.CharOffsetAtCell(graphemes, 0));
        Assert.Equal(3, LinkActivator.CharOffsetAtCell(graphemes, 3));
        Assert.Equal(5, LinkActivator.CharOffsetAtCell(graphemes, 4));
        Assert.Equal(7, LinkActivator.CharOffsetAtCell(graphemes, 5));
        Assert.Equal(8, LinkActivator.CharOffsetAtCell(graphemes, 6));
    }

    [Fact]
    public void CharOffsetAtCell_CountsCombiningMarksWithTheirBaseCharacter()
    {
        var graphemes = new[] { "e\u0301", "f" };   // "e" + combining acute: one grapheme, two chars
        Assert.Equal(0, LinkActivator.CharOffsetAtCell(graphemes, 0));
        Assert.Equal(2, LinkActivator.CharOffsetAtCell(graphemes, 1));
        Assert.Equal(3, LinkActivator.CharOffsetAtCell(graphemes, 2));
    }

    [Fact]
    public void CharOffsetAtCell_ClampsOutOfRangeIndices()
    {
        var graphemes = new[] { "a", "b", "c" };
        Assert.Equal(0, LinkActivator.CharOffsetAtCell(graphemes, -5));
        Assert.Equal(3, LinkActivator.CharOffsetAtCell(graphemes, 99));
        Assert.Equal(0, LinkActivator.CharOffsetAtCell(Array.Empty<string>(), 4));
    }
}
