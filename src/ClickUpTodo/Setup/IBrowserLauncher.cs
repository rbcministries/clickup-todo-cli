using System.Diagnostics;
using System.Runtime.InteropServices;
using ClickUpTodo.Agent;

namespace ClickUpTodo.Setup;

/// <summary>Opens a URL in the user's default browser. Abstracted so the OAuth flow is testable.</summary>
public interface IBrowserLauncher
{
    /// <summary>Tries to open <paramref name="url"/>; <see langword="false"/> if no browser could be launched.</summary>
    bool TryOpen(Uri url);
}

/// <summary>
/// Default <see cref="IBrowserLauncher"/>. Asks <see cref="BrowserLaunchPlanner"/> for the ordered,
/// PATH-filtered list of openers for the current OS (Windows shell association, macOS <c>open</c>,
/// Linux <c>xdg-open</c> and friends) and starts each until one launches. The real
/// <see cref="Process.Start(ProcessStartInfo)"/> path can't run headlessly, so it lives behind this
/// seam; every external dependency (OS, PATH probe, env, process start) is injectable and the
/// per-platform planning is unit-tested without spawning a process.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    private readonly OSPlatformKind _os;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, string?> _getEnv;
    private readonly Func<BrowserCommand, bool> _start;

    public SystemBrowserLauncher(
        OSPlatformKind? os = null,
        Func<string, bool>? exists = null,
        Func<string, string?>? getEnv = null,
        Func<BrowserCommand, bool>? start = null)
    {
        _os = os ?? BrowserLaunchPlanner.CurrentOS();
        _exists = exists ?? ExecutableOnPath;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _start = start ?? StartProcess;
    }

    public bool TryOpen(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        foreach (var cmd in BrowserLaunchPlanner.Plan(_os, _exists, _getEnv, url))
        {
            if (_start(cmd))
                return true;
        }
        return false;
    }

    private static bool StartProcess(BrowserCommand cmd)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd.FileName) { UseShellExecute = cmd.UseShellExecute };
            if (!cmd.UseShellExecute)
            {
                foreach (var arg in cmd.Arguments)
                    psi.ArgumentList.Add(arg);
            }
            return Process.Start(psi) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>True if <paramref name="name"/> resolves to an executable on the current PATH.</summary>
    private static bool ExecutableOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Contains('/') || name.Contains('\\'))
            return File.Exists(name);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(dir, name)))
                return true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
                {
                    if (File.Exists(Path.Combine(dir, name + ext)))
                        return true;
                }
            }
        }
        return false;
    }
}
