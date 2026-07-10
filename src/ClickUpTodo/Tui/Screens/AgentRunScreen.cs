using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window screen for a <b>background one-off</b> <c>claude -p</c> dispatch (#99). While the run
/// is in flight it shows an animating "thinking" spinner (driven by <see cref="Application.AddTimeout"/>);
/// on completion it renders the captured output in a read-only, scrollable <see cref="TextView"/> — the
/// single focusable pane, so the #3/#38 latency invariants hold (no second focusable view). Esc
/// <b>cancels</b> the in-flight run (raising <see cref="CancelRequested"/> so the host kills the child)
/// and, once the run has finished, <b>closes</b> the screen. The host drives the lifecycle: it starts
/// the run off the UI thread and calls <see cref="ShowResult"/> / <see cref="ShowCancelled"/> (via
/// <see cref="Application.Invoke"/>) when it ends. The pure spinner/state logic lives in
/// <see cref="AgentRunModel"/>.
/// </summary>
public sealed class AgentRunScreen : Screen
{
    private const int SpinnerIntervalMs = 120;

    /// <summary>The hint shown in the output pane while the run is still in flight.</summary>
    public const string RunningHint = "Working… press Esc to cancel.";

    /// <summary>Shown in place of empty output when a run finishes without printing anything.</summary>
    public const string NoOutputPlaceholder = "(Claude produced no output.)";

    private readonly AgentRunModel _model;
    private readonly Label _header;
    private readonly TextView _output;
    private object? _spinnerToken;

    /// <summary>Raised when the user asks to cancel an in-flight run (Esc while running). The host
    /// cancels the run's <see cref="System.Threading.CancellationTokenSource"/>, which kills the child.</summary>
    public event EventHandler? CancelRequested;

    public AgentRunScreen(string taskName)
    {
        _model = new AgentRunModel(taskName);
        Title = "Claude — one-off run";

        _header = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = 1,
            CanFocus = false,
            Text = _model.Header,
        };

        _output = new TextView
        {
            X = 1,
            Y = Pos.Bottom(_header) + 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            // Arrow/PgUp/PgDn scroll the output; Tab shouldn't insert a tab into a read-only view.
            TabKeyAddsTab = false,
            Text = RunningHint,
        };

        // Bind only the single focusable pane's key handler (like NotificationsFeedScreen) — the output
        // TextView always holds focus, so a screen-level handler would only ever double-fire.
        _output.KeyDown += OnKey;

        Add(_header, _output);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.AgentRun;

    public override void OnShown()
    {
        _output.SetFocus();
        StartSpinner();
    }

    /// <summary>
    /// Switches the screen from "running" to a finished state, stopping the spinner and rendering the
    /// captured <paramref name="output"/>. Called on the UI thread (via <see cref="Application.Invoke"/>)
    /// by the host when the background run completes.
    /// </summary>
    public void ShowResult(string output, bool success)
    {
        _model.MarkFinished(success);
        ShowFinished(output);
    }

    /// <summary>Switches the screen to the cancelled state (the child was killed). Called on the UI thread.</summary>
    public void ShowCancelled(string message)
    {
        _model.MarkCancelled();
        ShowFinished(message);
    }

    private void ShowFinished(string body)
    {
        StopSpinner();
        _header.Text = _model.Header;
        _output.Text = string.IsNullOrWhiteSpace(body) ? NoOutputPlaceholder : body;
        // Keep the caret at the top so the user reads from the start of the output, and refocus the
        // (now result-bearing) pane so Esc/scroll keys land on it.
        _output.SetFocus();
        SetNeedsDraw();
    }

    private void OnKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Esc:
                key.Handled = true;
                if (_model.IsActive)
                {
                    // First Esc: ask the host to cancel the in-flight run. The screen stays open and
                    // flips to a "Cancelling…" header until the host confirms via ShowCancelled.
                    _model.MarkCancelling();
                    _header.Text = _model.Header;
                    SetNeedsDraw();
                    RequestFlash("Cancelling Claude…");
                    CancelRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // The run has finished — Esc closes the screen and returns to the detail view.
                    Close();
                }
                break;
            case KeyCode.F1:
                key.Handled = true;
                RequestHelp();
                break;
        }
    }

    private void StartSpinner()
    {
        _spinnerToken ??= Application.AddTimeout(TimeSpan.FromMilliseconds(SpinnerIntervalMs), OnSpinnerTick);
    }

    private bool OnSpinnerTick()
    {
        if (!_model.IsActive)
        {
            _spinnerToken = null;
            return false; // stop the timeout
        }
        _header.Text = _model.Advance();
        _header.SetNeedsDraw();
        return true; // keep ticking
    }

    private void StopSpinner()
    {
        if (_spinnerToken is { } token)
        {
            Application.RemoveTimeout(token);
            _spinnerToken = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            StopSpinner();
        base.Dispose(disposing);
    }
}
