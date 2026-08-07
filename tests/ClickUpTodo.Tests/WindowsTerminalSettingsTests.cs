using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the #462 Windows Terminal <c>settings.json</c> locator: the pure candidate-path
/// list built from <c>%LOCALAPPDATA%</c> and the first-existing-wins read over injected filesystem
/// delegates. No real filesystem or platform is touched.
/// </summary>
public sealed class WindowsTerminalSettingsTests
{
    private const string LocalAppData = @"C:\Users\me\AppData\Local";

    private static Func<string, string?> Env(string? localAppData)
        => name => name == "LOCALAPPDATA" ? localAppData : null;

    [Fact]
    public void CandidatePaths_OrdersStablePreviewCanaryThenUnpackaged()
    {
        var paths = WindowsTerminalSettings.CandidatePaths(Env(LocalAppData));

        Assert.Equal(4, paths.Count);
        Assert.Contains("Microsoft.WindowsTerminal_8wekyb3d8bbwe", paths[0]);
        Assert.Contains("Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", paths[1]);
        Assert.Contains("Microsoft.WindowsTerminalCanary_8wekyb3d8bbwe", paths[2]);
        Assert.Contains(Path.Combine("Microsoft", "Windows Terminal", "settings.json"), paths[3]);
        Assert.All(paths, p => Assert.EndsWith("settings.json", p));
        Assert.All(paths, p => Assert.StartsWith(LocalAppData, p));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CandidatePaths_EmptyWhenLocalAppDataUnset(string? localAppData)
        => Assert.Empty(WindowsTerminalSettings.CandidatePaths(Env(localAppData)));

    [Fact]
    public void Load_ReturnsContentOfFirstExistingCandidate()
    {
        var candidates = WindowsTerminalSettings.CandidatePaths(Env(LocalAppData));
        var existing = candidates[1]; // the Preview path exists; the stable one doesn't.

        var content = WindowsTerminalSettings.Load(
            Env(LocalAppData),
            fileExists: p => p == existing,
            readAllText: p => p == existing ? "{ \"ok\": true }" : throw new IOException("wrong file"));

        Assert.Equal("{ \"ok\": true }", content);
    }

    [Fact]
    public void Load_ReturnsNull_WhenNoCandidateExists()
        => Assert.Null(WindowsTerminalSettings.Load(Env(LocalAppData), fileExists: _ => false, readAllText: _ => "x"));

    [Fact]
    public void Load_ReturnsNull_WhenLocalAppDataUnset()
        => Assert.Null(WindowsTerminalSettings.Load(Env(null), fileExists: _ => true, readAllText: _ => "x"));

    public static TheoryData<Exception> ReadFailures => new()
    {
        new UnauthorizedAccessException(),
        new IOException(),
        new System.Security.SecurityException(),
        new NotSupportedException(),
    };

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void Load_ReturnsNull_WhenReadThrows(Exception failure)
        => Assert.Null(WindowsTerminalSettings.Load(
            Env(LocalAppData),
            fileExists: _ => true,
            readAllText: _ => throw failure));
}
