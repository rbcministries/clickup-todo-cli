using System.Collections.ObjectModel;
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
    private readonly Func<string, NewTaskRequest, CancellationToken, Task<TaskItem>> _createAsync;
    private readonly Func<string, string, CancellationToken, Task> _addToListAsync;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

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
        "Setting multiple lists isn't supported here yet — the new task is created in its home list only.";

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
    public NewTaskScreen(
        Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> match,
        Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> topFrequent,
        TaskAssignee lockedSelf,
        Func<string, ISet<string>, IReadOnlyList<NamedEntity>> listMatch,
        Func<int, ISet<string>, IReadOnlyList<NamedEntity>> listTopFrequent,
        NamedEntity primaryList,
        Func<string, NewTaskRequest, CancellationToken, Task<TaskItem>> createAsync,
        Func<string, string, CancellationToken, Task> addToListAsync)
    {
        _createAsync = createAsync;
        _addToListAsync = addToListAsync;
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
        var cancel = new Button { X = Pos.Right(_save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        _save.Accepting += (_, _) => TrySave();
        cancel.Accepting += (_, _) => Close();

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
        // not on internal search↔list moves. Remove this when re-enabling multi-list (see TrySave).
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

        // Esc cancels, F1 opens Help (#103). Wire the handler to the screen and the text/list editors so
        // they're intercepted before the TextField/TextView/ListView consume them; the selectors already
        // let Esc/F1 fall through to the host.
        KeyDown += OnKey;
        _name.KeyDown += OnKey;
        _description.KeyDown += OnKey;
        _priority.KeyDown += OnKey;
        _due.KeyDown += OnKey;

        // Add in Tab order: Name → Description → Assignees → List → Priority → Due date → Save/Cancel.
        // Labels are not focusable, so their position here doesn't affect the tab cycle.
        Add(nameLabel, _name, descriptionLabel, _description, assigneesLabel, _assignees,
            listsLabel, _lists, priorityLabel, _priority, dueLabel, _due, _save, cancel);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.NewTask;

    public override void OnShown() => _name.SetFocus();

    private void OnKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Esc:
                key.Handled = true;
                Close();
                break;
            case KeyCode.F1:
                key.Handled = true;
                RequestHelp();
                break;
        }
    }

    // Validate, then create off the UI thread. On success raise Created + close; on failure keep the
    // form open, re-enable Save, and flash the error (so the user can retry without re-typing).
    private void TrySave()
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

        _busy = true;
        _save.Enabled = false;
        RequestFlash("Creating task…");

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
                    primary!, [primary!], request!, _createAsync, _addToListAsync, token).ConfigureAwait(false);
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
