using System.Text;

namespace ClickUpTodo.Tui;

/// <summary>
/// Builds the window title used in single-task launch mode (<c>--task &lt;id&gt;</c>, #296; #418). A
/// single-task tab titles its top-level <see cref="Terminal.Gui.Views.Window"/> <c>{id}: {name}</c>;
/// Terminal.Gui propagates that title to the host terminal's window/tab (via the driver's
/// <c>SetTerminalTitle</c>), so several such tabs are distinguishable at a glance from the tab strip /
/// window title alone — where identical product branding would not tell them apart.
/// <para>
/// The formatting is a pure function so it is fully unit-testable in CI without a terminal or a
/// Terminal.Gui host.
/// </para>
/// </summary>
public static class TerminalTitle
{
    /// <summary>Max length of the composed <c>{id}: {name}</c> title. Tab titles are short (#418).</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// The title text for a launched task: <c>{id}: {name}</c>, truncated to <paramref name="maxLength"/>
    /// characters. The id part prefers the human-facing <paramref name="customId"/> when it is non-blank,
    /// otherwise the numeric <paramref name="id"/>. A blank <paramref name="name"/> yields just the id
    /// part (no dangling colon). Control characters are collapsed to spaces (a task name is normally a
    /// single line, but a stray newline/tab/escape must not corrupt the window-frame render or the OSC
    /// title escape Terminal.Gui emits from it). Any trailing whitespace left by the cut is trimmed so
    /// the title never ends mid-space.
    /// </summary>
    public static string ForTask(string id, string? customId, string name, int maxLength = MaxLength)
    {
        // Sanitize each part before composing so the blank-name decision sees post-sanitize text: a name
        // that is only control characters collapses to whitespace and must take the id-only branch, not
        // leave a dangling "{id}:".
        var idPart = Sanitize(string.IsNullOrWhiteSpace(customId) ? id : customId);
        var cleanName = Sanitize(name);
        var composed = string.IsNullOrWhiteSpace(cleanName) ? idPart : $"{idPart}: {cleanName}";

        maxLength = Math.Max(0, maxLength);
        if (composed.Length > maxLength)
        {
            composed = composed[..maxLength];
            // Task names can end in an emoji (e.g. 📌), so the cut can land between a surrogate pair's
            // high and low halves and leave a dangling high surrogate — an invalid string that renders
            // as a replacement glyph. Drop that orphan so the title stays one char under the cap rather
            // than corrupt.
            if (composed.Length > 0 && char.IsHighSurrogate(composed[^1]))
                composed = composed[..^1];
        }

        return composed.TrimEnd();
    }

    // Collapse control characters (C0/C1, incl. newlines and tabs) to a single space each so a title can
    // never corrupt the terminal — neither the window-frame draw nor the OSC title escape Terminal.Gui
    // emits from the window Title. Sanitizing before the truncate keeps length predictable (control
    // chars are rare and collapse 1:1), so the 40-char cut lands where the visible text says it does.
    private static string Sanitize(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title)
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        return sb.ToString();
    }
}
