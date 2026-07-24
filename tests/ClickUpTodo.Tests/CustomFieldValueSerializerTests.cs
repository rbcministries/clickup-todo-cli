using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tests for <see cref="CustomFieldValueSerializer"/> (#368 §1): each fillable field type maps an
/// entered value into the create-payload shape ClickUp's <c>custom_fields</c> array expects, blank/absent
/// input skips, unparseable numeric/date input errors, and non-fillable types skip. Hand-built inputs, no
/// Kiota type, no network.
/// </summary>
public sealed class CustomFieldValueSerializerTests
{
    private static CustomFieldDefinition Field(string type, string id = "f1", bool required = false,
        params (string Id, string Name)[] options)
        => new(id, "Field", type, required,
            options.Select(o => new CustomFieldOption(o.Id, o.Name, null)).ToList());

    [Theory]
    [InlineData("text")]
    [InlineData("short_text")]
    [InlineData("url")]
    [InlineData("email")]
    [InlineData("phone")]
    public void TextTypes_ProduceAStringValue(string type)
    {
        var result = CustomFieldValueSerializer.Build(Field(type), new CustomFieldEntry { Text = "hello " });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal("f1", result.Value!.Id);
        Assert.Equal(JsonValueKind.String, result.Value.Value.ValueKind);
        Assert.Equal("hello", result.Value.Value.GetString()); // trimmed
    }

    [Fact]
    public void Number_Integer_StaysAnInteger()
    {
        var result = CustomFieldValueSerializer.Build(Field("number"), new CustomFieldEntry { Text = "42" });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal(JsonValueKind.Number, result.Value!.Value.ValueKind);
        Assert.Equal(42L, result.Value.Value.GetInt64());
        Assert.Equal("42", result.Value.Value.GetRawText()); // no spurious "42.0"
    }

    [Fact]
    public void Currency_Decimal_ProducesADouble()
    {
        var result = CustomFieldValueSerializer.Build(Field("currency"), new CustomFieldEntry { Text = "19.99" });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal(JsonValueKind.Number, result.Value!.Value.ValueKind);
        Assert.Equal(19.99, result.Value.Value.GetDouble(), 3);
    }

    [Fact]
    public void Number_NonNumeric_IsAnError()
    {
        var result = CustomFieldValueSerializer.Build(Field("number"), new CustomFieldEntry { Text = "abc" });

        Assert.Equal(CustomFieldWriteOutcome.Error, result.Outcome);
        Assert.Contains("number", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")] // overflows a double to +Infinity
    public void Number_NonFinite_IsAnError_NotAThrow(string text)
    {
        // double.TryParse accepts these, but JsonSerializer can't emit NaN/Infinity — they must degrade to
        // a graceful validation error rather than throwing at the Save path.
        var result = CustomFieldValueSerializer.Build(Field("number"), new CustomFieldEntry { Text = text });

        Assert.Equal(CustomFieldWriteOutcome.Error, result.Outcome);
        Assert.Contains("number", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Checkbox_ExplicitChecked_Wins()
    {
        var result = CustomFieldValueSerializer.Build(Field("checkbox"), new CustomFieldEntry { Checked = true });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal(JsonValueKind.True, result.Value!.Value.ValueKind);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void Checkbox_TextFallback_Parses(string text, bool expected)
    {
        var result = CustomFieldValueSerializer.Build(Field("checkbox"), new CustomFieldEntry { Text = text });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal(expected, result.Value!.Value.GetBoolean());
    }

    [Fact]
    public void Checkbox_UnparseableText_IsAnError()
    {
        var result = CustomFieldValueSerializer.Build(Field("checkbox"), new CustomFieldEntry { Text = "maybe" });

        Assert.Equal(CustomFieldWriteOutcome.Error, result.Outcome);
    }

    [Fact]
    public void Date_ParsesToEpochMs()
    {
        var result = CustomFieldValueSerializer.Build(Field("date"), new CustomFieldEntry { Text = "2026-07-15" });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal(JsonValueKind.Number, result.Value!.Value.ValueKind);
        // 2026-07-15T00:00:00Z as Unix epoch milliseconds.
        var expected = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(expected, result.Value.Value.GetInt64());
    }

    [Fact]
    public void Date_Unparseable_IsAnError()
    {
        var result = CustomFieldValueSerializer.Build(Field("date"), new CustomFieldEntry { Text = "not-a-date" });

        Assert.Equal(CustomFieldWriteOutcome.Error, result.Outcome);
    }

    [Fact]
    public void DropDown_UsesFirstSelectedOptionId()
    {
        var field = Field("drop_down", options: [("opt-a", "A"), ("opt-b", "B")]);
        var result = CustomFieldValueSerializer.Build(field,
            new CustomFieldEntry { SelectedOptionIds = ["opt-b", "opt-a"] });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal(JsonValueKind.String, result.Value!.Value.ValueKind);
        Assert.Equal("opt-b", result.Value.Value.GetString());
    }

    [Fact]
    public void Labels_ProduceAnArrayOfOptionIds()
    {
        var result = CustomFieldValueSerializer.Build(Field("labels"),
            new CustomFieldEntry { SelectedOptionIds = ["l1", "", "  ", "l2"] });

        Assert.Equal(CustomFieldWriteOutcome.Value, result.Outcome);
        Assert.Equal(JsonValueKind.Array, result.Value!.Value.ValueKind);
        Assert.Equal(["l1", "l2"], result.Value.Value.EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("text", "")]
    [InlineData("text", "   ")]
    [InlineData("number", "")]
    [InlineData("date", "")]
    public void BlankTextInput_Skips(string type, string text)
    {
        var result = CustomFieldValueSerializer.Build(Field(type), new CustomFieldEntry { Text = text });

        Assert.Equal(CustomFieldWriteOutcome.Skip, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public void DropDown_NoSelection_Skips()
    {
        var result = CustomFieldValueSerializer.Build(Field("drop_down"),
            new CustomFieldEntry { SelectedOptionIds = ["", "   "] });

        Assert.Equal(CustomFieldWriteOutcome.Skip, result.Outcome);
    }

    [Fact]
    public void Checkbox_NoInput_Skips()
    {
        var result = CustomFieldValueSerializer.Build(Field("checkbox"), new CustomFieldEntry());

        Assert.Equal(CustomFieldWriteOutcome.Skip, result.Outcome);
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("rollup")]
    [InlineData("users")]
    [InlineData("tasks")]
    [InlineData("automatic_progress")]
    [InlineData("signature")]
    [InlineData("emoji")]
    [InlineData("some_future_type")]
    public void NonFillableTypes_Skip_EvenWithInput(string type)
    {
        var result = CustomFieldValueSerializer.Build(Field(type),
            new CustomFieldEntry { Text = "x", Checked = true, SelectedOptionIds = ["o1"] });

        Assert.Equal(CustomFieldWriteOutcome.Skip, result.Outcome);
    }

    [Fact]
    public void BlankFieldId_Skips()
    {
        var result = CustomFieldValueSerializer.Build(Field("text", id: "  "),
            new CustomFieldEntry { Text = "value" });

        Assert.Equal(CustomFieldWriteOutcome.Skip, result.Outcome);
    }
}
