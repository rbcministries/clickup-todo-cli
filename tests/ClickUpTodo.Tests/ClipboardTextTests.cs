using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>The Task Detail copy-URL gesture's pure core, exercised with a recording clipboard fake.</summary>
public sealed class ClipboardTextTests
{
    private const string AppUrl = "https://app.clickup.com/t/abc123";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CopyTaskUrl_NoUrl_ReportsNoUrl_AndNeverTouchesTheClipboard(string? url)
    {
        var copied = new List<string>();

        var message = ClipboardText.CopyTaskUrl(url, "odbm", t => { copied.Add(t); return true; });

        Assert.Equal("No URL for this task.", message);
        Assert.Empty(copied);
    }

    [Theory]
    [InlineData("odbm", "https://odbm.clickup.com/t/abc123")]
    [InlineData("", AppUrl)]
    [InlineData(null, AppUrl)]
    public void CopyTaskUrl_CopiesTheRewrittenUrl(string? subdomain, string expected)
    {
        var copied = new List<string>();

        var message = ClipboardText.CopyTaskUrl(AppUrl, subdomain, t => { copied.Add(t); return true; });

        Assert.Equal([expected], copied);
        Assert.Equal($"Copied URL: {expected}", message);
    }

    [Fact]
    public void CopyTaskUrl_ClipboardUnavailable_ShowsTheUrlToCopyByHand()
    {
        var message = ClipboardText.CopyTaskUrl(AppUrl, "odbm", _ => false);

        Assert.Equal("Couldn't reach the clipboard — copy the URL: https://odbm.clickup.com/t/abc123", message);
    }
}
