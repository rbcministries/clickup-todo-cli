namespace ClickUpTodo.Tui;

/// <summary>
/// The shared <c>"{icon} {name}"</c> text of a Status or Priority badge — <c>○ In Progress</c>,
/// <c>⚑ Urgent</c> — used by both the main-list <see cref="Configuration.BadgeDisplay.Text"/> badges
/// (<see cref="TaskRowFormatter.AppendTextBadge"/>) and the task detail title line
/// (<see cref="TaskDetailFormatter.HeaderLines"/>, drawn by <see cref="DetailAttributesView"/>), so the
/// two surfaces render an identical label (#162). Pure text with no Terminal.Gui dependency: the field
/// colour is applied by each surface's renderer (<see cref="StatusBadgeListSource"/> /
/// <see cref="DetailAttributesView"/>), keyed off <see cref="StatusBadgeColor"/>. The glyphs are the
/// single source shared with the icon-mode chips (<see cref="TaskRowFormatter.StatusIcon"/> /
/// <see cref="TaskRowFormatter.PriorityIcon"/>).
/// </summary>
public static class StatusPriorityBadge
{
    /// <summary>The Status glyph — a hollow circle, a single display column.</summary>
    public const string StatusGlyph = "○";

    /// <summary>The Priority glyph — a flag, a single display column.</summary>
    public const string PriorityGlyph = "⚑";

    /// <summary>The Status badge label, e.g. <c>○ In Progress</c>.</summary>
    public static string Status(string name) => $"{StatusGlyph} {name}";

    /// <summary>The Priority badge label, e.g. <c>⚑ Urgent</c>.</summary>
    public static string Priority(string name) => $"{PriorityGlyph} {name}";
}
