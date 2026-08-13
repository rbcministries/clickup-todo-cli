using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure quick-open (#303, Ctrl+O) parser + cache resolution. Covers the id /
/// custom-id / URL input shapes and the cache-first (id-then-custom-id) match order.
/// </summary>
public sealed class QuickOpenParserTests
{
    // ── Parse: bare tokens ────────────────────────────────────────────────
    [Theory]
    [InlineData("86abc123")]
    [InlineData("9hx")]
    [InlineData("  86abc123  ")] // trimmed
    public void Parse_BareId_IsTaskId(string input)
    {
        var r = QuickOpenParser.Parse(input);
        Assert.Equal(QuickOpenKind.TaskId, r.Kind);
        Assert.Equal(input.Trim(), r.Value);
    }

    [Theory]
    [InlineData("ABC-123")]
    [InlineData("dev-42")]
    public void Parse_HyphenatedToken_IsCustomId(string input)
    {
        var r = QuickOpenParser.Parse(input);
        Assert.Equal(QuickOpenKind.CustomId, r.Kind);
        Assert.Equal(input, r.Value);
        Assert.Null(r.TeamId); // a bare custom id carries no team id — resolves against the configured workspace
    }

    // ── Parse: URLs ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("https://app.clickup.com/t/86abc123", "86abc123")]
    [InlineData("https://odbm.clickup.com/t/86abc123", "86abc123")] // workspace subdomain accepted
    [InlineData("https://app.clickup.com/t/86abc123/", "86abc123")] // trailing slash
    [InlineData("https://app.clickup.com/t/86abc123?comment=1#c", "86abc123")] // query + fragment stripped
    [InlineData("app.clickup.com/t/86abc123", "86abc123")] // scheme-less paste (subdomain)
    [InlineData("clickup.com/t/86abc123", "86abc123")] // scheme-less paste (apex)
    [InlineData("https://evil.com@app.clickup.com/t/86abc123", "86abc123")] // userinfo — host is the real one
    public void Parse_TaskUrl_ExtractsPlainId(string input, string expectedId)
    {
        var r = QuickOpenParser.Parse(input);
        Assert.Equal(QuickOpenKind.TaskId, r.Kind);
        Assert.Equal(expectedId, r.Value);
        Assert.Null(r.TeamId); // a plain-id URL has no team segment
    }

    [Theory]
    [InlineData("https://app.clickup.com.evil.com/t/86abc123")] // subdomain-suffix spoof
    [InlineData("https://notclickup.com/t/86abc123")]
    public void Parse_SpoofedClickUpHost_IsInvalid(string input)
        => Assert.Equal(QuickOpenKind.Invalid, QuickOpenParser.Parse(input).Kind);

    [Theory]
    [InlineData("https://app.clickup.com/t/9014107164/ABC-123", "ABC-123", "9014107164")]
    [InlineData("https://odbm.clickup.com/t/9014107164/DEV-42", "DEV-42", "9014107164")]
    public void Parse_CustomIdUrl_ExtractsCustomIdAndTeamId(string input, string expectedCustomId, string expectedTeamId)
    {
        var r = QuickOpenParser.Parse(input);
        Assert.Equal(QuickOpenKind.CustomId, r.Kind);
        Assert.Equal(expectedCustomId, r.Value);
        // #353 item 2: the URL's own team_id is carried so the caller resolves against that workspace,
        // not the configured one (a custom-id URL pasted from a different workspace no longer 404s).
        Assert.Equal(expectedTeamId, r.TeamId);
    }

    [Fact]
    public void Parse_HyphenlessCustomIdUrl_IsCustomIdWithTeamId()
    {
        // A hyphenless custom id (e.g. PROJ123) in a /t/{team}/{custom} URL still classifies as a
        // custom id (the URL shape disambiguates it), carrying its team id.
        var r = QuickOpenParser.Parse("https://app.clickup.com/t/9014107164/PROJ123");
        Assert.Equal(QuickOpenKind.CustomId, r.Kind);
        Assert.Equal("PROJ123", r.Value);
        Assert.Equal("9014107164", r.TeamId);
    }

    // ── Parse: invalid ────────────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_Blank_IsInvalid(string? input)
        => Assert.Equal(QuickOpenKind.Invalid, QuickOpenParser.Parse(input).Kind);

    [Theory]
    [InlineData("https://example.com/t/86abc123")] // foreign host, even with a /t/ path
    [InlineData("https://github.com/rbcministries/clickup-todo-cli")]
    public void Parse_NonClickUpUrl_IsInvalid(string input)
        => Assert.Equal(QuickOpenKind.Invalid, QuickOpenParser.Parse(input).Kind);

    [Theory]
    [InlineData("https://app.clickup.com/9014107164/v/l/901401775377")] // a list URL, not a task
    [InlineData("https://app.clickup.com/t/")] // /t/ with nothing after it
    [InlineData("https://app.clickup.com/")]
    public void Parse_ClickUpUrlWithoutTaskSegment_IsInvalid(string input)
        => Assert.Equal(QuickOpenKind.Invalid, QuickOpenParser.Parse(input).Kind);

    // ── FindInCache ───────────────────────────────────────────────────────
    private static TaskItem Task(string id, string? customId = null) => new()
    {
        Id = id,
        Name = $"task {id}",
        CustomId = customId,
    };

    [Fact]
    public void FindInCache_MatchesByPlainId()
    {
        var universe = new[] { Task("aaa"), Task("bbb", "ABC-1"), Task("ccc") };
        var found = QuickOpenParser.FindInCache(universe, QuickOpenRef.Task("bbb"));
        Assert.Equal("bbb", found?.Id);
    }

    [Fact]
    public void FindInCache_MatchesByCustomId_CaseInsensitive()
    {
        var universe = new[] { Task("aaa"), Task("bbb", "ABC-1") };
        var found = QuickOpenParser.FindInCache(universe, QuickOpenRef.Custom("abc-1"));
        Assert.Equal("bbb", found?.Id);
    }

    [Fact]
    public void FindInCache_PrefersIdOverCustomId()
    {
        // A ref value that is one task's id and another task's custom id resolves to the id match first.
        var byCustom = Task("aaa", "shared");
        var byId = Task("shared");
        var found = QuickOpenParser.FindInCache([byCustom, byId], QuickOpenRef.Task("shared"));
        Assert.Equal("shared", found?.Id);
    }

    [Fact]
    public void FindInCache_IsKindAgnostic_MatchesCustomIdFieldForATaskIdRef()
    {
        // A TaskId-kind ref whose value happens to match only a task's CustomId still resolves — the
        // resolution order tries both fields regardless of the ref's kind (a bare hyphenless custom id
        // parses as TaskId but still opens if it's on screen).
        var universe = new[] { Task("aaa", "XYZ") };
        var found = QuickOpenParser.FindInCache(universe, QuickOpenRef.Task("xyz"));
        Assert.Equal("aaa", found?.Id);
    }

    [Fact]
    public void FindInCache_NoMatch_ReturnsNull()
        => Assert.Null(QuickOpenParser.FindInCache([Task("aaa")], QuickOpenRef.Task("zzz")));

    [Fact]
    public void FindInCache_InvalidRef_ReturnsNull()
        => Assert.Null(QuickOpenParser.FindInCache([Task("aaa")], QuickOpenRef.Invalid));

    // ── ResolveLaunch (launch modes B, #615) ──────────────────────────────
    // The new-tab / split-pane resolution: a cache hit supplies the real id + name, a miss hands the raw
    // trimmed token to the child (both id and display name), and an unparseable token yields null.

    [Fact]
    public void ResolveLaunch_CacheHitById_UsesRealIdAndName()
    {
        var universe = new[] { Task("aaa"), Task("bbb", "ABC-1") };
        var launch = QuickOpenParser.ResolveLaunch(universe, "bbb");
        Assert.Equal(new QuickOpenLaunch("bbb", "task bbb"), launch);
    }

    [Fact]
    public void ResolveLaunch_CacheHitByCustomId_UsesRealIdAndName()
    {
        // A hyphenated bare token parses as a custom id and still resolves off the cache by CustomId.
        var universe = new[] { Task("aaa"), Task("bbb", "ABC-1") };
        var launch = QuickOpenParser.ResolveLaunch(universe, "abc-1");
        Assert.Equal(new QuickOpenLaunch("bbb", "task bbb"), launch);
    }

    [Fact]
    public void ResolveLaunch_CacheMiss_HandsRawTrimmedTokenToChild()
    {
        // An uncached but parseable token: the raw trimmed token is both the --task ref and the display
        // name — the child's --task resolves every Ctrl+O form (#464), so no parent-side round-trip.
        var launch = QuickOpenParser.ResolveLaunch([Task("aaa")], "  86zzz999  ");
        Assert.Equal(new QuickOpenLaunch("86zzz999", "86zzz999"), launch);
    }

    [Fact]
    public void ResolveLaunch_CacheMiss_Url_HandsRawUrlToChild()
    {
        // A task URL is parseable (so not rejected) but uncached — the whole URL goes to the child, whose
        // --task classifies it through this same parser.
        const string url = "https://app.clickup.com/t/86abc123";
        var launch = QuickOpenParser.ResolveLaunch([Task("aaa")], url);
        Assert.Equal(new QuickOpenLaunch(url, url), launch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://notclickup.com/t/86abc123")] // a foreign URL is unparseable
    public void ResolveLaunch_Unparseable_ReturnsNull(string input)
        => Assert.Null(QuickOpenParser.ResolveLaunch([Task("aaa")], input));
}
