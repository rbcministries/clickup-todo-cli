using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tests for <see cref="CustomFieldRequiredValidator"/> (#368 §3): a required, fillable field whose
/// id isn't filled is reported (by name); filled ones, optional ones, and required-but-not-fillable
/// (computed) ones are not; order is preserved. Hand-built definitions, no terminal.
/// </summary>
public sealed class CustomFieldRequiredValidatorTests
{
    private static CustomFieldDefinition Field(string id, string type, bool required, string? name = null)
        => new(id, name ?? $"Field {id}", type, required);

    private static ISet<string> Filled(params string[] ids)
        => new HashSet<string>(ids, StringComparer.Ordinal);

    [Fact]
    public void UnfilledRequiredFillableField_IsReportedByName()
    {
        var fields = new[] { Field("a", "text", required: true, name: "Priority reason") };

        var missing = CustomFieldRequiredValidator.MissingRequired(fields, Filled());

        Assert.Equal(["Priority reason"], missing);
    }

    [Fact]
    public void FilledRequiredField_IsNotReported()
    {
        var fields = new[] { Field("a", "text", required: true) };

        var missing = CustomFieldRequiredValidator.MissingRequired(fields, Filled("a"));

        Assert.Empty(missing);
    }

    [Fact]
    public void OptionalField_IsNeverReported_EvenWhenUnfilled()
    {
        var fields = new[] { Field("a", "text", required: false) };

        Assert.Empty(CustomFieldRequiredValidator.MissingRequired(fields, Filled()));
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("rollup")]
    [InlineData("automatic_progress")]
    [InlineData("users")]
    public void RequiredButNonFillableField_IsNotReported(string type)
    {
        // A required computed/relationship field the screen can't fill must not create an unsatisfiable
        // Save block.
        var fields = new[] { Field("a", type, required: true) };

        Assert.Empty(CustomFieldRequiredValidator.MissingRequired(fields, Filled()));
    }

    [Fact]
    public void BlankIdRequiredField_IsSkipped()
    {
        var fields = new[] { Field("  ", "text", required: true) };

        Assert.Empty(CustomFieldRequiredValidator.MissingRequired(fields, Filled()));
    }

    [Fact]
    public void MultipleMissing_PreserveDefinitionOrder_AndSkipFilled()
    {
        var fields = new[]
        {
            Field("a", "text", required: true, name: "Alpha"),
            Field("b", "drop_down", required: true, name: "Beta"),   // filled
            Field("c", "number", required: false, name: "Gamma"),    // optional
            Field("d", "labels", required: true, name: "Delta"),
        };

        var missing = CustomFieldRequiredValidator.MissingRequired(fields, Filled("b"));

        Assert.Equal(["Alpha", "Delta"], missing);
    }
}
