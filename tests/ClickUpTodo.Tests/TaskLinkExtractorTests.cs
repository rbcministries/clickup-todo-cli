using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure link model (issue #316) — the foundation for link styling (#317), mouse
/// activation (#318), and focus traversal (#319). Terminal.Gui-free, so the detection, classification,
/// and offset accuracy are all covered here.
/// </summary>
public sealed class TaskLinkExtractorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no links here at all")]
    [InlineData("mailto:someone@example.com is not http")]
    [InlineData("ftp://example.com/file is not matched")]
    public void Extract_NoHttpLinks_ReturnsEmpty(string? text)
    {
        Assert.Empty(TaskLinkExtractor.Extract(text));
    }

    [Fact]
    public void Extract_SingleWebUrl_ReturnsOneWebSpanWithExactOffsets()
    {
        const string text = "See https://example.com/docs for details";
        var spans = TaskLinkExtractor.Extract(text);

        var span = Assert.Single(spans);
        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Null(span.TaskId);
        Assert.Equal("https://example.com/docs", span.Url);
        // The span indexes back into the source string exactly.
        Assert.Equal("https://example.com/docs", text.Substring(span.Start, span.Length));
        Assert.Equal(text.IndexOf("https", StringComparison.Ordinal), span.Start);
        Assert.Equal(span.Start + span.Length, span.End);
    }

    [Fact]
    public void Extract_ClickUpTaskUrl_ClassifiedAsTaskWithId()
    {
        const string text = "Fixed in https://app.clickup.com/t/86c1abced now";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal("86c1abced", span.TaskId);
        Assert.Equal("https://app.clickup.com/t/86c1abced", span.Url);
        Assert.Equal("https://app.clickup.com/t/86c1abced", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_WorkspacePrefixedTaskUrl_ExtractsId()
    {
        const string text = "https://app.clickup.com/9014107164/t/86c1abced";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal("86c1abced", span.TaskId);
    }

    [Fact]
    public void Extract_NonClickUpHostWithSlashT_IsWebNotTask()
    {
        // A "/t/" path on some other host must not be mistaken for a ClickUp task link.
        const string text = "https://example.com/t/86c1abced";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Null(span.TaskId);
    }

    [Fact]
    public void Extract_ClickUpSubdomainWithSlashT_IsWebNotTask()
    {
        // Only app.clickup.com / clickup.com are task hosts — a help/marketing subdomain with a "/t/"
        // path (a help-centre article, say) must stay a plain web link.
        const string text = "https://help.clickup.com/t/some-article";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Null(span.TaskId);
    }

    [Theory]
    [InlineData("see https://. done")]
    [InlineData("https://,")]
    [InlineData("wrapped (https://)")]
    public void Extract_DegenerateSchemeOnly_IsNotEmittedAsSpan(string text)
    {
        // Trimming a match like "https://." leaves the bare scheme "https://", which has no host and is
        // not a navigable link — it must be dropped, not emitted as a host-less span.
        Assert.Empty(TaskLinkExtractor.Extract(text));
    }

    [Fact]
    public void Extract_TaskUrlWithQueryAndFragment_ClassifiesAndKeepsFullUrl()
    {
        // A query string / fragment lives outside AbsolutePath, so classification still works; the whole
        // URL (query included) is preserved in the span.
        const string text = "here: https://app.clickup.com/t/86c1abced?comment=99#c";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal("86c1abced", span.TaskId);
        Assert.Equal("https://app.clickup.com/t/86c1abced?comment=99#c", span.Url);
        Assert.Equal(span.Url, text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_WebUrlWithQuery_KeepsQueryIntact()
    {
        const string text = "search https://example.com/find?q=hello&n=2 now";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Equal("https://example.com/find?q=hello&n=2", span.Url);
    }

    [Fact]
    public void Extract_HttpNonTlsWebUrl_IsDetected()
    {
        const string text = "legacy http://example.com/x end";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Equal("http://example.com/x", span.Url);
    }

    [Fact]
    public void Extract_UppercaseScheme_IsDetected()
    {
        const string text = "HTTPS://example.com/x";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Equal("HTTPS://example.com/x", span.Url);
    }

    [Fact]
    public void Extract_StackedTrailingPunctuation_AllTrimmed()
    {
        const string text = "(https://example.com/page).";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal("https://example.com/page", span.Url);
        Assert.Equal(span.Url, text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_OffsetsAreCharAccurateAfterAstralCharacter()
    {
        // The offsets are UTF-16 char indices; an emoji (a surrogate pair) before the link must not throw
        // Start/Length off — the span must still slice back to the exact URL.
        const string text = "😀 https://app.clickup.com/t/ZZ";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal("ZZ", span.TaskId);
        Assert.Equal(span.Url, text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_MultipleLinksOnOneLine_InOrderWithAccurateOffsets()
    {
        const string text = "a https://one.example.com b https://app.clickup.com/t/T2 c";
        var spans = TaskLinkExtractor.Extract(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal(LinkKind.Web, spans[0].Kind);
        Assert.Equal("https://one.example.com", spans[0].Url);
        Assert.Equal(LinkKind.Task, spans[1].Kind);
        Assert.Equal("T2", spans[1].TaskId);
        // Both spans slice back to their URL text.
        Assert.Equal(spans[0].Url, text.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal(spans[1].Url, text.Substring(spans[1].Start, spans[1].Length));
        // Document order.
        Assert.True(spans[0].Start < spans[1].Start);
    }

    [Fact]
    public void Extract_LinksAcrossMultipleLines_KeepCorrectOffsets()
    {
        const string text = "first https://a.example.com\nsecond https://app.clickup.com/t/ZZ";
        var spans = TaskLinkExtractor.Extract(text);

        Assert.Equal(2, spans.Count);
        foreach (var span in spans)
            Assert.Equal(span.Url, text.Substring(span.Start, span.Length));
        Assert.Equal("ZZ", spans[1].TaskId);
    }

    [Theory]
    [InlineData("Read https://example.com/page.", "https://example.com/page")]
    [InlineData("Read https://example.com/page,", "https://example.com/page")]
    [InlineData("(https://example.com/page)", "https://example.com/page")]
    [InlineData("end: https://example.com/page!", "https://example.com/page")]
    [InlineData("quote \"https://example.com/page\"", "https://example.com/page")]
    public void Extract_TrimsTrailingPunctuation(string text, string expectedUrl)
    {
        var span = Assert.Single(TaskLinkExtractor.Extract(text));
        Assert.Equal(expectedUrl, span.Url);
        Assert.Equal(expectedUrl, text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_PreservesBalancedParensInUrl()
    {
        // A URL that legitimately ends in ")" (balanced with an earlier "(") must keep the paren.
        const string text = "see https://en.wikipedia.org/wiki/Foo_(disambiguation) here";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(disambiguation)", span.Url);
    }

    [Fact]
    public void Extract_TrimsClosingParenWhenWrappingBalancedUrl()
    {
        // Wrapping parens around a URL that itself has balanced parens: the outer ")" is prose and trims,
        // the inner balanced ")" stays.
        const string text = "(https://en.wikipedia.org/wiki/Foo_(bar))";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", span.Url);
    }

    [Theory]
    [InlineData("https://app.clickup.com/t/86c1abced", "86c1abced")]
    [InlineData("https://app.clickup.com/t/86c1abced/", "86c1abced")]
    [InlineData("http://app.clickup.com/t/abc", "abc")]
    [InlineData("https://app.clickup.com/9014107164/t/xyz", "xyz")]
    [InlineData("https://clickup.com/t/rootid", "rootid")]
    public void TryParseTaskUrl_ValidForms_ExtractId(string url, string expectedId)
    {
        Assert.True(TaskLinkExtractor.TryParseTaskUrl(url, out var id));
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData("https://example.com/t/86c1abced")]      // wrong host
    [InlineData("https://app.clickup.com/v/l/123")]        // list URL, not a task
    [InlineData("https://app.clickup.com/t/")]             // no id segment
    [InlineData("https://app.clickup.com/9014107164/t/id/extra")] // trailing extra segment
    [InlineData("not a url")]
    [InlineData("ftp://app.clickup.com/t/abc")]            // non-http scheme
    public void TryParseTaskUrl_InvalidForms_ReturnFalse(string url)
    {
        Assert.False(TaskLinkExtractor.TryParseTaskUrl(url, out var id));
        Assert.Equal(string.Empty, id);
    }

    // --- #356: markdown [text](url) link spans -----------------------------------------------------------

    [Fact]
    public void Extract_MarkdownWebLink_SpansVisibleTextWithResolvedUrl()
    {
        const string text = "See [the docs](https://example.com/docs) here";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Null(span.TaskId);
        Assert.Equal("https://example.com/docs", span.Url);
        // The span covers the *visible text*, not the "[...](...)" markup.
        Assert.Equal("the docs", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_MarkdownTaskLink_ClassifiedAsTaskWithId()
    {
        const string text = "fixed in [that task](https://app.clickup.com/t/86c1abced) today";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal("86c1abced", span.TaskId);
        Assert.False(span.IsCustomTaskId);
        Assert.Equal("https://app.clickup.com/t/86c1abced", span.Url);
        Assert.Equal("that task", text.Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("[email me](mailto:someone@example.com)")]   // non-http scheme
    [InlineData("[relative](/local/page)")]                  // not absolute
    [InlineData("[missing]()")]                              // no url (fails the mdurl group)
    [InlineData("[](https://example.com)")]                  // empty visible text
    [InlineData("[   ](https://example.com)")]               // whitespace-only visible text
    public void Extract_MarkdownLinkWithoutNavigableHttpTarget_YieldsNoSpan(string text)
    {
        Assert.Empty(TaskLinkExtractor.Extract(text));
    }

    [Fact]
    public void Extract_MarkdownUrlWithBalancedParens_KeepsFullUrl()
    {
        // The markdown URL delimiter is ')', but a URL that itself contains a balanced "(...)"
        // (Wikipedia-style) must not be truncated at its inner '(' — mirrors the bare-URL path.
        const string text = "[wiki](https://en.wikipedia.org/wiki/Foo_(bar))";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Web, span.Kind);
        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", span.Url);
        Assert.Equal("wiki", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_MarkdownVisibleTextDoesNotSpanNewline()
    {
        // mdtext is single-line, so a "[" that is never closed on its own line does not swallow the
        // newline into a visible-text span (a span always sits on one rendered line). The bare task URL
        // on the next line is still detected.
        const string text = "[unclosed\nhttps://app.clickup.com/t/T9";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal("T9", span.TaskId);
        Assert.Equal("https://app.clickup.com/t/T9", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Extract_MarkdownLinkDoesNotAlsoEmitBareSpanForItsOwnUrl()
    {
        // The markdown alternative consumes the whole "[text](url)", so the URL inside the parens must not
        // be re-detected as a second, bare span.
        const string text = "[docs](https://example.com/docs)";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal("docs", text.Substring(span.Start, span.Length));
        Assert.Equal("https://example.com/docs", span.Url);
    }

    [Fact]
    public void Extract_MarkdownAndBareLinkMixed_BothInDocumentOrder()
    {
        const string text = "[a task](https://app.clickup.com/t/T1) and https://example.com/plain";
        var spans = TaskLinkExtractor.Extract(text);

        Assert.Equal(2, spans.Count);
        Assert.Equal(LinkKind.Task, spans[0].Kind);
        Assert.Equal("T1", spans[0].TaskId);
        Assert.Equal("a task", text.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal(LinkKind.Web, spans[1].Kind);
        Assert.Equal("https://example.com/plain", text.Substring(spans[1].Start, spans[1].Length));
        Assert.True(spans[0].Start < spans[1].Start);
    }

    // --- #356: custom-id task URLs ------------------------------------------------------------------------

    [Theory]
    [InlineData("https://app.clickup.com/t/9014107164/ABC-123", "ABC-123")]
    [InlineData("https://app.clickup.com/t/9014107164/ABC-123/", "ABC-123")]
    [InlineData("https://clickup.com/t/42/GH-9", "GH-9")]
    public void Extract_CustomIdTaskUrl_ClassifiedAsTaskAndFlagged(string url, string expectedId)
    {
        var span = Assert.Single(TaskLinkExtractor.Extract(url));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal(expectedId, span.TaskId);
        Assert.True(span.IsCustomTaskId);
    }

    [Fact]
    public void Extract_ApiIdTaskUrl_IsNotFlaggedCustom()
    {
        var span = Assert.Single(TaskLinkExtractor.Extract("https://app.clickup.com/t/86c1abced"));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.False(span.IsCustomTaskId);
    }

    [Fact]
    public void Extract_MarkdownCustomIdTaskLink_FlaggedCustom()
    {
        const string text = "[ticket](https://app.clickup.com/t/42/GH-9)";
        var span = Assert.Single(TaskLinkExtractor.Extract(text));

        Assert.Equal(LinkKind.Task, span.Kind);
        Assert.Equal("GH-9", span.TaskId);
        Assert.True(span.IsCustomTaskId);
        Assert.Equal("ticket", text.Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("https://app.clickup.com/t/9014107164/ABC-123", "ABC-123")]
    [InlineData("https://clickup.com/t/42/GH-9", "GH-9")]
    public void TryParseTaskUrl_CustomIdForm_ReportsCustom(string url, string expectedId)
    {
        Assert.True(TaskLinkExtractor.TryParseTaskUrl(url, out var id, out var isCustom));
        Assert.Equal(expectedId, id);
        Assert.True(isCustom);
    }

    [Theory]
    [InlineData("https://app.clickup.com/t/86c1abced", "86c1abced")]
    [InlineData("https://app.clickup.com/9014107164/t/xyz", "xyz")]
    public void TryParseTaskUrl_ApiIdForm_NotCustom(string url, string expectedId)
    {
        Assert.True(TaskLinkExtractor.TryParseTaskUrl(url, out var id, out var isCustom));
        Assert.Equal(expectedId, id);
        Assert.False(isCustom);
    }
}
