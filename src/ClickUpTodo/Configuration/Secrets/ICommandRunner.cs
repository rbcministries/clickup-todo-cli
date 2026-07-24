using System.Diagnostics;

namespace ClickUpTodo.Configuration.Secrets;

/// <summary>The outcome of running an external command: its exit code and captured output.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StdOut">Everything the process wrote to standard output.</param>
/// <param name="StdErr">Everything the process wrote to standard error.</param>
public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// A thin seam for running a secret-store CLI (<c>security</c> on macOS, <c>secret-tool</c> on Linux)
/// with an explicit argument vector and, where the CLI supports it, a secret fed over <b>stdin</b> so
/// it can't leak into the process list. (Linux <c>secret-tool</c> reads over stdin; macOS
/// <c>security add-generic-password</c> has no non-interactive stdin path and takes the value on argv —
/// a transient, same-user exposure accepted as the only option there.) Abstracted so the CLI backends'
/// argv construction and exit-code parsing are unit-testable with a fake runner; the real
/// <see cref="Process"/> path lives behind this seam and can't run headlessly (mirrors
/// <c>SystemBrowserLauncher</c> / <c>TerminalLauncher</c>).
/// </summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="args"/>, writing <paramref name="stdin"/>
    /// to the process' standard input when non-null. Returns the result, or <see langword="null"/> when
    /// the executable can't be found or fails to start.
    /// </summary>
    CommandResult? Run(string fileName, IReadOnlyList<string> args, string? stdin = null);
}

/// <summary>
/// Default <see cref="ICommandRunner"/> over <see cref="Process"/>. Captures stdout/stderr and, when a
/// secret is supplied, writes it to stdin with no trailing newline. Secret-store payloads are tiny, so
/// the sequential stdout-then-stderr read can't deadlock in practice.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult? Run(string fileName, IReadOnlyList<string> args, string? stdin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            // Drain stderr concurrently so a child that fills its stderr pipe buffer can't deadlock a
            // blocking stdout read (secret-store output is tiny today, but the seam is generic).
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = stderrTask.GetAwaiter().GetResult();
            process.WaitForExit();
            return new CommandResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or PlatformNotSupportedException or ObjectDisposedException or IOException)
        {
            // Includes a broken-pipe IOException if the CLI exits before reading stdin. The caller
            // (TokenStore.Save) treats a null result as "store unavailable" and degrades to the
            // disclosed plaintext fallback — a failed secure write never silently loses the token.
            return null;
        }
    }
}
