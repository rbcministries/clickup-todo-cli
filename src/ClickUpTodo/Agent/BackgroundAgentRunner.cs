using System.ComponentModel;
using System.Diagnostics;

namespace ClickUpTodo.Agent;

/// <summary>
/// Default <see cref="IBackgroundAgentRunner"/>. Runs a one-off <c>claude -p</c> (#99) as a background
/// child <see cref="Process"/> with redirected stdin/stdout/stderr — no terminal window — feeding the
/// composed prompt in on stdin and capturing the output. Cancellation kills the child process tree.
/// <para>
/// The prompt is fed via <b>stdin</b> (not a positional argument) so an arbitrarily large composed
/// prompt — task description + comments — can't hit the OS command-line length limit. Only the
/// <c>-p</c> flag and any configured extra args ever reach the argument vector, and it is built as an
/// array (never a shell string), so nothing in the prompt is ever interpreted by a shell.
/// </para>
/// </summary>
public sealed class BackgroundAgentRunner : IBackgroundAgentRunner
{
    public async Task<BackgroundRunResult> RunAsync(
        string promptFilePath,
        string? workingDir,
        TerminalLauncherOptions options,
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

        // Read both streams concurrently (draining them avoids a deadlock if the child fills a pipe
        // buffer), then feed the prompt in and close stdin so `claude -p` reads it to completion.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            // Both the stdin write and the exit wait honour `ct`, so a cancel at *any* point lands in the
            // single catch below and kills the child — an OperationCanceledException from WriteAsync must
            // not slip past (it isn't an IOException) and orphan the process.
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child may have exited before reading all of stdin (broken pipe); the output/exit
                // code below still reflect what it did, so this is not itself a failure.
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            // Observe the in-flight read tasks so their cancellation/pipe faults aren't left unobserved.
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            await ObserveAsync(stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return BackgroundRunResult.Exited(process.ExitCode, stdout, string.IsNullOrWhiteSpace(stderr) ? null : stderr);
    }

    /// <summary>
    /// The argument vector for a background one-off run: <c>-p</c> (headless print mode) followed by any
    /// configured extra args (e.g. a model flag). The prompt is <b>not</b> here — it is fed on stdin.
    /// Pure, so the composition is unit-tested.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(TerminalLauncherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string> { "-p" };
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
