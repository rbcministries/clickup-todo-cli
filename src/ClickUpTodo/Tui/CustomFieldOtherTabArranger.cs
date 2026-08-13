using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>The kinds of row the Task Detail <b>Other</b> tab renders once its custom-fields body is a
/// navigable row model (#587 §2) rather than one opaque <see cref="Terminal.Gui.Views.TextView"/> blob.
/// Only a <see cref="Field"/> row that is <see cref="CustomFieldOtherRow.Fillable"/> is a selection /
/// activation target; everything else is inert scenery the selection skips over.</summary>
public enum CustomFieldOtherRowKind
{
    /// <summary>A coloured-header line the #81 short-terminal split pushed into the body — non-selectable,
    /// rendered as plain text at the top of the scrollable body so it stays reachable.</summary>
    Spill,

    /// <summary>The <c>Custom fields:</c> section heading — non-selectable.</summary>
    SectionLabel,

    /// <summary>The <c>(none)</c> empty-state line shown when the task has no custom fields — non-selectable.</summary>
    EmptyState,

    /// <summary>One custom field. Selectable (and, in §3, activatable) only when
    /// <see cref="CustomFieldOtherRow.Fillable"/>; a computed / relationship type renders but stays inert.</summary>
    Field,
}

/// <summary>
/// One projected row of the Other tab's navigable custom-fields body (#587 §2). Pure data: the arranger
/// decides the ordering and per-row text; the Terminal.Gui glue renders it and moves the selection over
/// the <see cref="Selectable"/> rows only. A <see cref="Field"/> row carries its <see cref="FieldId"/>,
/// <see cref="FieldName"/>, <see cref="FieldType"/> and current <see cref="Value"/> text so the §3
/// activation path never has to re-walk <see cref="TaskDetail.CustomFields"/> from a selected index.
/// </summary>
public readonly record struct CustomFieldOtherRow(
    CustomFieldOtherRowKind Kind,
    string Text,
    string? FieldId,
    string? FieldName,
    string? FieldType,
    bool Fillable,
    string? Value)
{
    /// <summary><c>true</c> for a custom-field row (whether or not it is fillable).</summary>
    public bool IsField => Kind == CustomFieldOtherRowKind.Field;

    /// <summary>Whether ↑/↓ may land on this row and §3 may activate it: a fillable custom field only.
    /// Spill / heading / empty-state rows and computed or relationship field types are skipped and inert.</summary>
    public bool Selectable => Kind == CustomFieldOtherRowKind.Field && Fillable;
}

/// <summary>The result of <see cref="CustomFieldOtherTabArranger.Project"/>: the flat, display-ordered
/// <see cref="Rows"/> plus counts the glue uses. <see cref="FieldCount"/> is every custom field (fillable
/// or not); <see cref="SelectableCount"/> is how many rows the selection can land on (zero ⇒ the tab is
/// read-only in practice even though it lists fields).</summary>
public readonly record struct CustomFieldOtherProjection(
    IReadOnlyList<CustomFieldOtherRow> Rows,
    int FieldCount,
    int SelectableCount)
{
    /// <summary>The task lists no custom fields at all (the <c>(none)</c> empty-state row is shown).</summary>
    public bool HasFields => FieldCount > 0;

    /// <summary>Whether any row is a selection / activation target.</summary>
    public bool HasSelectableRows => SelectableCount > 0;

    /// <summary>Index of the first <see cref="CustomFieldOtherRow.Selectable"/> row, or -1 when none —
    /// the glue's initial selection when the tab is entered.</summary>
    public int FirstSelectableIndex()
    {
        for (var i = 0; i < Rows.Count; i++)
            if (Rows[i].Selectable)
                return i;
        return -1;
    }
}

/// <summary>
/// Projects a task's custom fields (<see cref="TaskDetail.CustomFields"/>) plus the #81 spilled header
/// lines into one ordered row list for the Other tab's navigable body (#587 §2). Pure (no Terminal.Gui,
/// no I/O) so the ordering / selectability / short-terminal rules are unit-testable — the same arranger
/// shape as <see cref="Services.ChecklistArranger"/>; the tab view is thin glue over it.
/// <para>
/// The row text reuses <see cref="TaskDetailFormatter.CustomFieldLine"/> — the same source the read-only
/// <see cref="TaskDetailFormatter.CustomFieldsBody"/> blob renders from — so a field's line can't drift
/// between the plain read view and the row model. Field order is preserved as ClickUp returned it (the
/// read blob's order), so switching to a row model never reshuffles the tab.
/// </para>
/// </summary>
public static class CustomFieldOtherTabArranger
{
    /// <summary>Projects <paramref name="fields"/> (with the #81 <paramref name="spilledHeaderLines"/>
    /// prepended as non-selectable rows) into the Other tab's display rows. A null/empty field list
    /// yields the heading plus a single <see cref="CustomFieldOtherRowKind.EmptyState"/> row.</summary>
    public static CustomFieldOtherProjection Project(
        IReadOnlyList<string>? spilledHeaderLines,
        IReadOnlyList<CustomFieldItem>? fields)
    {
        var rows = new List<CustomFieldOtherRow>();

        // #81: coloured-header lines the short-terminal split clipped render as leading, non-selectable
        // body rows (in order) so every attribute stays reachable by scrolling — the row-model analogue
        // of DetailOtherTabView.BuildSpilledBody's plain-text prefix.
        if (spilledHeaderLines is not null)
            foreach (var line in spilledHeaderLines)
                rows.Add(NonSelectable(CustomFieldOtherRowKind.Spill, line));

        rows.Add(NonSelectable(CustomFieldOtherRowKind.SectionLabel, TaskDetailFormatter.CustomFieldsHeading));

        var fieldCount = 0;
        var selectableCount = 0;
        if (fields is null || fields.Count == 0)
        {
            rows.Add(NonSelectable(CustomFieldOtherRowKind.EmptyState, TaskDetailFormatter.CustomFieldsEmptyLine));
        }
        else
        {
            foreach (var f in fields)
            {
                fieldCount++;
                var fillable = CustomFieldTypes.IsFillable(f.Type);
                if (fillable)
                    selectableCount++;
                rows.Add(new CustomFieldOtherRow(
                    Kind: CustomFieldOtherRowKind.Field,
                    Text: TaskDetailFormatter.CustomFieldLine(f),
                    FieldId: f.Id,
                    FieldName: f.Name,
                    FieldType: f.Type,
                    Fillable: fillable,
                    Value: TaskDetailFormatter.CustomFieldValue(f)));
            }
        }

        return new CustomFieldOtherProjection(rows, fieldCount, selectableCount);
    }

    private static CustomFieldOtherRow NonSelectable(CustomFieldOtherRowKind kind, string text)
        => new(kind, text, FieldId: null, FieldName: null, FieldType: null, Fillable: false, Value: null);
}
