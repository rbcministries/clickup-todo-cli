using ClickUpTodo.Tui.Screens;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace ClickUpTodo.Tui;

/// <summary>
/// The two bottom rows both TUI hosts share (#103/#346): a transient status line and the single
/// window-owned contextual help line. Both hosts previously hand-rolled identical <c>Label</c> layout,
/// the <see cref="Flash"/> assignment, and the help-line fit/format-if-changed (<c>UpdateHelpLine</c>);
/// this owns them in one place.
/// <para>
/// The <em>source</em> of the help items and the status composition still belong to each host (the
/// dashboard composes its status line in pieces and caches the fitted footer items for the #289 click
/// hit-test; the single-task host only ever <see cref="Flash"/>es), so <see cref="RenderHelp"/> takes
/// the items and returns the fitted set, and <see cref="Status"/>/<see cref="CommitStatus"/> expose the
/// compose-then-commit split without this type deciding what the status says.
/// </para>
/// </summary>
internal sealed class ContextualFooter
{
    private readonly Label _statusLabel;
    private readonly Label _helpLabel;
    private string _status;

    // The hover hint (#408) currently shown over the steady status, or null when none. Lowest precedence:
    // Flash clears it, CommitStatus keeps it on top of a recomposed steady status.
    private string? _hoverHint;

    /// <param name="initialStatus">The status line's seed text.</param>
    /// <param name="initialHelp">The help line's seed text, or null to leave it empty until the first
    /// <see cref="RenderHelp"/> (the dashboard seeds it with the list shortcuts so the default footer is
    /// byte-for-byte the pre-#103 text).</param>
    public ContextualFooter(string initialStatus, string? initialHelp = null)
    {
        _status = initialStatus;
        _statusLabel = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = initialStatus };
        _helpLabel = new Label { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1) };
        if (initialHelp is not null)
            _helpLabel.Text = initialHelp;
    }

    /// <summary>The help <see cref="Label"/>, exposed so a host can attach a footer-click handler (#289).</summary>
    public Label HelpLabel => _helpLabel;

    /// <summary>The status label's current on-screen text — the flash / hover-hint / steady-status
    /// composition after precedence (#408). Exposed for the precedence unit tests.</summary>
    internal string StatusText => _statusLabel.Text;

    /// <summary>Adds the status + help rows to <paramref name="window"/>.</summary>
    public void AddTo(Window window) => window.Add(_statusLabel, _helpLabel);

    /// <summary>
    /// The composed status text. Setting it does <b>not</b> touch the label — the host composes the line
    /// in pieces (e.g. appending an adaptive-fetch note) then calls <see cref="CommitStatus"/>.
    /// </summary>
    public string Status
    {
        get => _status;
        set => _status = value;
    }

    /// <summary>Assigns the composed <see cref="Status"/> to the status label — keeping any active hover
    /// hint (#408) on top, so recomposing the steady status while the mouse rests on a link updates what
    /// shows once the hint clears without hiding the hint under the pointer.</summary>
    public void CommitStatus() => _statusLabel.Text = _hoverHint ?? _status;

    /// <summary>Sets and commits a transient status message in one step. A flash outranks a hover hint
    /// (#408): it clears the hint and shows the message, and the hint re-asserts on the next hover move.</summary>
    public void Flash(string message)
    {
        _hoverHint = null;
        _status = message;
        _statusLabel.Text = message;
    }

    /// <summary>
    /// Shows a transient hover hint (#408) over the steady status line, or clears it. A non-<c>null</c>
    /// <paramref name="hint"/> displays until cleared; <c>null</c> restores the steady <see cref="Status"/>.
    /// Lowest precedence — a <see cref="Flash"/> replaces a hint and drops it, and <see cref="CommitStatus"/>
    /// keeps a live hint on top of a recomposed steady status. Idempotent, so repeated clears are cheap.
    /// </summary>
    public void SetHoverHint(string? hint)
    {
        _hoverHint = hint;
        _statusLabel.Text = hint ?? _status;
    }

    /// <summary>
    /// Fits <paramref name="items"/> to the help label's current width (column-aware, so wide/emoji
    /// glyphs count), assigns the formatted text only when it changed (so the resize re-fit can't loop),
    /// and returns the fitted set so the caller can cache exactly what's on screen (#289). Before the
    /// first layout the width is 0, so the full set renders and the first resize re-fits it.
    /// </summary>
    public IReadOnlyList<HelpItem> RenderHelp(IReadOnlyList<HelpItem> items)
    {
        var width = _helpLabel.Frame.Width;
        var fitted = width > 0
            ? HelpLine.Fit(items, width, static s => s.GetColumns())
            : items;
        var text = HelpLine.Format(fitted);
        if (_helpLabel.Text != text)
            _helpLabel.Text = text;
        return fitted;
    }
}
