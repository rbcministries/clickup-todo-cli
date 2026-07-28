using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tests for <see cref="NewTaskCustomFieldForm.Collect"/> (#395 §2): the New Task screen's
/// custom-field aggregation over the #368 foundation — produced values, still-missing required fields,
/// and per-field parse errors — decided without a terminal from hand-built definitions + entries.
/// </summary>
public sealed class NewTaskCustomFieldFormTests
{
    private static CustomFieldDefinition Field(
        string type, string id, string name, bool required = false,
        params (string Id, string Name)[] options)
        => new(id, name, type, required,
            options.Select(o => new CustomFieldOption(o.Id, o.Name, null)).ToList());

    private static NewTaskCustomFieldResult Collect(
        IReadOnlyList<CustomFieldDefinition> fields,
        params (string Id, CustomFieldEntry Entry)[] entries)
        => NewTaskCustomFieldForm.Collect(fields, entries.ToDictionary(e => e.Id, e => e.Entry));

    [Fact]
    public void NoFields_IsValid_Empty()
    {
        var result = Collect([]);

        Assert.True(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Empty(result.MissingRequired);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FilledTextField_ProducesAValue()
    {
        var fields = new[] { Field("text", "f1", "Notes") };

        var result = Collect(fields, ("f1", new CustomFieldEntry { Text = "hello" }));

        Assert.True(result.IsValid);
        Assert.Single(result.Values);
        Assert.Equal("f1", result.Values[0].Id);
        Assert.Equal("hello", result.Values[0].Value.GetString());
    }

    [Fact]
    public void RequiredField_LeftBlank_IsMissing_BlocksSave()
    {
        var fields = new[] { Field("text", "f1", "Notes", required: true) };

        var result = Collect(fields); // no entry supplied → treated as empty

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Equal(["Notes"], result.MissingRequired);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void RequiredField_Filled_IsValid()
    {
        var fields = new[] { Field("text", "f1", "Notes", required: true) };

        var result = Collect(fields, ("f1", new CustomFieldEntry { Text = "done" }));

        Assert.True(result.IsValid);
        Assert.Single(result.Values);
        Assert.Empty(result.MissingRequired);
    }

    [Fact]
    public void InvalidNumber_IsAnError_BlocksSave()
    {
        var fields = new[] { Field("number", "f1", "Estimate") };

        var result = Collect(fields, ("f1", new CustomFieldEntry { Text = "abc" }));

        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Single(result.Errors);
        Assert.Contains("Estimate", result.Errors[0]);
    }

    [Fact]
    public void RequiredInvalidNumber_ReportsErrorAndMissing()
    {
        // An unparseable value doesn't fill the field, so a required invalid entry both errors and is
        // reported missing; the screen surfaces the (more specific) error first.
        var fields = new[] { Field("number", "f1", "Estimate", required: true) };

        var result = Collect(fields, ("f1", new CustomFieldEntry { Text = "abc" }));

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal(["Estimate"], result.MissingRequired);
    }

    [Fact]
    public void NonFillableRequiredType_NeverBlocks()
    {
        // A required computed/relationship field the UI can't fill must not create an unsatisfiable Save.
        var fields = new[] { Field("formula", "f1", "Computed", required: true) };

        var result = Collect(fields);

        Assert.True(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Empty(result.MissingRequired);
    }

    [Fact]
    public void DropDown_SelectedOption_ProducesTheOptionId()
    {
        var fields = new[] { Field("drop_down", "f1", "Stage", options: [("o1", "Alpha"), ("o2", "Beta")]) };

        var result = Collect(fields, ("f1", new CustomFieldEntry { SelectedOptionIds = ["o2"] }));

        Assert.True(result.IsValid);
        Assert.Single(result.Values);
        Assert.Equal("o2", result.Values[0].Value.GetString());
    }

    [Fact]
    public void Checkbox_ExplicitState_ProducesABool()
    {
        var fields = new[] { Field("checkbox", "f1", "Done") };

        var result = Collect(fields, ("f1", new CustomFieldEntry { Checked = true }));

        Assert.True(result.IsValid);
        Assert.Equal(JsonValueKind.True, result.Values[0].Value.ValueKind);
    }

    [Fact]
    public void Values_PreserveFieldOrder()
    {
        var fields = new[]
        {
            Field("text", "a", "A"),
            Field("text", "b", "B"),
            Field("text", "c", "C"),
        };

        var result = Collect(fields,
            ("c", new CustomFieldEntry { Text = "3" }),
            ("a", new CustomFieldEntry { Text = "1" }),
            ("b", new CustomFieldEntry { Text = "2" }));

        Assert.Equal(["a", "b", "c"], result.Values.Select(v => v.Id));
    }

    [Fact]
    public void BlankValues_AreSkipped_NotSent()
    {
        var fields = new[] { Field("text", "f1", "Notes"), Field("number", "f2", "N") };

        var result = Collect(fields,
            ("f1", new CustomFieldEntry { Text = "   " }),
            ("f2", new CustomFieldEntry { Text = "" }));

        Assert.True(result.IsValid);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void MixedFields_CollectValueErrorAndMissing_Independently()
    {
        var fields = new[]
        {
            Field("text", "ok", "Notes"),
            Field("number", "bad", "Estimate"),
            Field("text", "req", "Owner", required: true),
        };

        var result = Collect(fields,
            ("ok", new CustomFieldEntry { Text = "value" }),
            ("bad", new CustomFieldEntry { Text = "xyz" }));

        Assert.False(result.IsValid);
        Assert.Equal(["ok"], result.Values.Select(v => v.Id));
        Assert.Single(result.Errors);
        Assert.Equal(["Owner"], result.MissingRequired);
    }

    [Fact]
    public void BlankId_Field_IsIgnored_NeverMissing()
    {
        // A definition with a blank id can't be written back, so the serializer/validator skip it — it must
        // not surface as missing even when required.
        var fields = new[] { Field("text", "  ", "Ghost", required: true) };

        var result = Collect(fields);

        Assert.True(result.IsValid);
        Assert.Empty(result.MissingRequired);
    }
}
