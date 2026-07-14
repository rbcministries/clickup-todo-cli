using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window screen (#153/#156) that lets the user change a task's <b>Status</b>, <b>Priority</b>
/// and <b>Assignees</b> without leaving their place. It hosts three vertically-stacked, focusable
/// controls; <c>Tab</c>/<c>Shift+Tab</c> cycle focus Status → Priority → Assignees (wrapping) and
/// <c>Esc</c> exits from any pane.
/// <para>
/// Status and Priority are <b>deferred-commit</b> (#157): moving the highlight does nothing; pressing
/// <c>Enter</c> applies the highlighted value. Each pane marks its current effective value with a
/// leading <c>✓</c>. On Enter the screen moves the <c>✓</c> optimistically and raises
/// <see cref="StatusCommitted"/> / <see cref="PriorityCommitted"/>; the host performs the optimistic
/// row update + off-thread write + revert-on-failure and then calls <see cref="SetEffectiveStatus"/> /
/// <see cref="SetEffectivePriority"/> so the <c>✓</c> always reflects the server-confirmed value. The
/// Assignees search box + immediate apply (drawing on the frequency cache #155) lands in #158.
/// </para>
/// </summary>
public sealed class QuickUpdatesScreen : Screen
{
    private readonly ListView _statusList;
    private readonly ListView _priorityList;
    private readonly ListView _assigneesList;
    private readonly IReadOnlyList<StatusOption> _statuses;

    // The panes in focus (Tab) order — index maps to QuickUpdatesPane.
    private readonly ListView[] _panes;

    // The task's current effective values, mirrored so the ✓ marker (and the "unchanged" guard) track
    // the latest confirmed value. Seeded from the task; updated optimistically on Enter and reconciled
    // by the host from the server's returned value.
    private string? _effectiveStatus;
    private int? _effectivePriorityLevel;

    /// <summary>Raised when the user commits a (changed) status with Enter; the host applies it.</summary>
    public event Action<string>? StatusCommitted;

    /// <summary>Raised when the user commits a (changed) priority with Enter (<c>null</c> = clear);
    /// the host applies it.</summary>
    public event Action<int?>? PriorityCommitted;

    public QuickUpdatesScreen(
        string taskName,
        IReadOnlyList<StatusOption> statuses,
        string? currentStatus,
        int? currentPriorityLevel,
        IReadOnlyList<TaskAssignee> currentAssignees)
    {
        _statuses = statuses;
        _effectiveStatus = currentStatus;
        _effectivePriorityLevel = currentPriorityLevel;

        var title = taskName.Length > 40 ? taskName[..39] + "…" : taskName;
        Title = $"Quick Updates — {title}";

        // Three bordered sections, top-to-bottom in focus order. Priority (5 fixed rows) and Assignees
        // get a compact frame each at the bottom; Status takes the remaining top space. The shared
        // footer (#103) carries the shortcuts, so no per-pane hint labels are needed.
        _statusList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _statusList.SetSource(new ObservableCollection<string>(QuickUpdatesModel.StatusRows(statuses, _effectiveStatus)));
        var preselectedStatus = StatusPickerModel.PreselectedIndex(statuses, currentStatus);
        if (preselectedStatus >= 0)
            _statusList.SelectedItem = preselectedStatus;

        _priorityList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _priorityList.SetSource(new ObservableCollection<string>(QuickUpdatesModel.PriorityRows(_effectivePriorityLevel)));
        _priorityList.SelectedItem = QuickUpdatesModel.PriorityRowForLevel(currentPriorityLevel);

        _assigneesList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _assigneesList.SetSource(new ObservableCollection<string>(QuickUpdatesModel.AssigneeRows(currentAssignees)));

        var statusFrame = new FrameView { Title = "Status", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(13) };
        statusFrame.Add(_statusList);

        var priorityFrame = new FrameView { Title = "Priority", X = 0, Y = Pos.AnchorEnd(13), Width = Dim.Fill(), Height = 7 };
        priorityFrame.Add(_priorityList);

        var assigneesFrame = new FrameView { Title = "Assignees", X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 6 };
        assigneesFrame.Add(_assigneesList);

        _panes = [_statusList, _priorityList, _assigneesList];
        foreach (var pane in _panes)
            pane.KeyDown += OnPaneKey;

        Add([statusFrame, priorityFrame, assigneesFrame]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.QuickUpdates;

    public override void OnShown() => _statusList.SetFocus();

    /// <summary>Reconciles the Status pane's <c>✓</c> to the host's confirmed (or reverted) value.</summary>
    public void SetEffectiveStatus(string? status)
    {
        _effectiveStatus = status;
        ReplaceRows(_statusList, QuickUpdatesModel.StatusRows(_statuses, _effectiveStatus));
    }

    /// <summary>Reconciles the Priority pane's <c>✓</c> to the host's confirmed (or reverted) value.</summary>
    public void SetEffectivePriority(int? level)
    {
        _effectivePriorityLevel = level;
        ReplaceRows(_priorityList, QuickUpdatesModel.PriorityRows(_effectivePriorityLevel));
    }

    /// <summary>
    /// Screen-wide keys shared by all three panes. Tab/Shift+Tab cycle focus (wrapping); Esc exits;
    /// F1 opens Help; Enter commits the highlighted Status/Priority value (Assignees apply is #158).
    /// ↑/↓ fall through so each ListView moves its own selection.
    /// </summary>
    private void OnPaneKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Tab:
                key.Handled = true;
                CyclePane(forward: !key.IsShift);
                break;
            case KeyCode.Enter:
                if (ReferenceEquals(sender, _statusList))
                {
                    CommitStatus();
                    key.Handled = true;
                }
                else if (ReferenceEquals(sender, _priorityList))
                {
                    CommitPriority();
                    key.Handled = true;
                }
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

    private void CommitStatus()
    {
        if (_statusList.SelectedItem is not int i || i < 0 || i >= _statuses.Count)
            return;
        var name = _statuses[i].Name;
        if (string.Equals(name, _effectiveStatus, StringComparison.OrdinalIgnoreCase))
        {
            RequestFlash("Status unchanged.");
            return;
        }
        // Hand the write to the host; it moves the ✓ (via SetEffectiveStatus) only once it commits to
        // attempting the write, and reconciles us again from the server-confirmed value.
        StatusCommitted?.Invoke(name);
    }

    private void CommitPriority()
    {
        if (_priorityList.SelectedItem is not int i)
            return;
        var level = QuickUpdatesModel.PriorityLevelForRow(i);
        if (level == _effectivePriorityLevel)
        {
            RequestFlash("Priority unchanged.");
            return;
        }
        PriorityCommitted?.Invoke(level);
    }

    // Replaces a pane's rows in place, keeping the highlighted index so moving the ✓ doesn't jump the
    // cursor (SetSource resets SelectedItem to 0).
    private static void ReplaceRows(ListView list, IReadOnlyList<string> rows)
    {
        var selected = list.SelectedItem;
        list.SetSource(new ObservableCollection<string>(rows));
        if (selected is int i && i >= 0 && i < rows.Count)
            list.SelectedItem = i;
    }

    private void CyclePane(bool forward)
    {
        var current = Array.FindIndex(_panes, static p => p.HasFocus);
        if (current < 0)
            current = 0;
        var next = QuickUpdatesModel.Cycle((QuickUpdatesPane)current, forward);
        _panes[(int)next].SetFocus();
    }
}
