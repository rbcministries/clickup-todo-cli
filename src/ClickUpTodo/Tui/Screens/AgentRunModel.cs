using ClickUpTodo.Agent;

namespace ClickUpTodo.Tui.Screens;

/// <summary>The lifecycle phase of a background one-off run (#99), driving the header the run screen shows.</summary>
public enum AgentRunPhase
{
    /// <summary>The child <c>claude -p</c> process is in flight (spinner animating).</summary>
    Running,

    /// <summary>The run has been asked to stop; the child is being killed.</summary>
    Cancelling,

    /// <summary>The run finished with exit code 0.</summary>
    Succeeded,

    /// <summary>The run finished with a non-zero exit code, or could not start.</summary>
    Failed,

    /// <summary>The run was cancelled by the user.</summary>
    Cancelled,
}

/// <summary>
/// Pure spinner + state machine behind <see cref="AgentRunScreen"/> (issue #99), factored out of the
/// Terminal.Gui glue so the animation frames, header text, and phase transitions are unit-testable
/// without a terminal — the same pure-surface split the repo uses for its other screens
/// (e.g. <see cref="DispatchPaneModel"/>, <see cref="StatusPickerModel"/>). The screen ticks
/// <see cref="Advance"/> from an <c>Application.AddTimeout</c> and reads <see cref="Header"/>; on
/// completion it calls <see cref="MarkSucceeded"/> / <see cref="MarkFailed"/> / <see cref="MarkCancelled"/>.
/// </summary>
public sealed class AgentRunModel
{
    /// <summary>The braille spinner frames, advanced one per timer tick and wrapping at the end.</summary>
    public static readonly IReadOnlyList<string> SpinnerFrames =
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly string _taskName;
    private int _frame;

    public AgentRunModel(string taskName) =>
        _taskName = string.IsNullOrWhiteSpace(taskName) ? "task" : taskName.Trim();

    /// <summary>
    /// The body text to render for a finished background run (#99): stdout on success; stdout plus the
    /// stderr/error and the non-zero exit code on failure; the start-failure message when the process
    /// never ran. Pure, so the branch selection is unit-tested without a real process.
    /// </summary>
    public static string FormatOutput(BackgroundRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.Started)
            return run.Error ?? "Claude could not be started.";

        var text = run.Output ?? string.Empty;
        if (!run.Success)
        {
            if (!string.IsNullOrWhiteSpace(run.Error))
                text += (text.Length > 0 ? "\n\n" : string.Empty) + run.Error;
            text += (text.Length > 0 ? "\n\n" : string.Empty) + $"[claude -p exited with code {run.ExitCode}]";
        }
        return text;
    }

    /// <summary>The current lifecycle phase; starts <see cref="AgentRunPhase.Running"/>.</summary>
    public AgentRunPhase Phase { get; private set; } = AgentRunPhase.Running;

    /// <summary>True while the run is still in flight (running or being cancelled) — Esc should cancel,
    /// not close, in these phases.</summary>
    public bool IsActive => Phase is AgentRunPhase.Running or AgentRunPhase.Cancelling;

    /// <summary>The current spinner frame (does not advance).</summary>
    public string CurrentFrame => SpinnerFrames[_frame];

    /// <summary>
    /// Advances the spinner one frame (wrapping) and returns the fresh <see cref="Header"/>. A no-op on
    /// the header text once the run is no longer active — but still safe to call.
    /// </summary>
    public string Advance()
    {
        _frame = (_frame + 1) % SpinnerFrames.Count;
        return Header;
    }

    /// <summary>Transition to <see cref="AgentRunPhase.Cancelling"/> (the user pressed Esc while running).
    /// Ignored once the run has already finished.</summary>
    public void MarkCancelling()
    {
        if (Phase == AgentRunPhase.Running)
            Phase = AgentRunPhase.Cancelling;
    }

    /// <summary>Mark the run finished per its outcome. <paramref name="success"/> maps to
    /// <see cref="AgentRunPhase.Succeeded"/> / <see cref="AgentRunPhase.Failed"/>.</summary>
    public void MarkFinished(bool success) => Phase = success ? AgentRunPhase.Succeeded : AgentRunPhase.Failed;

    /// <summary>Mark the run cancelled (the child was killed).</summary>
    public void MarkCancelled() => Phase = AgentRunPhase.Cancelled;

    /// <summary>
    /// The header line for the current phase: an animated spinner while running, a fixed marker + verb
    /// once finished. The task name is always named so the user knows what completed.
    /// </summary>
    public string Header => Phase switch
    {
        AgentRunPhase.Running => $"{CurrentFrame} Claude is working on '{_taskName}'…",
        AgentRunPhase.Cancelling => $"{CurrentFrame} Cancelling '{_taskName}'…",
        AgentRunPhase.Succeeded => $"✓ Claude finished '{_taskName}'.",
        AgentRunPhase.Failed => $"✗ Claude failed on '{_taskName}'.",
        AgentRunPhase.Cancelled => $"■ Cancelled '{_taskName}'.",
        _ => _taskName,
    };
}
