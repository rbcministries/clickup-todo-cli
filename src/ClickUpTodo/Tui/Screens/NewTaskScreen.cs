using System.Collections.ObjectModel;
using System.Drawing;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Services;
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
/// A full-window compose screen for filing a new task from the main list (#213, sub-issue E of the
/// Writing New Content epic #208): a required <b>Name</b>, an optional multi-line <b>Description</b>,
/// an <b>Assignees</b> selector (the reusable <see cref="AssigneeSelectorView"/> #212 in
/// collect-selection mode, seeded with the current user as a locked default), a <b>List</b> selector
/// (the reusable <see cref="ListSelectorView"/> #239/#240 in collect-selection mode, seeded with the
/// cursor's list — or the personal-list fallback — as the primary/home create target), and the two
/// optional basic fields (#215): a <b>Priority</b> selector (the four canonical priorities + a "no
/// priority" clear row, mirroring the Quick Updates pane #157) and a <b>Due date</b> field. Save builds a
/// <see cref="NewTaskRequest"/> via the pure <see cref="NewTaskForm"/> and creates the task through the
/// injected <paramref name="createAsync"/> callback (the create facade #209) against the List selector's
/// primary list (#240) — a new task must have at least one list, so Save is blocked when none is selected.
/// <para>
/// <b>Custom fields (#395/§2 of #368).</b> When Save is pressed and the chosen primary list has
/// <em>fillable</em> Custom Fields (fetched via the injected <paramref name="fetchListFieldsAsync"/>), the
/// screen swaps to a second <b>Custom fields</b> page — a top-down stack of one input widget per fillable
/// field — before creating: its Save collects the entered values through the pure
/// <see cref="NewTaskCustomFieldForm"/>, enforces required fields client-side, and attaches the values to
/// the create request; Cancel steps back to the base fields. A list with no fillable fields creates
/// directly, exactly as before. The base fields stay a single sectioned page and the second page owns the
/// full area, so no second focusable pane is introduced on the main list (#3/#38); the two pages share one
/// Save/Cancel button and one create path.
/// </para>
/// <para>
/// The create runs <b>while the screen is still mounted</b> so a server failure can keep the form open
/// (re-enable Save, flash the error) — hence the injected-async pattern (the same shape #212's
/// <c>applyAsync</c> uses) rather than the Result-then-close pattern of <see cref="FilterSortGroupScreen"/>.
/// On success it raises <see cref="Created"/> (so the host refreshes + selects the new task) and closes.
/// Swapped into the dashboard's single toplevel like the other screens — no nested run-loop, no second
/// focusable pane on the main list (#3/#38).
/// </para>
/// </summary>
public sealed class NewTaskScreen : Screen
{
    private readonly TextField _name;
    private readonly TextView _description;
    private readonly AssigneeSelectorView _assignees;
    private readonly ListSelectorView _lists;
    private readonly ListView _priority;
    private readonly TextField _due;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly Func<string, NewTaskRequest, CancellationToken, Task<TaskItem>> _createAsync;
    private readonly Func<string, string, CancellationToken, Task> _addToListAsync;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<CustomFieldDefinition>>> _fetchListFieldsAsync;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

    // The base-fields (page 1) controls hidden while the Custom fields page (page 2) is shown — the labels
    // and inputs only; the shared Save/Cancel buttons stay visible on both pages. Populated at construction.
    private readonly List<View> _baseMiddleControls = [];

    // The Custom fields page (page 2): a region filling the area above the shared button row, populated on
    // demand from the primary list's fillable field definitions. Hidden until Save advances to it.
    private readonly View _fieldsPage;
    private readonly Label _fieldsTitle;

    // One collector per rendered widget: the field definition plus a reader that snapshots the widget's
    // current input into a widget-agnostic CustomFieldEntry for the pure NewTaskCustomFieldForm.
    private readonly List<(CustomFieldDefinition Def, Func<CustomFieldEntry> Read)> _fieldReaders = [];

