using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the quote-aware tokeniser behind the configurable terminal launch command (#385).
/// </summary>
public sealed class TerminalCommandParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_YieldsEmptyList(string? input)
        => Assert.Empty(TerminalCommandParser.Parse(input));

    [Fact]
    public void SplitsOnWhitespace()
        => Assert.Equal(["alacritty", "-e", "{}"], TerminalCommandParser.Parse("alacritty -e {}"));

    [Fact]
    public void CollapsesRunsOfWhitespace()
        => Assert.Equal(["kitty", "{}"], TerminalCommandParser.Parse("  kitty    {} "));

    [Fact]
    public void SingleExecutable_NoPlaceholder()
        => Assert.Equal(["kitty"], TerminalCommandParser.Parse("kitty"));

    [Fact]
    public void SingleQuotes_GroupSpaces_IntoOneToken()
        => Assert.Equal(["/opt/my term/st", "-e", "{}"], TerminalCommandParser.Parse("'/opt/my term/st' -e {}"));

    [Fact]
    public void DoubleQuotes_GroupSpaces_IntoOneToken()
        => Assert.Equal(["/opt/my term/st", "{}"], TerminalCommandParser.Parse("\"/opt/my term/st\" {}"));

    [Fact]
    public void QuotedPlaceholder_IsStillThePlaceholderToken()
        => Assert.Equal(["myterm", "--exec", "{}"], TerminalCommandParser.Parse("myterm --exec \"{}\""));

    [Fact]
    public void AdjacentQuotedAndUnquoted_JoinIntoOneToken()
        => Assert.Equal(["ab cd"], TerminalCommandParser.Parse("a\"b c\"d"));

    [Fact]
    public void EmptyQuotedRun_IsDropped()
        => Assert.Equal(["foo", "bar"], TerminalCommandParser.Parse("foo \"\" bar"));

    [Fact]
    public void UnterminatedQuote_TakesRestOfLine()
        => Assert.Equal(["wezterm", "start --"], TerminalCommandParser.Parse("wezterm \"start --"));

    [Fact]
    public void Placeholder_ConstantIsBraces()
        => Assert.Equal("{}", TerminalCommandParser.Placeholder);
}
