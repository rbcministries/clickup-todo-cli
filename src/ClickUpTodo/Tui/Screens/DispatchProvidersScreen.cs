using System.Collections.ObjectModel;
using ClickUpTodo.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window editor for the dispatch <b>provider list</b> (#547), reached from the F10 Dispatch
/// section — the management UI over the #497 model. A master/detail form: a <see cref="ListView"/> of
/// providers (the master, with a <c>●</c> marker on the default) beside Name / Executable / Extra-args
/// fields that edit the selected one (the detail, committed on selection change and on Save). Buttons
/// add / delete / set-default; <b>Delete</b> arms an inline Y/N confirm on the status row (no nested
/// modal — #38, mirroring <see cref="PromptTemplateEditorScreen"/>). Save exposes the normalized
/// <see cref="Result"/>; Cancel/Esc leave it null. All decision-free logic lives in the pure
/// <see cref="DispatchProviderListEditor"/>.
/// <para>
/// Multiple focusable controls on a dedicated screen is the established pattern
/// (<see cref="SettingsScreen"/>, <c>NewTaskScreen</c>); the #3 single-focusable-pane rule is about the
/// main task list, not sub-screens.
/// </para>
/// </summary>
public sealed class DispatchProvidersScreen : Screen
{
    private readonly DispatchProviderListEditor _editor;
    private readonly ObservableCollection<string> _rows = [];
    private readonly ListView _list;
    private readonly TextField _nameField;
    private readonly TextField _exeField;
    private readonly TextField _argsField;
    private readonly Label _status;

    /// <summary>The row whose values are currently loaded into the edit fields.</summary>
    private int _editingIndex;

    /// <summary>Guards field/selection writes so programmatic loads don't re-enter as user edits.</summary>
    private bool _syncing;

    /// <summary>True while a delete is awaiting its inline Y/N answer.</summary>
    private bool _pendingDelete;

    /// <summary>The saved provider list + default, or null if the screen was cancelled.</summary>
    public DispatchProvidersResult? Result { get; private set; }

    public DispatchProvidersScreen(IReadOnlyList<DispatchProvider> providers, string? defaultProviderName)
    {
        Title = "Dispatch providers";
        _editor = new DispatchProviderListEditor(providers, defaultProviderName);

        // ── Left: the provider list (master) ───────────────────────────────────
        var listHeader = new Label { X = 1, Y = 0, Text = "─ Providers (● = default) ─" };
        _list = new ListView { X = 1, Y = 1, Width = Dim.Percent(46), Height = Dim.Fill(4) };
        for (var i = 0; i < _editor.Count; i++)
            _rows.Add(Summary(i));
        _list.SetSource(_rows);
        _list.SelectedItem = DefaultRow();

        // ── Right: the selected provider's fields (detail) ─────────────────────
        var rightX = Pos.Percent(50) + 1;
        var editHeader = new Label { X = rightX, Y = 0, Text = "─ Selected provider ─" };
        var nameLabel = new Label { X = rightX, Y = 1, Text = "Name:" };
        _nameField = new TextField { X = rightX, Y = 2, Width = Dim.Fill(2) };
        var exeLabel = new Label { X = rightX, Y = 4, Text = "Executable (blank = claude):" };
        _exeField = new TextField { X = rightX, Y = 5, Width = Dim.Fill(2) };
        var argsLabel = new Label { X = rightX, Y = 7, Text = "Extra args (space-separated):" };
        _argsField = new TextField { X = rightX, Y = 8, Width = Dim.Fill(2) };
        var setDefaultButton = new Button { X = rightX, Y = 10, Text = "Set as default" };

        // ── Bottom: status/confirm line + action buttons ───────────────────────
        _status = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = "" };
        var addButton = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Add" };
        var deleteButton = new Button { X = Pos.Right(addButton) + 1, Y = Pos.AnchorEnd(1), Text = "Delete" };
        var save = new Button { X = Pos.Right(deleteButton) + 2, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 1, Y = Pos.AnchorEnd(1), Text = "Cancel" };

        LoadFields(_list.SelectedItem ?? 0);

        // ValueChanged fires when the selected row changes (SelectedItem aliases Value in TG 2.4.10).
        _list.ValueChanged += (_, _) =>
        {
            if (_syncing)
                return;
            CancelPendingDelete();
            CommitFields();
            LoadFields(_list.SelectedItem ?? 0);
        };

        addButton.Accepting += (_, _) =>
        {
            CancelPendingDelete();
            CommitFields();
            var i = _editor.Add();
            _syncing = true;
            _rows.Add(Summary(i));
            _list.SelectedItem = i;
            _syncing = false;
            LoadFields(i);
            _status.Text = "Provider added — edit its name and executable.";
            _nameField.SetFocus();
        };

        deleteButton.Accepting += (_, _) =>
        {
            var i = _list.SelectedItem ?? 0;
            if (i < 0 || i >= _editor.Count)
                return;
            _pendingDelete = true;
            _status.Text = $"Delete provider '{_editor.Providers[i].Name}'? (Y / N)";
        };

        setDefaultButton.Accepting += (_, _) =>
        {
            CancelPendingDelete();
            CommitFields();
            var i = _list.SelectedItem ?? 0;
            _editor.SetDefault(i);
            RefreshRows();
            _status.Text = $"'{_editor.Providers[i].Name}' is now the default.";
        };

        save.Accepting += (_, _) =>
        {
            if (_pendingDelete)
            {
                CancelPendingDelete();
                return;
            }
            CommitFields();
            Result = _editor.Build();
            Close();
        };
        cancel.Accepting += (_, _) => Close();

        // Wire the key handler to the screen and the editable fields so Esc / F1 / the pending-delete
        // Y/N are intercepted before a focused TextField consumes them (mirrors PromptTemplateEditorScreen).
        KeyDown += OnKey;
        _nameField.KeyDown += OnKey;
        _exeField.KeyDown += OnKey;
        _argsField.KeyDown += OnKey;
        _list.KeyDown += OnListKey;

        Add([
            listHeader, _list,
            editHeader, nameLabel, _nameField, exeLabel, _exeField, argsLabel, _argsField, setDefaultButton,
            _status, addButton, deleteButton, save, cancel,
        ]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.DispatchProviders;

    public override void OnShown() => _list.SetFocus();

    /// <summary>The row index of the current default (for the initial selection).</summary>
    private int DefaultRow()
    {
        for (var i = 0; i < _editor.Count; i++)
            if (_editor.IsDefault(i))
                return i;
        return 0;
    }

    private string Summary(int i)
    {
        var p = _editor.Providers[i];
        var marker = _editor.IsDefault(i) ? "● " : "  ";
        var exe = string.IsNullOrWhiteSpace(p.Executable) ? AgentDispatchSettings.DefaultExecutable : p.Executable.Trim();
        var args = SettingsForm.FormatExtraArgs(p.ExtraArgs);
        return args.Length == 0 ? $"{marker}{p.Name} — {exe}" : $"{marker}{p.Name} — {exe} {args}";
    }

    private void LoadFields(int index)
    {
        if (index < 0 || index >= _editor.Count)
            return;
        _syncing = true;
        var p = _editor.Providers[index];
        _nameField.Text = p.Name;
        _exeField.Text = p.Executable;
        _argsField.Text = SettingsForm.FormatExtraArgs(p.ExtraArgs);
        _editingIndex = index;
        _syncing = false;
    }

    /// <summary>Folds the edit fields back into the editor's currently-tracked row.</summary>
    private void CommitFields()
    {
        if (_editingIndex < 0 || _editingIndex >= _editor.Count)
            return;
        _editor.SetName(_editingIndex, _nameField.Text?.ToString());
        _editor.SetExecutable(_editingIndex, _exeField.Text?.ToString());
        _editor.SetExtraArgs(_editingIndex, SettingsForm.ParseExtraArgs(_argsField.Text?.ToString()));
        _rows[_editingIndex] = Summary(_editingIndex);
        _list.SetNeedsDraw();
    }

    /// <summary>Rewrites every row summary in place (markers move after a set-default).</summary>
    private void RefreshRows()
    {
        for (var i = 0; i < _rows.Count && i < _editor.Count; i++)
            _rows[i] = Summary(i);
        _list.SetNeedsDraw();
    }

    private void CancelPendingDelete()
    {
        if (!_pendingDelete)
            return;
        _pendingDelete = false;
        _status.Text = "";
    }

    private void PerformDelete()
    {
        var i = _list.SelectedItem ?? 0;
        var next = _editor.Delete(i);
        _syncing = true;
        _rows.Clear();
        for (var j = 0; j < _editor.Count; j++)
            _rows.Add(Summary(j));
        _list.SelectedItem = next;
        _syncing = false;
        LoadFields(next);
        _pendingDelete = false;
        _status.Text = "Provider deleted.";
    }

    private void OnKey(object? sender, Key key)
    {
        // A pending delete swallows the next keystroke as its Y/N answer: Y deletes, anything else
        // (incl. Esc/N) cancels — without deleting, editing a field, or closing the screen.
        if (_pendingDelete)
        {
            key.Handled = true;
            if ((key.KeyCode & ~KeyCode.ShiftMask) == KeyCode.Y)
                PerformDelete();
            else
                CancelPendingDelete();
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

    private void OnListKey(object? sender, Key key)
    {
        OnKey(sender, key);
        if (key.Handled)
            return;

        // Delete / Backspace on the list arms the same inline delete confirm as the button (mirrors
        // FilterSortGroupScreen's list Delete). Guarded on a valid selection.
        if (key.KeyCode is KeyCode.Delete or KeyCode.Backspace)
        {
            var i = _list.SelectedItem ?? 0;
            if (i < 0 || i >= _editor.Count)
                return;
            key.Handled = true;
            _pendingDelete = true;
            _status.Text = $"Delete provider '{_editor.Providers[i].Name}'? (Y / N)";
        }
    }
}