    private bool _onFieldsPage;
    // The stacked height of the built field widgets (the final layout `y`), used as the Custom fields
    // page's scroll content height (#446). 0 until BuildFieldWidgets runs.
    private int _fieldsContentHeight;
    private NewTaskRequest? _pendingRequest;
    private NamedEntity? _pendingPrimary;

    /// <summary>Raised on a successful create with the create outcome (#241) — the server-mapped task plus
    /// any additional lists that couldn't be added — so the host can refresh the list, select the new task,
    /// and report a partial multi-list failure. The task always exists once this fires; the screen closes
    /// itself immediately after.</summary>
    public event EventHandler<NewTaskCreateResult>? Created;

    /// <summary>Flashed when the List selector takes focus while multi-list create is disabled (#241,
    /// pending the list-change migration #365): a new task is filed into its single home list only, so any
    /// additional list the user picks here is ignored on Save. Public so the tui-validate check can assert
    /// the disabled-state note.</summary>
    public const string MultiListDisabledNote =
        "Pick a single list — setting multiple lists isn't supported here yet (the task is created in one list).";

    /// <param name="match">Substring match over the candidate pool, excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.Match</c>.</param>
    /// <param name="topFrequent">Top-N most-frequent candidates excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.TopMostFrequent</c>.</param>
    /// <param name="lockedSelf">The current user, pre-selected and non-removable (the New Task default
    /// assignee). A blank name would be silently dropped by the selector, so the host passes a fallback.</param>
    /// <param name="listMatch">Substring match over the candidate list pool, excluding the given ids — i.e.
    /// <c>ListFrequencyCache.Match</c> (#238/#240).</param>
    /// <param name="listTopFrequent">Top-N most-frequent lists excluding the given ids — i.e.
    /// <c>ListFrequencyCache.TopMostFrequent</c>.</param>
    /// <param name="primaryList">The primary/home list to seed as the create target (#240): the cursor
    /// task's list, or the personal-list fallback — see <see cref="NewTaskForm.ResolveListSeed"/>. Shown
    /// pre-selected with a <c>" (home)"</c> marker; removable (the "≥1 list" rule is enforced on Save).</param>
    /// <param name="createAsync">Creates the task in the given list from the built request and returns it
    /// mapped; run off the UI thread. The host wires this to the create facade; the target list id comes
    /// from the List selector's <c>Primary</c>, not a fixed host constant.</param>
    /// <param name="addToListAsync">Adds the created task to an additional selected list (#237/#241); run
    /// off the UI thread. The host wires this to the membership-write facade. When only the primary list is
    /// selected it is never called (single-list path).</param>
    /// <param name="fetchListFieldsAsync">Fetches a list's Custom Field definitions (#249/#395); run off the
    /// UI thread. On Save the screen fetches the primary list's fields and, when any are fillable, collects
    /// their values on a second page before creating. Wired to <c>TaskService.GetListCustomFieldsAsync</c>.</param>
    public NewTaskScreen(
        Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> match,
        Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> topFrequent,
        TaskAssignee lockedSelf,
        Func<string, ISet<string>, IReadOnlyList<NamedEntity>> listMatch,
        Func<int, ISet<string>, IReadOnlyList<NamedEntity>> listTopFrequent,
        NamedEntity primaryList,
        Func<string, NewTaskRequest, CancellationToken, Task<TaskItem>> createAsync,
        Func<string, string, CancellationToken, Task> addToListAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<CustomFieldDefinition>>> fetchListFieldsAsync)
    {
        _createAsync = createAsync;
        _addToListAsync = addToListAsync;
        _fetchListFieldsAsync = fetchListFieldsAsync;
        Title = "New task";

        var nameLabel = new Label { X = 1, Y = 0, Text = "Name (required):" };
        _name = new TextField { X = 1, Y = 1, Width = Dim.Fill(2), Height = 1 };

        var descriptionLabel = new Label { X = 1, Y = 3, Text = "Description:" };
        _description = new TextView
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(2),
            Height = 6,
            // Let Tab move focus to the next control instead of inserting a tab into the description.
            TabKeyAddsTab = false,
            WordWrap = true,
        };

