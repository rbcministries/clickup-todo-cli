using ClickUpTodo.ClickUp;
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
/// and an <b>Assignees</b> selector (the reusable <see cref="AssigneeSelectorView"/> #212 in
/// collect-selection mode, seeded with the current user as a locked default). Save builds a
/// <see cref="NewTaskRequest"/> via the pure <see cref="NewTaskForm"/> and creates the task through the
/// injected <paramref name="createAsync"/> callback (the create facade #209, wired by the host to the
/// configured Personal Tasks list).
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
    private readonly Button _save;
    private readonly Func<NewTaskRequest, CancellationToken, Task<TaskItem>> _createAsync;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

    /// <summary>Raised on a successful create with the server-mapped task, so the host can refresh the
    /// list and select the new task. The screen closes itself immediately after.</summary>
    public event EventHandler<TaskItem>? Created;

    /// <param name="match">Substring match over the candidate pool, excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.Match</c>.</param>
    /// <param name="topFrequent">Top-N most-frequent candidates excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.TopMostFrequent</c>.</param>
    /// <param name="lockedSelf">The current user, pre-selected and non-removable (the New Task default
    /// assignee). A blank name would be silently dropped by the selector, so the host passes a fallback.</param>
    /// <param name="createAsync">Creates the task from the built request and returns it mapped; run off
    /// the UI thread. The host wires this to the create facade against the target list.</param>
    public NewTaskScreen(
        Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> match,
        Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> topFrequent,
        TaskAssignee lockedSelf,
        Func<NewTaskRequest, CancellationToken, Task<TaskItem>> createAsync)
    {
        _createAsync = createAsync;
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
            mode: AssigneeSelectorMode.CollectSelection)
        {
            X = 1,
            Y = Pos.Bottom(assigneesLabel),
            Width = Dim.Fill(2),
            // Leave the bottom two rows for the button line (AnchorEnd(1)) plus a blank gap above it.
            Height = Dim.Fill(2),
        };
        _assignees.Flash += (_, message) => RequestFlash(message);

        _save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(_save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        _save.Accepting += (_, _) => TrySave();
        cancel.Accepting += (_, _) => Close();

        // Esc cancels, F1 opens Help (#103). Wire the handler to the screen and the two text editors so
        // they're intercepted before the TextField/TextView consume them; the selector already lets
        // Esc/F1 fall through to the host.
        KeyDown += OnKey;
        _name.KeyDown += OnKey;
        _description.KeyDown += OnKey;

        Add(nameLabel, _name, descriptionLabel, _description, assigneesLabel, _assignees, _save, cancel);
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
        if (!NewTaskForm.TryBuild(_name.Text?.ToString(), _description.Text?.ToString(), assigneeIds, out var request, out var error))
        {
            RequestFlash(error!);
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
                var created = await _createAsync(request!, token).ConfigureAwait(false);
                Application.Invoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    Created?.Invoke(this, created);
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
