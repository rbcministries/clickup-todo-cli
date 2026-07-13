using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ClickUpTodo.Agent;

/// <summary>
/// Default <see cref="IBackgroundAgentRunner"/>. Runs a one-off <c>claude -p</c> (#99) as a background
/// child <see cref="Process"/> with redirected stdin/stdout/stderr — no terminal window — feeding the
/// composed prompt in on stdin and <b>streaming</b> the parsed output as it arrives (#187). Cancellation
/// kills the child process tree.
/// <para>
/// The run uses <c>--output-format stream-json --verbose</c> so stdout is a newline-delimited JSON event
/// stream; each line is parsed by <see cref="AgentStreamJson"/> into display text that is both reported
/// to <c>progress</c> live and accumulated into the returned <see cref="BackgroundRunResult.Output"/>
/// (so the final render matches what streamed).
/// </para>
/// <para>
/// The prompt is fed via <b>stdin</b> (not a positional argument) so an arbitrarily large composed
/// prompt — task description + comments — can't hit the OS command-line length limit. Only the flags and
/// any configured extra args ever reach the argument vector, and it is built as an array (never a shell
/// string), so nothing in the prompt is ever interpreted by a shell.
/// </para>
/// </summary>
public sealed class BackgroundAgentRunner : IBackgroundAgentRunner
{
    public async Task<BackgroundRunResult> RunAsync(
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(promptFilePath) || !File.Exists(promptFilePath))
            return BackgroundRunResult.NotStarted($"Prompt file not found: {promptFilePath}");

        var prompt = await File.ReadAllTextAsync(promptFilePath, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo(options.ClaudeExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in BuildArguments(options))
            psi.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(workingDir))
            psi.WorkingDirectory = workingDir;

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
                return BackgroundRunResult.NotStarted($"Could not start '{options.ClaudeExecutable}'.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Most commonly the executable isn't on PATH — surface a clear, actionable message.
            return BackgroundRunResult.NotStarted(
                $"Could not start '{options.ClaudeExecutable}': {ex.Message} (is it installed and on PATH?)");
        }

        // Feed stdin and drain stderr on their own tasks so they run concurrently with the stdout read
        // loop below — a child that fills the stdout pipe before consuming all of stdin can't deadlock.
        var stdinTask = FeedStdinAsync(process, prompt, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var assembled = new StringBuilder();
        try
        {
            // Read stdout line by line: each line is one stream-json event. Parse it into display lines,
            // report each live (progress), and accumulate the identical text so the final Output matches
            // what streamed. ReadLineAsync honours `ct`, so a cancel lands in the catch and kills the child.
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                foreach (var display in AgentStreamJson.ParseLine(line))
                {
                    var piece = display + "\n";
                    assembled.Append(piece);
                    progress?.Report(piece);
                }
            }

            await stdinTask.ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            // Observe the in-flight tasks so their cancellation/pipe faults aren't left unobserved.
            await ObserveAsync(stdinTask).ConfigureAwait(false);
            await ObserveAsync(stderrTask).ConfigureAwait(false);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        return BackgroundRunResult.Exited(
            process.ExitCode, assembled.ToString(), string.IsNullOrWhiteSpace(stderr) ? null : stderr);
    }

    /// <summary>Writes the prompt to the child's stdin and closes it so <c>claude -p</c> reads it to
    /// completion. A broken pipe (the child exited before consuming all input) is not itself a failure —
    /// the captured output/exit code still reflect what it did — so it is swallowed.</summary>
    private static async Task FeedStdinAsync(Process process, string prompt, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Broken pipe: the child stopped reading stdin early. Not a failure on its own.
        }
    }

    /// <summary>
    /// The argument vector for a background one-off run: <c>-p</c> (headless print mode) with
    /// <c>--output-format stream-json --verbose</c> so the run emits a parseable JSON event stream to
    /// stdout (stream-json print mode requires <c>--verbose</c>), followed by any configured extra args
    /// (e.g. a model flag). The prompt is <b>not</b> here — it is fed on stdin. Pure, so the composition
    /// is unit-tested.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(TerminalLauncherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        args.AddRange(options.ExtraArgs.Where(a => !string.IsNullOrWhiteSpace(a)));
        return args;
    }

    /// <summary>Awaits a stream-read task purely to observe it (so a cancellation/pipe fault on the
    /// cancel path isn't left unobserved); the partial output is discarded because the run was killed.</summary>
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The read was cancelled or the pipe closed when the child was killed — nothing to keep.
        }
    }

    /// <summary>Best-effort kill of the child (and its descendants); ignores races where it already exited.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process exited between the check and the kill, or the platform can't kill a tree —
            // nothing more to do.
        }
    }
}
