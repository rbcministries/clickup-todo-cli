using System.Diagnostics;

namespace ClickUpTodo.Setup;

/// <summary>Opens a URL in the user's default browser. Abstracted so the OAuth flow is testable.</summary>
public interface IBrowserLauncher
{
    /// <summary>Tries to open <paramref name="url"/>; <see langword="false"/> if no browser could be launched.</summary>
    bool TryOpen(Uri url);
}

/// <summary>
/// Default <see cref="IBrowserLauncher"/> using the OS shell association, mirroring the
/// open-in-browser launch in <c>TodoApp</c>. The real <see cref="Process.Start(ProcessStartInfo)"/>
/// path can't run headlessly, so it lives behind this seam and is verified manually.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public bool TryOpen(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        try
        {
            return Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or ObjectDisposedException)
        {
            return false;
        }
    }
}
