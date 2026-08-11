using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window screen to edit the F3 view: build filter rules (field / operator / value), pick a
/// sort field + direction, and a group field. On Save it exposes the new <see cref="ViewSettings"/>
/// via <see cref="Result"/> and closes; Cancel/Esc close with <see cref="Result"/> left null. All
/// semantics live in the pure <see cref="TaskView"/> engine and <see cref="FilterSortGroupForm"/> —
/// this is only presentation, swapped into the dashboard's single toplevel like the other screens (#38).
/// <para>
/// The form itself is built by the host-agnostic <see cref="FilterSortGroupFormBuilder"/>, shared with
/// the #554 native-modal <c>Dialog</c> variant so the two hosting mechanisms can be measured A/B while
/// rendering the identical form; this class contributes only the <c>_screens</c> host affordances (the
/// context <see cref="KeybindingDispatcher"/> Esc/F1 and the footer <see cref="HelpItems"/>).
/// </para>
/// </summary>
public sealed class FilterSortGroupScreen : Screen
{
    private readonly FilterSortGroupFormHandle _form;
    private readonly KeybindingDispatcher _keys;

    /// <summary>The saved view, or null if the screen was cancelled.</summary>
    public ViewSettings? Result => _form.Result;

    public FilterSortGroupScreen(ViewSettings current)
    {
        Title = "Filter · Sort · Group";

        _form = FilterSortGroupFormBuilder.Build(current, RequestFlash, Close);

        // Command keys dispatch through the central (context, action) → key table (#355/#398), so the
        // bindings here and their footer labels (HelpItemSets.FilterSortGroup) share one source of
        // truth and cannot drift. Esc cancels from anywhere on the screen (Result stays null); F1
        // opens Help (#103). Form-focus keys — the value field's Enter (add) and the filters list's
        // Delete/Backspace (remove) — are per-form, wired inside the builder on their own views.
        _keys = new KeybindingDispatcher(ScreenContext.FilterSortGroup)
            .On(KeyAction.Help, RequestHelp)
            .On(KeyAction.Back, Close);

        KeyDown += (_, key) =>
        {
            if (_keys.Dispatch(key))
                key.Handled = true;
        };

        Add([.. _form.Controls]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.FilterSortGroup;

    public override void OnShown() => _form.PrimaryFocus.SetFocus();
}
