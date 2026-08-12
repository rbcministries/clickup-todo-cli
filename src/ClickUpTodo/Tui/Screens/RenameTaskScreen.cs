using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The main-list task-rename overlay (contextual chords H, #545): a full-window modal over the task
/// list with a single text field pre-filled with the task's current title. It only <em>collects</em>
/// the edit — <see cref="Result"/> carries the trimmed new title (null when cancelled or unchanged) and
/// the host (<c>TodoApp.ApplyRename</c>) does the optimistic write once this modal has closed. A modal
/// (not a second focusable pane) keeps the single-<c>ListView</c> input model intact (#3/#38), and the
/// submit decision is the pure <see cref="RenameTaskModel"/> so the validation stays unit-testable.
/// </summary>
public sealed class RenameTaskScreen : Screen
{
    private readonly string _originalName;
    private readonly TextField _input;
    private readonly KeybindingDispatcher _keys;

    /// <summary>The trimmed new title to write, or null when the screen was cancelled or left unchanged.</summary>
    public string? Result { get; private set; }

    public RenameTaskScreen(string originalName)
    {
        _originalName = originalName ?? string.Empty;
        Title = "Rename task";

        // #355/#398: Esc (Back) and F1 (Help) dispatch through the central table so the keys and their
        // footer labels (HelpItemSets.RenameTask) can't drift. Enter (Save) is a per-form key handled in
        // OnKey below, intentionally absent from the table like the New Task / description editor Save.
        _keys = new KeybindingDispatcher(ScreenContext.RenameTask)
            .On(KeyAction.Help, RequestHelp)
            .On(KeyAction.Back, Close);

        var prompt = new Label
        {
            X = 1,
            Y = 1,
            Text = "New title:",
            CanFocus = false,
        };

        _input = new TextField
        {
            X = 1,
            Y = Pos.Bottom(prompt),
            Width = Dim.Fill(1),
            Text = _originalName,
        };

        var save = new Button { X = 1, Y = Pos.Bottom(_input) + 1, Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.Bottom(_input) + 1, Text = "Cancel" };
        save.Accepting += (_, e) =>
        {
            // Swallow the Accept so the default-button activation doesn't also bubble as an Enter.
            e.Handled = true;
            Submit();
        };
        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        // Intercept Enter/Esc/F1 on both the field and the screen so Enter saves from the text field
        // (not just the default button) and Esc always cancels.
        _input.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([prompt, _input, save, cancel]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.RenameTask;

    public override void OnShown() => _input.SetFocus();

    private void OnKey(object? sender, Key key)
    {
        // Enter → Save (handled here, not via the table — Save is a per-form key). F1 → Help, Esc →
        // Cancel resolve from the central table (#355/#398). A non-matching key falls through unhandled.
        if (key.KeyCode == KeyCode.Enter)
        {
            key.Handled = true;
            Submit();
            return;
        }

        if (_keys.Dispatch(key))
            key.Handled = true;
    }

    /// <summary>
    /// Classifies the field via <see cref="RenameTaskModel"/>: a blank name flashes a hint and keeps the
    /// overlay open (an empty Enter shouldn't dismiss it); an unchanged title just closes with no write;
    /// a genuine edit records the trimmed <see cref="Result"/> and closes for the host to apply.
    /// </summary>
    private void Submit()
    {
        var decision = RenameTaskModel.Classify(_input.Text?.ToString(), _originalName);
        switch (decision.Outcome)
        {
            case RenameTaskModel.Outcome.Blank:
                RequestFlash("A task name can't be blank.");
                return;
            case RenameTaskModel.Outcome.Unchanged:
                Close();
                return;
            default:
                Result = decision.Name;
                Close();
                return;
        }
    }
}