        var assigneesLabel = new Label { X = 1, Y = Pos.Bottom(_description), Text = "Assignees:" };
        _assignees = new AssigneeSelectorView(
            match,
            topFrequent,
            initialSelected: null,
            lockedDefault: lockedSelf,
            mode: SelectorMode.CollectSelection)
        {
            X = 1,
            Y = Pos.Bottom(assigneesLabel),
            Width = Dim.Fill(2),
            // Reserve the bottom rows for the button line, a blank gap, the Priority/Due block, and the
            // List block below it (8 rows more than the pre-#240 layout, for the List label + 6-row selector
            // + a gap).
            Height = Dim.Fill(17),
        };
        _assignees.Flash += (_, message) => RequestFlash(message);

        _save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        _cancel = new Button { X = Pos.Right(_save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        _save.Accepting += (_, _) => OnSave();
        _cancel.Accepting += (_, _) => OnCancel();

        // List selector (#239/#240) in collect-selection mode, seeded with the primary/home create target.
        // No apply callback — collect mode only mutates the in-memory selection; Save reads Primary.
        var listsLabel = new Label { X = 1, Y = Pos.Top(_save) - 15, Text = "List (at least one required):" };
        _lists = new ListSelectorView(
            listMatch,
            listTopFrequent,
            initialSelected: null,
            primary: primaryList,
            mode: SelectorMode.CollectSelection)
        {
            X = 1,
            // Sits above the Priority/Due block, anchored to Save so it lands on fixed rows regardless of
            // window height (rows Save-14…Save-9 for the 6-row selector, with a blank gap below it).
            Y = Pos.Top(_save) - 14,
            Width = Dim.Fill(2),
            Height = 6,
        };
        _lists.Flash += (_, message) => RequestFlash(message);
        // #241 multi-list create is implemented + unit-tested (NewTaskCreator) but shipped DISABLED
        // pending the list-change field/status migration (#365), mirroring the Quick Updates List pane
        // (#242/#339). While disabled, Save files the task into its single home list only; flag that on the
        // status line the moment the List selector takes focus so a user who adds a second list understands
        // it won't be applied. HasFocus reflects the post-change state, so this fires once on focus-in and
        // not on internal search↔list moves. Remove this when re-enabling multi-list (see OnSave).
        _lists.HasFocusChanged += (_, _) =>
        {
            if (_lists.HasFocus)
                RequestFlash(MultiListDisabledNote);
        };

        // ── Optional fields (#215): Priority + Due date, sitting just above the button line. Positioned
        // relative to Save so the block lands on fixed rows regardless of window height (rows Save-6…Save-2
        // for the 5-row selector, with a blank gap at Save-1's neighbour). Priority mirrors the Quick
        // Updates pane's canonical row set (#157) and defaults to the "(no priority)" clear row.
        var priorityLabel = new Label { X = 1, Y = Pos.Top(_save) - 7, Text = "Priority:" };
        _priority = new ListView { X = 1, Y = Pos.Top(_save) - 6, Width = 22, Height = 5 };
        _priority.SetSource(new ObservableCollection<string>(QuickUpdatesModel.PriorityLabels));
        _priority.SelectedItem = QuickUpdatesModel.NoPriorityRow;

        var dueLabel = new Label { X = Pos.Right(_priority) + 4, Y = Pos.Top(_save) - 7, Text = "Due date (yyyy-MM-dd):" };
        _due = new TextField { X = Pos.Right(_priority) + 4, Y = Pos.Top(_save) - 6, Width = 24, Height = 1 };

        // The Custom fields page (#395): a region filling everything above the shared button row, hidden
        // until Save advances to it. Its widgets are built on demand from the primary list's fields.
        _fieldsTitle = new Label { X = 1, Y = 0, Text = "Custom fields" };
        _fieldsPage = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), Visible = false, CanFocus = true };
        // Make the page a scroll viewport (#446): when the fillable-field stack is taller than the page on
        // a short terminal, the built-in vertical scrollbar (Auto mode) shows and the widgets scroll. The
        // scrollbar auto-hides while the content fits, so short field lists render exactly as before. The
        // scroll content height is set from the built widgets (see ApplyFieldsContentSize); scroll-on-focus
        // and PgUp/PgDn (below) keep every widget reachable.
        _fieldsPage.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        _fieldsPage.Add(_fieldsTitle);

