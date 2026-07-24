using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace ClickUpTodo.Tests;

/// <summary>
/// The CI-testable half of the exit-confirmation modal (#299, multi-tab epic #292 sub-issue 7): the pure
/// answer routing in <see cref="ExitConfirmModel"/> and the screen's key classification. The Terminal.Gui
/// view is never instantiated (the suite never calls <c>Application.Init</c>), matching the repo's pattern
/// of asserting only the framework-free logic of a screen — <see cref="ExitConfirmScreen.Classify"/> is
/// static for exactly that reason, and keys are built with <see cref="Key.TryParse"/>, the same path a
/// real press and a footer click converge on.
/// </summary>
public sealed class ExitConfirmTests
{
    private static Key Parse(string token)
    {
        Assert.True(Key.TryParse(token, out var key), $"'{token}' should parse");
        return key;
    }

    // ── ExitConfirmModel: what an answer means ───────────────────────────────

    [Theory]
    [InlineData(ExitConfirmModel.ConfirmKey.Yes, ExitConfirmModel.ConfirmAction.Exit)]
    [InlineData(ExitConfirmModel.ConfirmKey.No, ExitConfirmModel.ConfirmAction.Cancel)]
    public void Route_MapsAnAnswerToItsAction(
        ExitConfirmModel.ConfirmKey key, ExitConfirmModel.ConfirmAction expected)
        => Assert.Equal(expected, ExitConfirmModel.Route(key));

    // The safety property: a key that isn't an answer keeps the question up. Never Exit (a stray press
    // must not quit) and never Cancel (a mistyped keystroke silently dismissing would look like the app
    // had ignored the quit).
    [Fact]
    public void Route_IgnoresAnythingThatIsNotAnAnswer()
        => Assert.Equal(ExitConfirmModel.ConfirmAction.Ignore, ExitConfirmModel.Route(ExitConfirmModel.ConfirmKey.Other));

