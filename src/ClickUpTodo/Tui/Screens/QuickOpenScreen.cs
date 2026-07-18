using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// The Ctrl+O quick-open entry surface (#303): a full-window modal over the task list with a single
/// text field for a task <b>id</b>, <b>custom id</b>, or <b>task URL</b>. It only <em>collects</em> the
/// input — <see cref="Result"/> carries the submitted text (null when cancelled) and the host does the
/// parse/resolve/navigate once this modal has closed, so the detail view never stacks on top of the
/// entry surface. A modal (not a second focusable pane) keeps the single-<c>ListView</c> input model
/// intact (#3/#38).
/// </summary>
public sealed class QuickOpenScreen : Screen
{
    private readonly TextField _input;

    /// <summary>The submitted (trimmed) text, or null when the screen was cancelled.</summary>
    public string? Result { get; private set; }

    public QuickOpenScreen()
    {
        Title = "Open a task";

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

        var open = new Button { X = 1, Y = Pos.Bottom(_input) + 1, Text = "Open", IsDefault = true };
        var cancel = new Button { X = Pos.Right(open) + 2, Y = Pos.Bottom(_input) + 1, Text = "Cancel" };
        open.Accepting += (_, e) =>
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

        // Intercept Enter/Esc on both the field and the screen so Enter submits from the text field
        // (not just the default button) and Esc always cancels.
        _input.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([prompt, _input, open, cancel]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.QuickOpen;

    public override void OnShown() => _input.SetFocus();

    private void OnKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Enter:
                key.Handled = true;
                Submit();
                break;
            case KeyCode.F1:
                key.Handled = true;
                RequestHelp();
                break;
            case KeyCode.Esc:
                key.Handled = true;
                Close();
                break;
        }
    }

    /// <summary>
    /// Records the trimmed input and closes when it's non-blank; a blank field flashes a hint and stays
    /// open so the user can type rather than dismissing the surface on an empty Enter.
    /// </summary>
    private void Submit()
    {
        var text = _input.Text?.ToString()?.Trim() ?? "";
        if (text.Length == 0)
        {
            RequestFlash("Enter a task id, custom id, or ClickUp task URL.");
            return;
        }

        Result = text;
        Close();
    }
}
