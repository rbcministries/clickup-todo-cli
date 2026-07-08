using ClickUpTodo.Agent;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window editor for the dispatch prompt template (#100), reached from the F2 settings screen.
/// A multi-line editable <see cref="TextView"/> seeded with the current template (saved or the
/// <see cref="AgentPromptComposer.DefaultTemplate"/>); Save exposes the normalized value via
/// <see cref="Result"/>, Cancel/Esc leaves it null. <b>Ctrl+Alt+R</b> resets to the default behind an
/// inline Y/N confirmation (no nested modal — #38), and the available placeholders are listed at the
/// bottom for reference. The decision-free logic lives in the pure <see cref="PromptTemplateEditor"/>.
/// </summary>
public sealed class PromptTemplateEditorScreen : Screen
{
    private readonly TextView _editor;
    private readonly Label _confirm;
    private bool _pendingReset;

    /// <summary>The saved template (normalized), or null if the screen was cancelled.</summary>
    public string? Result { get; private set; }

    public PromptTemplateEditorScreen(string? currentTemplate)
    {
        Title = "Edit dispatch prompt template";

        _editor = new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(PlaceholderReference().Split('\n').Length + 3),
            Text = PromptTemplateEditor.Seed(currentTemplate),
            // Let Tab move focus to Save/Cancel instead of inserting a tab into the template.
            TabKeyAddsTab = false,
            WordWrap = false,
        };

        var reference = new Label
        {
            X = 1,
            Y = Pos.Bottom(_editor),
            Width = Dim.Fill(1),
            Height = PlaceholderReference().Split('\n').Length,
            CanFocus = false,
            Text = PlaceholderReference(),
        };

        // Inline reset confirmation (shown only while a reset is pending). Kept on its own row so it
        // never disturbs the editor/reference layout.
        _confirm = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = "" };

        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        save.Accepting += (_, _) =>
        {
            Result = PromptTemplateEditor.Normalize(_editor.Text?.ToString());
            Close();
        };
        cancel.Accepting += (_, _) => Close();

        // Wire the key handler to both the screen and the editable TextView so Esc / F1 / Ctrl+Alt+R
        // (and the pending-reset Y/N) are intercepted before the TextView consumes them.
        _editor.KeyDown += OnKey;
        KeyDown += OnKey;

        Add([_editor, reference, _confirm, save, cancel]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.PromptTemplateEditor;

    public override void OnShown() => _editor.SetFocus();

    private void OnKey(object? sender, Key key)
    {
        // While a reset is pending, the next keystroke answers the Y/N: only Y confirms, anything else
        // (incl. Esc/N) cancels the reset without touching the editor or closing the screen.
        if (_pendingReset)
        {
            key.Handled = true;
            var confirmed = (key.KeyCode & ~KeyCode.ShiftMask) == KeyCode.Y;
            _editor.Text = PromptTemplateEditor.ApplyReset(confirmed, _editor.Text?.ToString() ?? string.Empty);
            _pendingReset = false;
            _confirm.Text = confirmed ? "Template reset to default." : "";
            return;
        }

        // Ctrl+Alt+R arms the reset; the confirmation warns it reverts every custom change.
        if (key.IsCtrl && key.IsAlt && (key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.AltMask)) == KeyCode.R)
        {
            key.Handled = true;
            _pendingReset = true;
            _confirm.Text = "Reset the prompt template to default? This reverts all custom changes. (Y / N)";
            return;
        }

        switch (key.KeyCode)
        {
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

    /// <summary>The placeholder reference rendered under the editor (one line per placeholder).</summary>
    private static string PlaceholderReference()
        => "Placeholders (unknown tokens stay literal; {{ }} escape a brace):\n"
            + string.Join('\n', AgentPromptComposer.Placeholders.Select(p => $"  {{{p.Name}}} — {p.Description}"));
}
