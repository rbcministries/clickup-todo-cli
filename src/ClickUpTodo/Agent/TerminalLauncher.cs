using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClickUpTodo.Agent;

/// <summary>
/// Default <see cref="ITerminalLauncher"/>. Resolves the OS and a real <c>PATH</c> probe, asks
/// <see cref="TerminalCommandPlanner"/> for the ordered candidates, then starts each via
/// <see cref="Process"/> until one launches. Mirrors the cross-platform <c>Process.Start</c> used for
/// open-in-browser in the TUI, with the per-platform terminal strategy in the planner.
///
/// Every external dependency (OS, PATH probe, env, process start, file check) is injectable so the
/// orchestration is unit-testable without spawning a real process.
/// </summary>
public sealed class TerminalLauncher : ITerminalLauncher
{
    private readonly OSPlatformKind _os;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<LaunchSpec, bool> _start;

    public TerminalLauncher(
        OSPlatformKind? os = null,
        Func<string, bool>? exists = null,
        Func<string, string?>? getEnv = null,
        Func<string, bool>? fileExists = null,
        Func<LaunchSpec, bool>? start = null)
    {
        _os = os ?? CurrentOS();
        _exists = exists ?? ExecutableOnPath;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
        _start = start ?? StartProcess;
    }

    public Task<LaunchResult> LaunchAsync(
        string promptFilePath, string? workingDir, TerminalLauncherOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promptFilePath) || !_fileExists(promptFilePath))
            return Task.FromResult(LaunchResult.Fail($"Prompt file not found: {promptFilePath}"));

        var candidates = TerminalCommandPlanner.Plan(_os, _exists, _getEnv, promptFilePath, workingDir, options);
        if (candidates.Count == 0)
            return Task.FromResult(LaunchResult.Fail("No terminal emulator found to launch Claude."));

        foreach (var spec in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (_start(spec))
            {
                var note = _exists(options.ClaudeExecutable)
                    ? null
                    : $"'{options.ClaudeExecutable}' was not found on PATH — it must be available in the new terminal.";
                return Task.FromResult(LaunchResult.Ok(spec.DisplayName, note));
            }
        }

        return Task.FromResult(LaunchResult.Fail("Found a terminal but failed to start it."));
    }

    private static OSPlatformKind CurrentOS()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return OSPlatformKind.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OSPlatformKind.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return OSPlatformKind.Linux;
        return OSPlatformKind.Unknown;
    }

    /// <summary>True if <paramref name="name"/> resolves to an executable on the current PATH.</summary>
    private static bool ExecutableOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // An explicit path: check it directly (with Windows extensions if none given).
        if (name.Contains('/') || name.Contains('\\'))
            return FileWithExtensions(name);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FileWithExtensions(Path.Combine(dir, name)))
                return true;
        }
        return false;
    }

    private static bool FileWithExtensions(string candidate)
    {
        if (File.Exists(candidate))
            return true;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
        {
            if (File.Exists(candidate + ext))
                return true;
        }
        return false;
    }

    private static bool StartProcess(LaunchSpec spec)
    {
        try
        {
            var psi = new ProcessStartInfo(spec.FileName) { UseShellExecute = false };
            foreach (var arg in spec.Arguments)
                psi.ArgumentList.Add(arg);
            if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
                psi.WorkingDirectory = spec.WorkingDirectory;
            return Process.Start(psi) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
