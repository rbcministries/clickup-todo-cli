using ClickUpTodo.Agent;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure prompt-template editor logic (#100): seeding, normalization back to a
/// stored value, and the reset-to-default decision. The Terminal.Gui screen is verified separately by
/// build + reasoning per the repo's TUI rule.
/// </summary>
public sealed class PromptTemplateEditorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Seed_Blank_ReturnsDefaultTemplate(string? saved)
        => Assert.Equal(AgentPromptComposer.DefaultTemplate, PromptTemplateEditor.Seed(saved));

    [Fact]
    public void Seed_SavedTemplate_IsReturnedAsIs()
        => Assert.Equal("MY {userPrompt}", PromptTemplateEditor.Seed("MY {userPrompt}"));

    [Fact]
    public void Normalize_TextEqualToDefault_CollapsesToBlank()
        => Assert.Equal("", PromptTemplateEditor.Normalize(AgentPromptComposer.DefaultTemplate));

    [Fact]
    public void Normalize_DefaultWithTrailingWhitespace_CollapsesToBlank()
        => Assert.Equal("", PromptTemplateEditor.Normalize(AgentPromptComposer.DefaultTemplate + "\n\n  "));

    [Fact]
    public void Normalize_FoldsCrLfAndCr_ToLf()
        => Assert.Equal("a\nb\nc", PromptTemplateEditor.Normalize("a\r\nb\rc"));

    [Fact]
    public void Normalize_TrimsTrailingWhitespace_ButKeepsInteriorAndLeading()
        => Assert.Equal("  lead {userPrompt}", PromptTemplateEditor.Normalize("  lead {userPrompt}   \n  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_Blank_ReturnsBlank(string? text)
        => Assert.Equal("", PromptTemplateEditor.Normalize(text));

    [Fact]
    public void Normalize_CustomTemplate_IsPreserved()
        => Assert.Equal("LEAD: {userPrompt}\n{contextJson}",
            PromptTemplateEditor.Normalize("LEAD: {userPrompt}\n{contextJson}"));

    [Fact]
    public void ApplyReset_Confirmed_ReturnsDefaultTemplate()
        => Assert.Equal(AgentPromptComposer.DefaultTemplate, PromptTemplateEditor.ApplyReset(true, "custom edits"));

    [Fact]
    public void ApplyReset_Declined_LeavesCurrentUntouched()
        => Assert.Equal("custom edits", PromptTemplateEditor.ApplyReset(false, "custom edits"));
}
