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
    }

    // ── Parse: URLs ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("https://app.clickup.com/t/86abc123", "86abc123")]
    [InlineData("https://odbm.clickup.com/t/86abc123", "86abc123")] // workspace subdomain accepted
    [InlineData("https://app.clickup.com/t/86abc123/", "86abc123")] // trailing slash
    [InlineData("https://app.clickup.com/t/86abc123?comment=1#c", "86abc123")] // query + fragment stripped
    [InlineData("app.clickup.com/t/86abc123", "86abc123")] // scheme-less paste
    public void Parse_TaskUrl_ExtractsPlainId(string input, string expectedId)
    {
        var r = QuickOpenParser.Parse(input);
        Assert.Equal(QuickOpenKind.TaskId, r.Kind);
        Assert.Equal(expectedId, r.Value);
    }

    [Theory]
    [InlineData("https://app.clickup.com/t/9014107164/ABC-123", "ABC-123")]
    [InlineData("https://odbm.clickup.com/t/9014107164/DEV-42", "DEV-42")]
    public void Parse_CustomIdUrl_ExtractsCustomId(string input, string expectedCustomId)
    {
        var r = QuickOpenParser.Parse(input);
        Assert.Equal(QuickOpenKind.CustomId, r.Kind);
        Assert.Equal(expectedCustomId, r.Value);
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
    public void FindInCache_NoMatch_ReturnsNull()
        => Assert.Null(QuickOpenParser.FindInCache([Task("aaa")], QuickOpenRef.Task("zzz")));

    [Fact]
    public void FindInCache_InvalidRef_ReturnsNull()
        => Assert.Null(QuickOpenParser.FindInCache([Task("aaa")], QuickOpenRef.Invalid));
}
