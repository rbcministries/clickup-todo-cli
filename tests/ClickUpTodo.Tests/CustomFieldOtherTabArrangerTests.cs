using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="CustomFieldOtherTabArranger.Project"/> — the pure row projection behind the
/// Task Detail Other tab's navigable custom-fields body (#587 §2): the heading and #81 spilled header
/// lines stay non-selectable, fillable field types are selectable while computed/relationship types
/// render but stay inert, the empty task shows a non-selectable empty-state row, field order is
/// preserved, and each field row's text matches the read-only <see cref="TaskDetailFormatter"/> blob so
/// the row model can't drift from it.
/// </summary>
public sealed class CustomFieldOtherTabArrangerTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CustomFieldItem Field(string name, string? type, string? valueJson = null, string? id = "f") =>
        new(name, type, valueJson is null ? null : Json(valueJson), Id: id);

    [Fact]
    public void NoFields_YieldsHeadingAndEmptyState_BothNonSelectable()
    {
        var p = CustomFieldOtherTabArranger.Project(spilledHeaderLines: null, fields: null);

        Assert.False(p.HasFields);
        Assert.False(p.HasSelectableRows);
        Assert.Equal(0, p.FieldCount);
        Assert.Equal(0, p.SelectableCount);
        Assert.Equal(-1, p.FirstSelectableIndex());

        Assert.Collection(p.Rows,
            r =>
            {
                Assert.Equal(CustomFieldOtherRowKind.SectionLabel, r.Kind);
                Assert.Equal(TaskDetailFormatter.CustomFieldsHeading, r.Text);
                Assert.False(r.Selectable);
            },
            r =>
            {
                Assert.Equal(CustomFieldOtherRowKind.EmptyState, r.Kind);
                Assert.Equal(TaskDetailFormatter.CustomFieldsEmptyLine, r.Text);
                Assert.False(r.Selectable);
            });
    }

    [Fact]
    public void EmptyFieldList_TreatedSameAsNull()
    {
        var p = CustomFieldOtherTabArranger.Project(null, []);

        Assert.False(p.HasFields);
        Assert.Equal(2, p.Rows.Count);
        Assert.Equal(CustomFieldOtherRowKind.EmptyState, p.Rows[^1].Kind);
    }

    [Fact]
    public void SpilledHeaderLines_BecomeLeadingNonSelectableRows_InOrder()
    {
        var spill = new[] { "Created:       2026-08-13", "Last activity: 2026-08-13", "Due:           —" };

        var p = CustomFieldOtherTabArranger.Project(spill, [Field("Points", "number", "5")]);

        // The three spill lines lead, in order, then the heading, then the field row.
        Assert.Equal(CustomFieldOtherRowKind.Spill, p.Rows[0].Kind);
        Assert.Equal(spill[0], p.Rows[0].Text);
        Assert.Equal(spill[1], p.Rows[1].Text);
        Assert.Equal(spill[2], p.Rows[2].Text);
        Assert.All(p.Rows.Take(3), r => Assert.False(r.Selectable));
        Assert.Equal(CustomFieldOtherRowKind.SectionLabel, p.Rows[3].Kind);
        Assert.Equal(CustomFieldOtherRowKind.Field, p.Rows[4].Kind);
        // Spill rows don't count as fields.
        Assert.Equal(1, p.FieldCount);
    }

    [Fact]
    public void FillableField_IsSelectable_AndCarriesItsIdentity()
    {
        var p = CustomFieldOtherTabArranger.Project(null, [Field("Points", "number", "5", id: "abc")]);

        var row = Assert.Single(p.Rows, r => r.IsField);
        Assert.True(row.Selectable);
        Assert.Equal("abc", row.FieldId);
        Assert.Equal("Points", row.FieldName);
        Assert.Equal("number", row.FieldType);
        Assert.True(row.Fillable);
        Assert.Equal("5", row.Value);
        Assert.Equal(1, p.SelectableCount);
        Assert.True(p.Rows[p.FirstSelectableIndex()].Selectable);
        Assert.Equal("Points", p.Rows[p.FirstSelectableIndex()].FieldName);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("short_text")]
    [InlineData("url")]
    [InlineData("email")]
    [InlineData("phone")]
    [InlineData("number")]
    [InlineData("currency")]
    [InlineData("checkbox")]
    [InlineData("date")]
    [InlineData("drop_down")]
    [InlineData("labels")]
    public void EveryFillableType_IsSelectable(string type)
    {
        var p = CustomFieldOtherTabArranger.Project(null, [Field("F", type)]);

        Assert.True(Assert.Single(p.Rows, r => r.IsField).Selectable);
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("rollup")]
    [InlineData("users")]
    [InlineData("tasks")]
    [InlineData("location")]
    [InlineData("emoji")]
    [InlineData("manual_progress")]
    [InlineData(null)]
    [InlineData("")]
    public void ComputedOrRelationshipType_RendersButIsNotSelectable(string? type)
    {
        var p = CustomFieldOtherTabArranger.Project(null, [Field("F", type)]);

        var row = Assert.Single(p.Rows, r => r.IsField);
        Assert.False(row.Selectable);
        Assert.False(row.Fillable);
        Assert.Equal(1, p.FieldCount);          // still counted + rendered…
        Assert.Equal(0, p.SelectableCount);      // …just not a selection target.
        Assert.False(p.HasSelectableRows);
        Assert.Equal(-1, p.FirstSelectableIndex());
    }

    [Fact]
    public void Fields_KeepInputOrder_AndFirstSelectableSkipsInertRows()
    {
        var fields = new[]
        {
            Field("Computed", "formula", id: "c"),   // inert
            Field("Name",     "text",    "\"hi\"", id: "n"),  // selectable
            Field("Flag",     "checkbox", "true",  id: "b"),  // selectable
        };

        var p = CustomFieldOtherTabArranger.Project(null, fields);

        var fieldRows = p.Rows.Where(r => r.IsField).ToList();
        Assert.Equal(new[] { "Computed", "Name", "Flag" }, fieldRows.Select(r => r.FieldName));
        Assert.Equal(3, p.FieldCount);
        Assert.Equal(2, p.SelectableCount);
        // First selectable is the "Name" row, not the leading inert "Computed" row.
        Assert.Equal("Name", p.Rows[p.FirstSelectableIndex()].FieldName);
    }

    [Fact]
    public void FieldRowText_MatchesTheReadOnlyBlobLine_NoDrift()
    {
        // The row text must equal what the read-only CustomFieldsBody renders for the same field, so the
        // navigable row model and the plain blob can't diverge.
        var field = Field("Priority Score", "number", "42", id: "f1");

        var p = CustomFieldOtherTabArranger.Project(null, [field]);

        var row = Assert.Single(p.Rows, r => r.IsField);
        Assert.Equal(TaskDetailFormatter.CustomFieldLine(field), row.Text);
        Assert.Equal("  • Priority Score  (number): 42", row.Text);
    }

    [Fact]
    public void FieldRows_ConcatenateToTheReadOnlyBody()
    {
        // A stronger anti-drift pin: the heading + every field line, joined, reproduce CustomFieldsBody.
        var fields = new[]
        {
            Field("Name", "text", "\"hi\"", id: "n"),
            Field("Score", "number", "7", id: "s"),
            Field("Done", "checkbox", "true", id: "d"),
        };
        var task = new TaskDetail { Id = "id", Name = "Title", CustomFields = fields };

        var p = CustomFieldOtherTabArranger.Project(null, fields);

        var rebuilt = string.Join('\n', p.Rows
            .Where(r => r.Kind is CustomFieldOtherRowKind.SectionLabel or CustomFieldOtherRowKind.Field)
            .Select(r => r.Text));
        Assert.Equal(TaskDetailFormatter.CustomFieldsBody(task), rebuilt);
    }
}