    [Fact]
    public void Prompt_AsksAboutExiting()
    {
        Assert.EndsWith("?", ExitConfirmModel.Prompt, StringComparison.Ordinal);
        Assert.Contains("exit", ExitConfirmModel.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnswerHint_SpellsOutBothAnswers()
    {
        var hint = ExitConfirmScreen.AnswerHint;

        Assert.Contains("Y", hint, StringComparison.Ordinal);
        Assert.Contains("N", hint, StringComparison.Ordinal);
        Assert.Contains("Esc", hint, StringComparison.Ordinal);
    }

    // ── ExitConfirmScreen.Classify: key → answer ─────────────────────────────

    // Y and Enter confirm; N and Esc decline — in *either* case for the letters. Both the table-bound
    // keys (Y / Esc) and the undisplayed aliases (Enter / N) are pinned here, so an alias can't quietly
    // stop working. The lowercase rows are not redundant: Terminal.Gui gives a plain `y` press a bare
    // KeyCode.Y while `Key.TryParse("Y")` yields KeyCode.Y|ShiftMask (pinned below), so without them the
    // most common physical keypress — an unshifted letter — would go unasserted.
    [Theory]
    [InlineData("Y", ExitConfirmModel.ConfirmKey.Yes)]
    [InlineData("y", ExitConfirmModel.ConfirmKey.Yes)]
    [InlineData("Enter", ExitConfirmModel.ConfirmKey.Yes)]
    [InlineData("N", ExitConfirmModel.ConfirmKey.No)]
    [InlineData("n", ExitConfirmModel.ConfirmKey.No)]
    [InlineData("Esc", ExitConfirmModel.ConfirmKey.No)]
    public void Classify_RecognizesBothAnswersAndTheirAliases(string token, ExitConfirmModel.ConfirmKey expected)
        => Assert.Equal(expected, ExitConfirmScreen.Classify(Parse(token)));

    // The Terminal.Gui encoding the classifier is written against, pinned so the two cases above can't
    // silently collapse into one input again: a parsed uppercase letter carries ShiftMask, lowercase does
    // not. This is also why the screen classifies by hand rather than through KeybindingDispatcher, which
    // matches the table token's exact KeyCode ("Y" → the shifted one) and would miss a plain `y`.
    [Fact]
    public void ParsedLetters_DifferByShiftMask_WhichIsWhyBothCasesAreTested()
    {
        Assert.Equal(KeyCode.Y | KeyCode.ShiftMask, Parse("Y").KeyCode);
        Assert.Equal(KeyCode.Y, Parse("y").KeyCode);
        Assert.NotEqual(Parse("y").KeyCode, Parse("Y").KeyCode);
    }

    // Shift is stripped, so a shifted answer still answers (the repo's existing Y/N confirm idiom).
    [Theory]
    [InlineData("Shift+Y", ExitConfirmModel.ConfirmKey.Yes)]
    [InlineData("Shift+N", ExitConfirmModel.ConfirmKey.No)]
    public void Classify_TolerantOfShift(string token, ExitConfirmModel.ConfirmKey expected)
        => Assert.Equal(expected, ExitConfirmScreen.Classify(Parse(token)));

    // An arbitrary Ctrl/Alt chord is never an answer: a half-remembered chord must not read as
    // "yes, exit" — nor as a "no" that dismisses the question the user asked for.
    [Theory]
    [InlineData("Ctrl+Y")]
    [InlineData("Ctrl+N")]
    [InlineData("Ctrl+E")]
    [InlineData("Alt+Y")]
    [InlineData("Alt+N")]
    public void Classify_NeverReadsAnArbitraryChordAsAnAnswer(string token)
        => Assert.Equal(ExitConfirmModel.ConfirmKey.Other, ExitConfirmScreen.Classify(Parse(token)));

    // The app's own quit chords are the exception — pressing the key that raised the question again is an
    // unambiguous second "yes", and swallowing them would leave both the quit command and the terminal's
    // conventional interrupt dead while a modal asks a question.
    [Theory]
    [InlineData("Ctrl+Q")]  // Keybindings[MainList, Quit]
    [InlineData("Ctrl+C")]  // its undisplayed alias in TodoApp.OnListKey
    public void Classify_TreatsTheQuitChordsAsAConfirmingSecondPress(string token)
        => Assert.Equal(ExitConfirmModel.ConfirmKey.Yes, ExitConfirmScreen.Classify(Parse(token)));

    // Anti-drift: if the quit key is ever rebound in the central table, the classifier above must follow.
    [Fact]
    public void Classify_ConfirmsWhicheverKeyTheTableBindsToQuit()
    {
        var quit = Parse(Keybindings.Token(ScreenContext.MainList, KeyAction.Quit));

        Assert.Equal(ExitConfirmModel.ConfirmKey.Yes, ExitConfirmScreen.Classify(quit));
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("Space")]
    [InlineData("A")]
    [InlineData("Tab")]
    public void Classify_LeavesEveryOtherKeyUnanswered(string token)
        => Assert.Equal(ExitConfirmModel.ConfirmKey.Other, ExitConfirmScreen.Classify(Parse(token)));

    // ── The footer set (#103/#355) ───────────────────────────────────────────

    [Fact]
    public void Footer_RendersBothAnswers()
        => Assert.Equal("Y/↩ yes, exit · Esc/N no, stay", HelpLine.Format(HelpItemSets.ExitConfirm));

    // The modal is the same in both launch modes because both hosts render this one set; that consistency
    // is an acceptance criterion of #299, so pin the keys it advertises.
    [Fact]
    public void Footer_AdvertisesTheTableKeys()
    {
        Assert.Equal("Y", Keybindings.Token(ScreenContext.ExitConfirm, KeyAction.Confirm));
        Assert.Equal("Esc", Keybindings.Token(ScreenContext.ExitConfirm, KeyAction.Back));
        Assert.Equal(
            ["Y", "Esc"],
            HelpItemSets.ExitConfirm.Select(i => i.ActionKey));
    }

    // It is the question, not a screen with help: F1 would stack Help over a yes/no.
    [Fact]
    public void Footer_DoesNotOfferHelp()
    {
        Assert.DoesNotContain(HelpItemSets.ExitConfirm, i => i.Key == "F1");
        Assert.False(Keybindings.TryToken(ScreenContext.ExitConfirm, KeyAction.Help, out _));
    }
}
