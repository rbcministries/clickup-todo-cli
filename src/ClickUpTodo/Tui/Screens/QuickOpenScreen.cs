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
    private readonly QuickOpenFormHandle _form;
    private readonly KeybindingDispatcher _commandKeys;
    private readonly LaunchChordOverrides _launchChords;

    /// <summary>The submitted (trimmed) text + chosen launch intent, or null when the screen was
    /// cancelled.</summary>
    public QuickOpenRequest? Result => _form.Result;

    public QuickOpenScreen(LaunchChordOverrides? launchChords = null)
    {
        _launchChords = launchChords ?? LaunchChordOverrides.None;
        Title = "Open a task";

        // The control-building + the three submit gestures + the blank-input flash live in the shared
        // QuickOpenFormBuilder (slice E, #618), so the native-modal Dialog host mounts the identical form.
        // The launch-chord override (#506) threads into the builder's submit dispatcher, so a rebound
        // Ctrl+Enter / Ctrl+Alt+Enter fires here too. Close/RequestFlash are this host's back/flash affordance.
        _form = QuickOpenFormBuilder.Build(RequestFlash, Close, _launchChords);

        // Context command keys stay on the host: F1 → Help, Esc → Back, resolved from the central table
        // (#355/#398). The form owns the submit gestures; DispatchSubmit is wired at the screen level too
        // so a chord fires from a focused button as well as from the text field. Help/Back aren't launch
        // chords, so this dispatcher needs no override.
        _commandKeys = new KeybindingDispatcher(ScreenContext.QuickOpen)
            .On(KeyAction.Help, RequestHelp)
            .On(KeyAction.Back, Close);
        KeyDown += OnKey;

        Add([.. _form.Controls]);
    }

    public override IReadOnlyList<HelpItem> HelpItems =>
        HelpItemSets.WithConfiguredLaunchChords(HelpItemSets.QuickOpen, _launchChords);

    public override void OnShown() => _form.PrimaryFocus.SetFocus();

    private void OnKey(object? sender, Key key)
    {
        // Enter/Ctrl+Enter/Ctrl+Alt+Enter → Submit(intent) (the form), F1 → Help, Esc → Back (the host).
        // A non-matching key falls through unhandled, exactly as before.
        if (_form.DispatchSubmit(key) || _commandKeys.Dispatch(key))
            key.Handled = true;
    }
}
