using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tests for <see cref="CustomFieldActivation"/> (#587 §3): a field type routes to the right gesture
/// (checkbox toggle / text edit / deferred option picker / inert), the checkbox toggle reads its current
/// state across ClickUp's bool/number/string encodings, and the value editor's seed text is sourced
/// round-trippably from the raw JSON (never the truncated display string). No Terminal.Gui, no network.
/// </summary>
public sealed class CustomFieldActivationTests
{
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CustomFieldItem Field(string type, JsonElement? value = null, string id = "f1")
        => new("Field", type, value, Options: null, Id: id);

    // ── Classify ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_Checkbox_IsCheckbox()
        => Assert.Equal(CustomFieldActivationKind.Checkbox, CustomFieldActivation.Classify("checkbox"));

    [Theory]
    [InlineData("text")]
    [InlineData("short_text")]
    [InlineData("url")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("number")]
    [InlineData("currency")]
    [InlineData("date")]
    public void Classify_TextLikeFillableTypes_AreTextEdit(string type)
        => Assert.Equal(CustomFieldActivationKind.TextEdit, CustomFieldActivation.Classify(type));

    [Theory]
    [InlineData("drop_down")]
    [InlineData("labels")]
    public void Classify_OptionTypes_AreDeferred(string type)
        => Assert.Equal(CustomFieldActivationKind.OptionsDeferred, CustomFieldActivation.Classify(type));

    [Theory]
    [InlineData("formula")]
    [InlineData("rollup")]
    [InlineData("users")]
    [InlineData("tasks")]
    [InlineData("location")]
    [InlineData("unknown_type")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_NonFillableOrUnknown_IsNotEditable(string? type)
        => Assert.Equal(CustomFieldActivationKind.NotEditable, CustomFieldActivation.Classify(type));

    [Fact]
    public void Classify_IsCaseInsensitiveAndTrims()
    {
        Assert.Equal(CustomFieldActivationKind.Checkbox, CustomFieldActivation.Classify(" CheckBox "));
        Assert.Equal(CustomFieldActivationKind.OptionsDeferred, CustomFieldActivation.Classify("DROP_DOWN"));
    }

    // ── Checkbox state ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsChecked_NullOrUnset_IsFalse()
    {
        Assert.False(CustomFieldActivation.IsChecked(null));
        Assert.False(CustomFieldActivation.IsChecked(Parse("null")));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"1\"", true)]
    [InlineData("\"0\"", false)]
    [InlineData("\"yes\"", true)]
    [InlineData("\"\"", false)]
    public void IsChecked_ReadsBoolNumberAndStringEncodings(string json, bool expected)
        => Assert.Equal(expected, CustomFieldActivation.IsChecked(Parse(json)));

    [Theory]
    [InlineData("true", false)]
    [InlineData("false", true)]
    public void NextCheckboxState_NegatesCurrent(string json, bool expectedNext)
        => Assert.Equal(expectedNext, CustomFieldActivation.NextCheckboxState(Parse(json)));

    [Fact]
    public void NextCheckboxState_UnsetChecksIt()
        => Assert.True(CustomFieldActivation.NextCheckboxState(null));

    // ── Seed text ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedText_NoValue_IsEmpty()
    {
        Assert.Equal("", CustomFieldActivation.SeedText(Field("text")));
        Assert.Equal("", CustomFieldActivation.SeedText(Field("text", Parse("null"))));
    }

    [Fact]
    public void SeedText_TextScalar_IsVerbatim()
        => Assert.Equal("hello world", CustomFieldActivation.SeedText(Field("text", Json("hello world"))));

    [Fact]
    public void SeedText_Number_IsInvariantString()
    {
        Assert.Equal("42", CustomFieldActivation.SeedText(Field("number", Json(42))));
        Assert.Equal("19.99", CustomFieldActivation.SeedText(Field("currency", Json(19.99))));
        // A number that arrived as a JSON string is preserved verbatim.
        Assert.Equal("7", CustomFieldActivation.SeedText(Field("number", Json("7"))));
    }

    [Fact]
    public void SeedText_Date_IsUtcYyyyMmDd_AndRoundTripsThroughTheParser()
    {
        // 2026-07-15T00:00:00Z = 1_784_073_600_000 ms.
        const long ms = 1_784_073_600_000L;
        var seed = CustomFieldActivation.SeedText(Field("date", Json(ms)));

        Assert.Equal("2026-07-15", seed);
        // The seed the editor shows re-parses to the same epoch, so an unedited save is a no-op.
        Assert.True(TaskFieldInfo.TryParseNumeric(seed, out var reparsed));
        Assert.Equal(ms, reparsed);
    }

    [Fact]
    public void SeedText_DateAsString_IsFormatted()
        => Assert.Equal("2026-07-15", CustomFieldActivation.SeedText(Field("date", Json("1784073600000"))));
}
