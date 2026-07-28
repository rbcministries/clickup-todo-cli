using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

public sealed class TerminalTitleTests
{
    [Fact]
    public void ForTask_ComposesIdAndName()
    {
        Assert.Equal("86abc: Fix the login bug", TerminalTitle.ForTask("86abc", null, "Fix the login bug"));
    }

    [Fact]
    public void ForTask_PrefersCustomIdOverNumericId()
    {
        Assert.Equal("DEV-123: Ship it", TerminalTitle.ForTask("86xyz", "DEV-123", "Ship it"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForTask_FallsBackToNumericId_WhenCustomIdBlank(string? customId)
    {
        Assert.Equal("86xyz: Ship it", TerminalTitle.ForTask("86xyz", customId, "Ship it"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForTask_BlankName_YieldsIdOnly_NoDanglingColon(string name)
    {
        Assert.Equal("86xyz", TerminalTitle.ForTask("86xyz", null, name));
    }

    [Fact]
    public void ForTask_TruncatesToFortyCharacters()
    {
        var title = TerminalTitle.ForTask("86abc", null, new string('x', 100));
        Assert.Equal(TerminalTitle.MaxLength, title.Length);
        Assert.StartsWith("86abc: xxxx", title);
    }

    [Fact]
    public void ForTask_ExactlyFortyCharacters_IsUnchanged()
    {
        // "86abc: " is 7 chars; 33 x's makes exactly 40.
        var name = new string('x', 33);
        var title = TerminalTitle.ForTask("86abc", null, name);
        Assert.Equal(40, title.Length);
        Assert.Equal("86abc: " + name, title);
    }

    [Fact]
    public void ForTask_UnderFortyCharacters_IsUnchanged()
    {
        Assert.Equal("86abc: short", TerminalTitle.ForTask("86abc", null, "short"));
    }

    [Fact]
    public void ForTask_TrimsTrailingWhitespaceLeftByTheCut()
    {
        // The 40-char cut lands right after a space; the result must not end mid-space.
        var name = new string('x', 32) + " tail";
        var title = TerminalTitle.ForTask("86abc", null, name);
        Assert.Equal("86abc: " + new string('x', 32), title);
        Assert.False(char.IsWhiteSpace(title[^1]));
    }

    [Fact]
    public void ForTask_RespectsCustomMaxLength()
    {
        Assert.Equal("86abc: Sh", TerminalTitle.ForTask("86abc", null, "Ship it", maxLength: 9));
    }

    [Fact]
    public void ForTask_CollapsesControlCharacters_SoATitleCannotCorruptTheTerminal()
    {
        // A name carrying ESC / BEL / newline / tab (which could break the OSC title escape Terminal.Gui
        // emits from the window Title, or corrupt the frame draw) collapses each control char to a space.
        var title = TerminalTitle.ForTask("86abc", null, "a\u001bb\u0007c\nd\te");
        Assert.Equal("86abc: a b c d e", title);
        Assert.DoesNotContain('\u001b', title);
        Assert.DoesNotContain('\u0007', title);
    }

    [Fact]
    public void ForTask_PassesNonAsciiThrough()
    {
        Assert.Equal("86abc: Café — 日本語", TerminalTitle.ForTask("86abc", null, "Café — 日本語"));
    }

    [Fact]
    public void ForTask_DoesNotSplitASurrogatePairAtTheCut()
    {
        // "86abc: " is 7 UTF-16 units; 32 x's put the 📌 (a surrogate pair) at units 39-40, so a naive
        // 40-unit cut would keep the high surrogate and drop its low half, leaving an invalid string.
        var name = new string('x', 32) + "📌 pinned";
        var title = TerminalTitle.ForTask("86abc", null, name);

        Assert.Equal("86abc: " + new string('x', 32), title);
        // The result is a valid UTF-16 string: no dangling high/low surrogate survived the cut.
        Assert.DoesNotContain(title, c => char.IsSurrogate(c));
        Assert.True(title.Length <= TerminalTitle.MaxLength);
    }

    [Fact]
    public void ForTask_KeepsAnEmojiThatFitsWhollyWithinTheCut()
    {
        // 📌 sits fully inside the 40-unit budget here, so it must survive intact (both surrogate halves).
        var title = TerminalTitle.ForTask("86abc", null, "pin 📌");
        Assert.Equal("86abc: pin 📌", title);
    }

    [Fact]
    public void ForTask_NameOfOnlyControlChars_YieldsIdOnly_NoDanglingColon()
    {
        // Control chars aren't whitespace, so the blank-name check must run *after* sanitize or this
        // would compose "86abc: " and trim to a dangling "86abc:".
        var title = TerminalTitle.ForTask("86abc", null, "\u0007\u001b\u0001");
        Assert.Equal("86abc", title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ForTask_NonPositiveMaxLength_ReturnsEmpty_WithoutThrowing(int maxLength)
    {
        Assert.Equal("", TerminalTitle.ForTask("86abc", null, "Ship it", maxLength));
    }
}
