using ClickUpTodo.Setup;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// The shared "open a task in the browser" core both TUI hosts delegate to (#346): the
/// app.clickup.com → workspace-subdomain rewrite (#304), URL parsing, and the launch outcome. The two
/// hosts previously duplicated (and drifted on) this; here it is exercised once with a fake launcher.
/// </summary>
public sealed class ClickUpTaskBrowserTests
{
    /// <summary>Records the URLs handed to it and returns a fixed success/failure verdict.</summary>
    private sealed class FakeLauncher(bool opens) : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];
        public bool TryOpen(Uri url)
        {
            Opened.Add(url);
            return opens;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Open_NoUrl_ReturnsNoUrl_AndNeverTouchesTheLauncher(string? url)
    {
        var launcher = new FakeLauncher(opens: true);

        var (result, target) = ClickUpTaskBrowser.Open(launcher, url, workspaceSubdomain: "acme");

        Assert.Equal(ClickUpTaskBrowser.Result.NoUrl, result);
        Assert.Equal("", target);
        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public void Open_NotAbsoluteUrl_ReturnsInvalidUrl_AndNeverTouchesTheLauncher()
    {
        var launcher = new FakeLauncher(opens: true);

        var (result, target) = ClickUpTaskBrowser.Open(launcher, "not a url", workspaceSubdomain: null);

        Assert.Equal(ClickUpTaskBrowser.Result.InvalidUrl, result);
        Assert.Equal("not a url", target);
        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public void Open_ValidUrl_LauncherSucceeds_ReturnsOpened()
    {
        var launcher = new FakeLauncher(opens: true);

        var (result, target) = ClickUpTaskBrowser.Open(launcher, "https://app.clickup.com/t/abc", workspaceSubdomain: null);

        Assert.Equal(ClickUpTaskBrowser.Result.Opened, result);
        Assert.Equal("https://app.clickup.com/t/abc", target);
        Assert.Equal(new Uri("https://app.clickup.com/t/abc"), Assert.Single(launcher.Opened));
    }

    [Fact]
    public void Open_ValidUrl_LauncherFails_ReturnsLaunchFailed()
    {
        var launcher = new FakeLauncher(opens: false);

        var (result, _) = ClickUpTaskBrowser.Open(launcher, "https://app.clickup.com/t/abc", workspaceSubdomain: null);

        Assert.Equal(ClickUpTaskBrowser.Result.LaunchFailed, result);
        Assert.Single(launcher.Opened);
    }

    [Fact]
    public void Open_RewritesAppHostOntoTheWorkspaceSubdomain_BeforeLaunching()
    {
        var launcher = new FakeLauncher(opens: true);

        var (result, target) = ClickUpTaskBrowser.Open(launcher, "https://app.clickup.com/t/abc", workspaceSubdomain: "acme");

        Assert.Equal(ClickUpTaskBrowser.Result.Opened, result);
        Assert.Equal("https://acme.clickup.com/t/abc", target);
        Assert.Equal(new Uri("https://acme.clickup.com/t/abc"), Assert.Single(launcher.Opened));
    }
}
