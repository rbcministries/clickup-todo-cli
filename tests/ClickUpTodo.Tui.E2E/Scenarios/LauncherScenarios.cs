using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Setup;

namespace ClickUpTodo.Tui.E2E;

/// <summary>#304: when E2E_BROWSER_LOG is set, capture Ctrl+B launches to that file (one URL per line) so a
/// pyte check can assert the app.clickup.com → subdomain host rewrite. Otherwise the app can't launch a real
/// browser under the PTY, so the orchestrator falls back to a no-op launcher.</summary>
internal sealed class BrowserLogScenario : IE2EScenario
{
    public string Name => "browser-log";
    public bool IsActive => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_BROWSER_LOG"));

    public IBrowserLauncher? BrowserLauncher
        => Environment.GetEnvironmentVariable("E2E_BROWSER_LOG") is { Length: > 0 } path
            ? new RecordingBrowserLauncher(path)
            : null;
}

/// <summary>#320: when E2E_TAB_LOG is set, record each new-terminal-tab launch (the Ctrl+Click new-tab
/// destination) to that file — one <c>clickup-todo --task &lt;id&gt;</c> display command per line — so a
/// pyte check asserts the launch from the recorder rather than the (failing, terminal-less) real launcher's
/// clipboard fallback.</summary>
internal sealed class TabLogScenario : IE2EScenario
{
    public string Name => "tab-log";
    public bool IsActive => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_TAB_LOG"));

    public ITerminalLauncher? TabLauncher
        => Environment.GetEnvironmentVariable("E2E_TAB_LOG") is { Length: > 0 } path
            ? new RecordingTerminalLauncher(path)
            : null;
}

/// <summary>Two-instance nudge channel (#376 item 1): when E2E_MARKER_DB is set, wire a real shared-file
/// LiteDbChangeMarkerStore into BOTH the producer (ClickUpClient) and the consumer (TodoApp), so a Quick
/// Update committed in one app process nudges the other (nudge-then-fetch, #294/#295). LiteDB's shared
/// connection is the cross-process mutex, so two processes pointed at the same file coordinate safely.
/// E2E_INSTANCE_ID gives each process a distinct marker id (the consumer skips its own writes by id);
/// it defaults to a random id.</summary>
internal sealed class MarkerDbScenario : IE2EScenario
{
    private HarnessMarkers? _markers;
    private bool _built;

    public string Name => "marker-db";
    public bool IsActive => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("E2E_MARKER_DB"));

    public HarnessMarkers? Markers
    {
        get
        {
            if (_built)
                return _markers;
            _built = true;
            var path = Environment.GetEnvironmentVariable("E2E_MARKER_DB");
            if (string.IsNullOrWhiteSpace(path))
                return _markers = null;
            var instanceId = Environment.GetEnvironmentVariable("E2E_INSTANCE_ID");
            if (string.IsNullOrWhiteSpace(instanceId))
                instanceId = Guid.NewGuid().ToString("N");
            var store = new LiteDbStateStore(path);
            return _markers = new HarnessMarkers
            {
                Store = store.CreateChangeMarkerStore(instanceId),
                Disposable = store,
            };
        }
    }
}

/// <summary>A browser launcher that succeeds without opening anything — the orchestrator's default under the
/// PTY, where there is no browser to launch and a real <c>SystemBrowserLauncher</c> would just fail.</summary>
internal sealed class NullBrowserLauncher : IBrowserLauncher
{
    public bool TryOpen(Uri url) => true;
}

/// <summary>A browser launcher that records each launched URL (one per line) to a file so a pyte check can
/// assert the #304 host rewrite. Appends so repeated Ctrl+B presses are all observable.</summary>
internal sealed class RecordingBrowserLauncher(string path) : IBrowserLauncher
{
    public bool TryOpen(Uri url)
    {
        File.AppendAllText(path, url.ToString() + "\n");
        return true;
    }
}

/// <summary>Records each new-terminal-tab app launch (#320's Ctrl+Click new-tab destination) to a file — one
/// <c>clickup-todo --task &lt;id&gt;</c> display command per line — and reports success, so under the PTY the
/// launch is observable from the recorder instead of failing to a clipboard fallback. Only
/// <see cref="LaunchAppAsync"/> is exercised here; the prompt-file <c>LaunchAsync</c> (agent dispatch) runs
/// through TodoApp's own real launcher, never this one.</summary>
internal sealed class RecordingTerminalLauncher(string path) : ITerminalLauncher
{
    public Task<LaunchResult> LaunchAsync(
        string promptFilePath, string? workingDir, TerminalLauncherOptions options,
        bool oneOff = false, CancellationToken ct = default)
        => throw new NotSupportedException("RecordingTerminalLauncher records only app-tab launches.");

    public Task<LaunchResult> LaunchAppAsync(
        AppLaunchCommand command, TerminalLauncherOptions options, CancellationToken ct = default)
    {
        File.AppendAllText(path, command.ToDisplayCommand() + "\n");
        return Task.FromResult(new LaunchResult(true, "e2e-recorder", null));
    }
}
