namespace ClickUpTodo.Configuration;

/// <summary>
/// How the task list renders a row's Status and Priority badges. Cycled by F6 in the order the
/// values are declared (Icons → Text → Hidden → Icons). Persisted in <c>config.json</c> as a string
/// (via <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>) so the choice survives
/// restarts.
/// </summary>
public enum BadgeDisplay
{
    /// <summary>Compact single-glyph chips — <c>○</c> (status) and <c>⚑</c> (priority) — each tinted
    /// with its field's colour and laid out as a fixed-width left gutter.</summary>
    Icons,

    /// <summary>Bracketed text badges — <c>[status]</c> and <c>[priority]</c> — each tinted with its
    /// field's colour.</summary>
    Text,

    /// <summary>No status/priority badges at all.</summary>
    Hidden,
}

/// <summary>Pure helpers over <see cref="BadgeDisplay"/> (the F6 cycle and its status-line label),
/// kept out of the Terminal.Gui layer so they're unit-testable.</summary>
public static class BadgeDisplayExtensions
{
    /// <summary>The next mode in the F6 cycle, looping Icons → Text → Hidden → Icons.</summary>
    public static BadgeDisplay Next(this BadgeDisplay mode) => mode switch
    {
        BadgeDisplay.Icons => BadgeDisplay.Text,
        BadgeDisplay.Text => BadgeDisplay.Hidden,
        _ => BadgeDisplay.Icons,
    };

    /// <summary>A short status-line description of the mode, shown when F6 switches to it.</summary>
    public static string Describe(this BadgeDisplay mode) => mode switch
    {
        BadgeDisplay.Icons => "Badges: icons (F6)",
        BadgeDisplay.Text => "Badges: text (F6)",
        _ => "Badges: hidden (F6)",
    };
}
