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
}
