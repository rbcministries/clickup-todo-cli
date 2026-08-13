using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pure tests for <see cref="CustomFieldValueEdit"/> (#587 §3): the optimistic Other-tab mutation replaces
/// exactly one field's value (or clears it), preserves order and every other field, and no-ops on a missing
/// id / empty list. The analogue of <c>ChecklistToggleTests</c> for the custom-field write.
/// </summary>
public sealed class CustomFieldValueEditTests
{
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static CustomFieldItem Field(string id, JsonElement? value = null)
        => new($"Name {id}", "text", value, Options: null, Id: id);

    [Fact]
    public void SetValue_ReplacesOnlyTheMatchingFieldsValue()
    {
        var fields = new[] { Field("a", Json("old-a")), Field("b", Json("old-b")), Field("c") };

        var result = CustomFieldValueEdit.SetValue(fields, "b", Json("new-b"));

        Assert.Equal(3, result.Count);
        Assert.Equal("old-a", result[0].Value!.Value.GetString());
        Assert.Equal("new-b", result[1].Value!.Value.GetString());
        Assert.False(result[2].Value.HasValue);
        // Names/order untouched — only field b's value moved.
        Assert.Equal(new[] { "a", "b", "c" }, result.Select(f => f.Id));
    }

    [Fact]
    public void SetValue_NullClearsTheValue()
    {
        var fields = new[] { Field("a", Json("set")) };

        var result = CustomFieldValueEdit.SetValue(fields, "a", null);

        Assert.False(result[0].Value.HasValue);
    }

    [Fact]
    public void SetValue_MissingId_IsANoOp()
    {
        var fields = new[] { Field("a", Json("keep")) };

        var result = CustomFieldValueEdit.SetValue(fields, "zzz", Json("ignored"));

        Assert.Single(result);
        Assert.Equal("keep", result[0].Value!.Value.GetString());
    }

    [Fact]
    public void SetValue_EmptyList_ReturnsEmpty()
        => Assert.Empty(CustomFieldValueEdit.SetValue([], "a", Json("x")));
}