        // Esc cancels (or, on the Custom fields page, steps back), F1 opens Help (#103). Wire the handler to
        // the screen and the text/list editors so they're intercepted before the TextField/TextView/ListView
        // consume them; the selectors already let Esc/F1 fall through to the host.
        KeyDown += OnKey;
        _name.KeyDown += OnKey;
        _description.KeyDown += OnKey;
        _priority.KeyDown += OnKey;
        _due.KeyDown += OnKey;
        _fieldsPage.KeyDown += OnKey;

        // Add in Tab order: Name → Description → Assignees → List → Priority → Due date → Save/Cancel.
        // Labels are not focusable, so their position here doesn't affect the tab cycle.
        _baseMiddleControls.AddRange([
            nameLabel, _name, descriptionLabel, _description, assigneesLabel, _assignees,
            listsLabel, _lists, priorityLabel, _priority, dueLabel, _due]);
        Add(nameLabel, _name, descriptionLabel, _description, assigneesLabel, _assignees,
            listsLabel, _lists, priorityLabel, _priority, dueLabel, _due, _fieldsPage, _save, _cancel);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.NewTask;

    public override void OnShown() => _name.SetFocus();

    /// <inheritdoc/>
    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        // Keep the Custom fields page's scroll content sized to its widgets as the layout settles or the
        // terminal resizes (#446). Cheap and idempotent — ApplyFieldsContentSize only re-sets on a change.
        if (_onFieldsPage)
            ApplyFieldsContentSize();
        base.OnSubViewLayout(args);
    }

    private void OnKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Esc:
                key.Handled = true;
                OnCancel();
                break;
            case KeyCode.F1:
                key.Handled = true;
                RequestHelp();
                break;
            case KeyCode.PageDown:
                // Scan the Custom fields page a viewport at a time without moving focus (#446). Skip when
                // the key came from a drop-down ListView so it keeps its own page-selection behaviour.
                if (_onFieldsPage && sender is not ListView && ScrollFieldsPage(1))
                    key.Handled = true;
                break;
            case KeyCode.PageUp:
                if (_onFieldsPage && sender is not ListView && ScrollFieldsPage(-1))
                    key.Handled = true;
                break;
        }
    }

    // Shared Save button: on the base page validate + advance/create; on the Custom fields page collect +
    // create. Shared Cancel button: on the base page close; on the Custom fields page step back.
    private void OnSave()
    {
        if (_onFieldsPage)
            SaveWithCustomFields();
        else
            SaveBaseFields();
    }

    private void OnCancel()
    {
        // Ignore Cancel/Esc while a fetch or create is in flight: on the Custom fields page it would
        // otherwise flip to the base page mid-create, so a create failure would land on the base page and
        // abandon the entered custom-field values instead of keeping page 2 open for retry.
        if (_busy)
            return;
        if (_onFieldsPage)
            ShowBasePage();
        else
            Close();
    }

    // Validate the base fields; then fetch the primary list's Custom Fields and either advance to the
    // Custom fields page (when any are fillable) or create straight away.
    private void SaveBaseFields()
    {
        if (_busy)
            return;

        var assigneeIds = _assignees.Selection.Select(a => a.Id).ToList();
        var priorityLevel = QuickUpdatesModel.PriorityLevelForRow(_priority.SelectedItem ?? QuickUpdatesModel.NoPriorityRow);
        // The create target is the List selector's primary (first-selected / home) list; null when the user
        // removed every list, which TryBuild rejects with ListRequiredError (#240).
        var primary = _lists.Primary;
        var primaryListId = primary?.Id;
        if (!NewTaskForm.TryBuild(
                _name.Text?.ToString(), _description.Text?.ToString(), assigneeIds,
                priorityLevel, _due.Text?.ToString(), primaryListId, out var request, out var error))
        {
            RequestFlash(error!);
            // Land the cursor on the field the error is about so the user can fix it in place.
            if (error == NewTaskForm.ListRequiredError)
                _lists.SetFocus();
            else if (error == NewTaskForm.DueDateInvalidError)
                _due.SetFocus();
            else
                _name.SetFocus();
            return;
        }

        _pendingRequest = request;
        _pendingPrimary = primary;

        // Fetch the primary list's Custom Fields; re-fetched on every Save so changing the list re-fetches
        // for the new target (#395: "re-fetch when the selected list changes").
        _busy = true;
        _save.Enabled = false;
        RequestFlash("Loading custom fields…");
        var token = _cts.Token;
        var listId = primary!.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                var fields = await _fetchListFieldsAsync(listId, token).ConfigureAwait(false);
                var fillable = fields
                    .Where(f => !string.IsNullOrWhiteSpace(f.Id) && CustomFieldTypes.IsFillable(f.Type))
                    .ToList();
                Application.Invoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    _busy = false;
                    _save.Enabled = true;
                    if (fillable.Count == 0)
                        StartCreate(request!);                 // no fillable fields → create as before
                    else
                        ShowFieldsPage(fillable, primary);     // collect their values first
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    _busy = false;
                    _save.Enabled = true;
                    RequestFlash($"Couldn't load custom fields: {FirstLine(ex.Message)}");
                });
            }
        });
    }

    // Collect the Custom fields page's widgets through the pure form; block on required/parse problems,
    // otherwise attach the values to the pending request and create.
    private void SaveWithCustomFields()
    {
        if (_busy || _pendingRequest is null)
            return;

        var entries = new Dictionary<string, CustomFieldEntry>(StringComparer.Ordinal);
        foreach (var (def, read) in _fieldReaders)
            entries[def.Id] = read();

        var result = NewTaskCustomFieldForm.Collect(_fieldReaders.Select(r => r.Def).ToList(), entries);
        if (!result.IsValid)
        {
            // Surface the more specific parse error first; else name the required fields still empty.
            RequestFlash(result.Errors.Count > 0
                ? result.Errors[0]
                : $"Fill required custom field(s): {string.Join(", ", result.MissingRequired)}");
            return;
        }

        StartCreate(_pendingRequest with { CustomFields = result.Values });
    }

    // Create off the UI thread from a fully-built request. On success raise Created + close; on failure keep
    // the current page open, re-enable Save, and flash the error (so the user can retry without re-typing).
    private void StartCreate(NewTaskRequest request)
    {
        if (_busy)
            return;

        _busy = true;
        _save.Enabled = false;
        RequestFlash("Creating task…");

        var primary = _pendingPrimary;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Multi-list create (#241) is DISABLED pending the list-change migration (#365): file the
                // task into its single home list only by passing just the primary — no additional-list
                // adds fire. The orchestrator, its facade delegate, and the partial-failure result stay
                // wired so re-enabling is a one-line change: pass `_lists.Selection` here instead of
                // `[primary!]` (and drop the MultiListDisabledNote focus flash above). A primary-create
                // failure still throws out (task not created), keeping the form open to retry.
                var result = await NewTaskCreator.CreateAsync(
                    primary!, [primary!], request, _createAsync, _addToListAsync, token).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    Created?.Invoke(this, result);
                    Close();
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    _busy = false;
                    _save.Enabled = true;
                    RequestFlash($"Couldn't create task: {FirstLine(ex.Message)}");
                });
            }
        });
    }

    // ── Custom fields page (#395/§2) ────────────────────────────────────────────

    // Build one input widget per fillable field, top-down, then reveal the page (hiding the base fields).
    private void ShowFieldsPage(IReadOnlyList<CustomFieldDefinition> fields, NamedEntity list)
    {
        BuildFieldWidgets(fields);
        _fieldsTitle.Text = string.IsNullOrWhiteSpace(list.Name)
            ? "Custom fields — * required"
            : $"Custom fields for “{list.Name}” — * required";
        _cancel.Text = "Back";
        _onFieldsPage = true;
        foreach (var c in _baseMiddleControls)
            c.Visible = false;
        _fieldsPage.Visible = true;
        // Start at the top of the stack and size the scroll content to the built widgets (#446). The size
        // is re-applied on layout too (OnSubViewLayout), so it's correct even if the viewport width isn't
        // final yet here or the terminal is resized.
        var vp = _fieldsPage.Viewport;
        _fieldsPage.Viewport = new Rectangle(0, 0, vp.Width, vp.Height);
        ApplyFieldsContentSize();
        FocusFirstFieldWidget();
    }

    // Restore the base fields page (Cancel/Back or Esc from the Custom fields page). The built widgets are
    // left in place; the next Save rebuilds them for the then-current list.
    private void ShowBasePage()
    {
        _onFieldsPage = false;
        _fieldsPage.Visible = false;
        _cancel.Text = "Cancel";
        foreach (var c in _baseMiddleControls)
            c.Visible = true;
        _name.SetFocus();
    }

    private void BuildFieldWidgets(IReadOnlyList<CustomFieldDefinition> fields)
    {
        _fieldReaders.Clear();
        _fieldsPage.RemoveAll();
        _fieldsPage.Add(_fieldsTitle);

        var y = 2;
        foreach (var field in fields)
        {
            var label = field.Name + (field.Required ? " *" : "");
            switch (field.Type!.Trim().ToLowerInvariant())
            {
                case "checkbox":
                    var check = new CheckBox { X = 1, Y = y, Text = label };
                    check.KeyDown += OnKey;
                    WireScrollOnFocus(check);
                    _fieldsPage.Add(check);
                    _fieldReaders.Add((field, () => new CustomFieldEntry
                    {
                        Checked = check.Value == CheckState.Checked,
                    }));
                    y += 2;
                    break;

                case "drop_down":
                    _fieldsPage.Add(new Label { X = 1, Y = y, Text = label + ":" });
                    var options = field.Options
                        .Where(o => !string.IsNullOrWhiteSpace(o.Id))
                        .ToList();
                    var rows = new ObservableCollection<string>(
                        new[] { "(none)" }.Concat(options.Select(o => o.Name ?? o.Id!)));
                    var height = Math.Min(rows.Count, 6);
                    var dd = new ListView { X = 3, Y = y + 1, Width = Dim.Fill(2), Height = height };
                    dd.SetSource(rows);
                    dd.SelectedItem = 0;
                    dd.KeyDown += OnKey;
                    WireScrollOnFocus(dd, labelRowsAbove: 1);
                    _fieldsPage.Add(dd);
                    _fieldReaders.Add((field, () =>
                    {
                        var idx = dd.SelectedItem ?? 0;
                        return new CustomFieldEntry
                        {
                            SelectedOptionIds = idx >= 1 && idx <= options.Count ? [options[idx - 1].Id!] : [],
                        };
                    }
                    ));
                    y += 1 + height + 1;
                    break;

                case "labels":
                    _fieldsPage.Add(new Label { X = 1, Y = y, Text = label + ":" });
                    var labelOptions = field.Options.Where(o => !string.IsNullOrWhiteSpace(o.Id)).ToList();
                    var boxes = new List<(string Id, CheckBox Box)>();
                    var oy = y + 1;
                    foreach (var opt in labelOptions)
                    {
                        var box = new CheckBox { X = 3, Y = oy, Text = opt.Name ?? opt.Id! };
                        box.KeyDown += OnKey;
                        WireScrollOnFocus(box);
                        _fieldsPage.Add(box);
                        boxes.Add((opt.Id!, box));
                        oy++;
                    }
                    _fieldReaders.Add((field, () => new CustomFieldEntry
                    {
                        SelectedOptionIds = boxes
                            .Where(b => b.Box.Value == CheckState.Checked)
                            .Select(b => b.Id)
                            .ToList(),
                    }));
                    y = oy + 1;
                    break;

                default:
                    // Every remaining fillable type (text/short_text/url/email/phone/number/currency/date)
                    // takes a single-line text field; the pure serializer parses/validates per type.
                    _fieldsPage.Add(new Label { X = 1, Y = y, Text = FieldPromptLabel(field, label) });
                    var tf = new TextField { X = 1, Y = y + 1, Width = Dim.Fill(2), Height = 1 };
                    tf.KeyDown += OnKey;
                    WireScrollOnFocus(tf, labelRowsAbove: 1);
                    _fieldsPage.Add(tf);
                    _fieldReaders.Add((field, () => new CustomFieldEntry { Text = tf.Text?.ToString() }));
                    y += 3;
                    break;
            }
        }

        // The stacked height, used as the scroll content height for the page (#446).
        _fieldsContentHeight = y;
    }

    // Size the Custom fields page's scroll content to the built widgets so the taller-than-viewport stack
    // scrolls (#446). Width tracks the current viewport width (the widgets Fill to it), height is the built
    // stack height. Called on show and on every layout so a not-yet-final viewport width or a terminal
    // resize is reconciled. A no-op until widgets are built (_fieldsContentHeight == 0).
    private void ApplyFieldsContentSize()
    {
        if (_fieldsContentHeight <= 0)
            return;
        var width = Math.Max(1, _fieldsPage.Viewport.Width);
        var current = _fieldsPage.GetContentSize();
        if (current.Width != width || current.Height != _fieldsContentHeight)
            _fieldsPage.SetContentSize(new Size(width, _fieldsContentHeight));
    }

    // Scroll the Custom fields page one viewport-height in `direction` (+1 down / -1 up) without moving
    // focus. Returns true when the viewport top actually moved (so the key is marked handled only then).
    private bool ScrollFieldsPage(int direction)
    {
        var vp = _fieldsPage.Viewport;
        var content = _fieldsPage.GetContentSize();
        var newTop = NewTaskFieldsScrollModel.ClampTop(
            vp.Y + direction * Math.Max(1, vp.Height), content.Height, vp.Height);
        if (newTop == vp.Y)
            return false;
        _fieldsPage.Viewport = new Rectangle(vp.X, newTop, vp.Width, vp.Height);
        return true;
    }

    // Scroll the page so a widget that Tab moved focus to is visible (#446) — the reachability guarantee
    // for fields below the fold. Reads the widget's content-space Frame (stable under scrolling) and asks
    // the pure model for the minimal viewport top; assigns only on a real move. <paramref name="labelRowsAbove"/>
    // extends the revealed range upward to also show a field's prompt label (which sits that many rows
    // above the input, e.g. a text field or drop-down); 0 when the widget carries its own label (a
    // checkbox) or is one of several rows under a shared label (a labels option), where revealing the
    // focused widget itself is what's wanted.
    private void WireScrollOnFocus(View widget, int labelRowsAbove = 0)
        => widget.HasFocusChanged += (_, _) =>
        {
            if (!widget.HasFocus)
                return;
            var vp = _fieldsPage.Viewport;
            var content = _fieldsPage.GetContentSize();
            var top = Math.Max(0, widget.Frame.Y - labelRowsAbove);
            var height = widget.Frame.Height + (widget.Frame.Y - top);
            var newTop = NewTaskFieldsScrollModel.ScrollToShow(vp.Y, top, height, vp.Height, content.Height);
            if (newTop != vp.Y)
                _fieldsPage.Viewport = new Rectangle(vp.X, newTop, vp.Width, vp.Height);
        };

    private static string FieldPromptLabel(CustomFieldDefinition field, string label)
        => field.Type!.Trim().ToLowerInvariant() == "date" ? label + " (yyyy-MM-dd):" : label + ":";

    private void FocusFirstFieldWidget()
    {
        // The title Label is first in the child list but not focusable; focus the first real widget so Tab
        // starts inside the field stack.
        foreach (var child in _fieldsPage.SubViews)
        {
            if (child.CanFocus && child.Visible)
            {
                child.SetFocus();
                return;
            }
        }
        _fieldsPage.SetFocus();
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? message : message[..newline];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
