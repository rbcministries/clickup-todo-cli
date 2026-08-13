using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>Where a resolved quick-open target should open (launch modes B, #615, epic #613): in place
/// in this process (<c>Enter</c>, today's behaviour), a new terminal tab (<c>Ctrl+Enter</c>,
/// <c>LaunchLocation.NewTab</c>), or a split pane beside the current one (<c>Ctrl+Alt+Enter</c>,
/// <c>LaunchLocation.SplitPane</c>). The screen only records the chosen intent alongside the text; the
/// host maps it to the launch terminus, which is what keeps slice E's native-modal re-host a hosting
/// change only.</summary>
public enum QuickOpenIntent
{
    OpenHere,
    NewTab,
    SplitPane,
}

/// <summary>The quick-open surface's collected result (launch modes B, #615): the trimmed
/// <paramref name="Text"/> and the <paramref name="Intent"/> chosen by the submitting gesture. Carrying
/// the intent in the result — rather than firing a per-gesture event — is what lets slice E re-host the
/// surface without the host learning a new contract.</summary>
public readonly record struct QuickOpenRequest(string Text, QuickOpenIntent Intent)
{
    /// <summary>Builds a request from the raw field text and the submitting <paramref name="intent"/>, or
    /// <c>null</c> when the field is blank/whitespace (the screen then flashes and stays open rather than
    /// dismissing on an empty gesture). The pure, Terminal.Gui-free seam the screen's submit paths funnel
    /// through, so the trim + blank guard is unit-tested for every intent.</summary>
    public static QuickOpenRequest? From(string? rawText, QuickOpenIntent intent)
    {
        var text = rawText?.Trim() ?? "";
        return text.Length == 0 ? null : new QuickOpenRequest(text, intent);
    }
}

/// <summary>
/// The Ctrl+O quick-open entry surface (#303): a full-window modal over the task list with a single
/// text field for a task <b>id</b>, <b>custom id</b>, or <b>task URL</b>. It only <em>collects</em> the
/// input — <see cref="Result"/> carries the submitted text + launch intent (null when cancelled) and the
/// host does the parse/resolve/navigate/launch once this modal has closed, so the detail view never
/// stacks on top of the entry surface. A modal (not a second focusable pane) keeps the single-<c>ListView</c>
/// input model intact (#3/#38).
/// <para>
/// Three submit gestures pick the destination (launch modes B, #615): <c>Enter</c>/<c>Open</c> opens in
/// place, <c>Ctrl+Enter</c>/<c>New tab</c> a new terminal tab, <c>Ctrl+Alt+Enter</c>/<c>Split pane</c> a
/// pane beside the current one. The buttons are the driver-robust path for the two chords — a
/// <c>Ctrl+Enter</c>-from-a-text-control is not reliable across drivers (the comment-composer precedent,
/// #503) — and are <c>Tab</c>-reachable; <c>Open</c> stays the default.
/// </para>
/// </summary>
public sealed class QuickOpenScreen : Screen
{
    private readonly TextField _input;
    private readonly KeybindingDispatcher _keys;

    /// <summary>The submitted (trimmed) text + chosen launch intent, or null when the screen was
    /// cancelled.</summary>
    public QuickOpenRequest? Result { get; private set; }

    public QuickOpenScreen()
    {
        Title = "Open a task";

        // #355/#398: dispatch the command shortcuts through the central table rather than a
        // hand-rolled key switch, so the keys and their footer labels (HelpItemSets.QuickOpen)
        // cannot drift. The three submit gestures pick the destination (launch modes B, #615);
        // Help/Back round out the ScreenContext.QuickOpen entries.
        _keys = new KeybindingDispatcher(ScreenContext.QuickOpen)
            .On(KeyAction.Open, () => Submit(QuickOpenIntent.OpenHere))
            .On(KeyAction.OpenInNewTab, () => Submit(QuickOpenIntent.NewTab))
            .On(KeyAction.OpenInSplitPane, () => Submit(QuickOpenIntent.SplitPane))
            .On(KeyAction.Help, RequestHelp)
            .On(KeyAction.Back, Close);

        var prompt = new Label
        {
            X = 1,
            Y = 1,
            Text = "Task id, custom id, or URL:",
            CanFocus = false,
        };

        _input = new TextField
        {
            X = 1,
            Y = Pos.Bottom(prompt),
            Width = Dim.Fill(1),
        };

        // Open stays the default (Enter); New tab / Split pane are the Tab-reachable, driver-robust path
        // for the two chords (#615), and Cancel closes. Each button funnels through the same Submit(intent)
        // the chords do, so the two entry paths can't drift.
        var open = new Button { X = 1, Y = Pos.Bottom(_input) + 1, Text = "Open", IsDefault = true };
        var newTab = new Button { X = Pos.Right(open) + 2, Y = Pos.Bottom(_input) + 1, Text = "New tab" };
        var splitPane = new Button { X = Pos.Right(newTab) + 2, Y = Pos.Bottom(_input) + 1, Text = "Split pane" };
        var cancel = new Button { X = Pos.Right(splitPane) + 2, Y = Pos.Bottom(_input) + 1, Text = "Cancel" };
        open.Accepting += (_, e) =>
        {
            // Swallow the Accept so the default-button activation doesn't also bubble as an Enter.
            e.Handled = true;
            Submit(QuickOpenIntent.OpenHere);
        };
        newTab.Accepting += (_, e) =>
        {
            e.Handled = true;
            Submit(QuickOpenIntent.NewTab);
        };
        splitPane.Accepting += (_, e) =>
        {
            e.Handled = true;
            Submit(QuickOpenIntent.SplitPane);
        };
        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        // Intercept the submit chords/Esc on both the field and the screen so Enter/Ctrl+Enter/
        // Ctrl+Alt+Enter submit from the text field (not just a focused button) and Esc always cancels.
        _input.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([prompt, _input, open, newTab, splitPane, cancel]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.QuickOpen;

    public override void OnShown() => _input.SetFocus();

    private void OnKey(object? sender, Key key)
    {
        // Enter/Ctrl+Enter/Ctrl+Alt+Enter → Submit(intent), F1 → Help, Esc → Cancel, all resolved from the
        // central table (#355/#398). A non-matching key falls through unhandled, exactly as before.
        if (_keys.Dispatch(key))
            key.Handled = true;
    }

    /// <summary>
    /// Records the trimmed input + <paramref name="intent"/> and closes when the field is non-blank; a
    /// blank field flashes a hint and stays open (for every intent) so the user can type rather than
    /// dismissing the surface on an empty gesture. The parse/resolve/launch runs in the host once this
    /// modal has closed.
    /// </summary>
    private void Submit(QuickOpenIntent intent)
    {
        if (QuickOpenRequest.From(_input.Text?.ToString(), intent) is { } request)
        {
            Result = request;
            Close();
        }
        else
        {
            RequestFlash("Enter a task id, custom id, or ClickUp task URL.");
        }
    }
}
