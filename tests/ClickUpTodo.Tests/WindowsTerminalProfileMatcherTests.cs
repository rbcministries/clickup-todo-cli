using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the #462 pure Windows Terminal profile matcher: given a JSONC
/// <c>settings.json</c> and a resolved dispatch directory, it returns the first matching profile's
/// identifier (guid preferred, else name) to hand to <c>wt -p</c>, or null on any miss. Covers the
/// JSONC quirks, the <c>defaults</c>/hidden/absent-directory exclusions, directory normalisation
/// (env vars, separators, trailing slash, case), match order, and malformed input.
/// </summary>
public sealed class WindowsTerminalProfileMatcherTests
{
    // Identity expander for the common case (no %ENV% in the fixture); a dedicated test injects a real one.
    private static readonly Func<string, string> NoExpand = s => s;

    private static string? Match(string json, string target, Func<string, string>? expand = null)
        => WindowsTerminalProfileMatcher.Match(json, target, expand ?? NoExpand);

    [Fact]
    public void Match_ReturnsGuid_ForProfileWhoseStartingDirectoryMatches()
    {
        var json = """
        {
            "profiles": {
                "defaults": {},
                "list": [
                    { "guid": "{aaa}", "name": "Home", "startingDirectory": "C:\\Users\\me" },
                    { "guid": "{bbb}", "name": "Project", "startingDirectory": "C:\\src\\foo" }
                ]
            }
        }
        """;

        Assert.Equal("{bbb}", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_PrefersGuidOverName()
    {
        var json = """
        { "profiles": { "list": [ { "guid": "{gid}", "name": "Project", "startingDirectory": "C:\\src\\foo" } ] } }
        """;

        Assert.Equal("{gid}", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_FallsBackToName_WhenNoGuid()
    {
        var json = """
        { "profiles": { "list": [ { "name": "Project", "startingDirectory": "C:\\src\\foo" } ] } }
        """;

        Assert.Equal("Project", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_ParsesJsonc_WithCommentsAndTrailingCommas()
    {
        var json = """
        {
            // WT settings are JSONC
            "profiles": {
                "list": [
                    { "guid": "{bbb}", "name": "Project", "startingDirectory": "C:\\src\\foo" }, // trailing comma below
                ],
            },
        }
        """;

        Assert.Equal("{bbb}", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_NeverMatchesProfilesDefaults()
    {
        // defaults carries a matching startingDirectory (inherited by all), but no list profile matches.
        var json = """
        {
            "profiles": {
                "defaults": { "startingDirectory": "C:\\src\\foo" },
                "list": [ { "guid": "{aaa}", "name": "Home", "startingDirectory": "C:\\Users\\me" } ]
            }
        }
        """;

        Assert.Null(Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_SkipsHiddenProfiles()
    {
        var json = """
        {
            "profiles": { "list": [
                { "guid": "{hid}", "name": "Hidden", "hidden": true, "startingDirectory": "C:\\src\\foo" },
                { "guid": "{vis}", "name": "Visible", "startingDirectory": "C:\\src\\bar" }
            ] }
        }
        """;

        // The hidden profile matches the dir but is skipped; nothing else matches it.
        Assert.Null(Match(json, "C:\\src\\foo"));
        Assert.Equal("{vis}", Match(json, "C:\\src\\bar"));
    }

    [Fact]
    public void Match_SkipsProfilesWithNoStartingDirectory()
    {
        var json = """
        {
            "profiles": { "list": [
                { "guid": "{a}", "name": "NoDir" },
                { "guid": "{b}", "name": "NullDir", "startingDirectory": null },
                { "guid": "{c}", "name": "Project", "startingDirectory": "C:\\src\\foo" }
            ] }
        }
        """;

        Assert.Equal("{c}", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_ExpandsEnvironmentVariables()
    {
        var json = """
        { "profiles": { "list": [ { "guid": "{env}", "name": "Env", "startingDirectory": "%USERPROFILE%\\src\\foo" } ] } }
        """;
        Func<string, string> expand = s => s.Replace("%USERPROFILE%", "C:\\Users\\me");

        Assert.Equal("{env}", Match(json, "C:\\Users\\me\\src\\foo", expand));
    }

    [Fact]
    public void Match_NormalisesForwardSlashesAndTrailingSeparators()
    {
        var json = """
        { "profiles": { "list": [ { "guid": "{n}", "name": "Norm", "startingDirectory": "C:/src/foo/" } ] } }
        """;

        Assert.Equal("{n}", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var json = """
        { "profiles": { "list": [ { "guid": "{ci}", "name": "Case", "startingDirectory": "C:\\Src\\Foo" } ] } }
        """;

        Assert.Equal("{ci}", Match(json, "c:\\src\\foo"));
    }

    [Fact]
    public void Match_ReturnsFirstProfileInOrder_WhenSeveralMatch()
    {
        var json = """
        {
            "profiles": { "list": [
                { "guid": "{first}", "name": "First", "startingDirectory": "C:\\src\\foo" },
                { "guid": "{second}", "name": "Second", "startingDirectory": "C:\\src\\foo" }
            ] }
        }
        """;

        Assert.Equal("{first}", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_SupportsBareProfilesArrayShape()
    {
        var json = """
        { "profiles": [ { "guid": "{arr}", "name": "Project", "startingDirectory": "C:\\src\\foo" } ] }
        """;

        Assert.Equal("{arr}", Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_ReturnsNull_OnNoMatch()
    {
        var json = """
        { "profiles": { "list": [ { "guid": "{a}", "name": "Home", "startingDirectory": "C:\\Users\\me" } ] } }
        """;

        Assert.Null(Match(json, "C:\\src\\foo"));
    }

    [Fact]
    public void Match_ReturnsNull_OnMalformedJson_WithoutThrowing()
        => Assert.Null(Match("{ this is not valid json ", "C:\\src\\foo"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_ReturnsNull_OnBlankSettings(string json)
        => Assert.Null(Match(json, "C:\\src\\foo"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_ReturnsNull_OnBlankTarget(string target)
        => Assert.Null(Match("""{ "profiles": { "list": [ { "name": "X", "startingDirectory": "C:\\src\\foo" } ] } }""", target));

    [Fact]
    public void Match_ReturnsNull_WhenProfilesMissing()
        => Assert.Null(Match("""{ "schema": 1 }""", "C:\\src\\foo"));
}
