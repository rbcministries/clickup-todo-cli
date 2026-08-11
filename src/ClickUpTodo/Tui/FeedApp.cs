using System.Diagnostics;
using ClickUpTodo.Agent;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using ClickUpTodo.Tui.Screens;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui 2.4 deprecates the static `Application` facade in favour of an instance-based API that
// is not yet stable or documented. The static API remains the supported v2 pattern, so we intentionally
// use it and silence the deprecation here until the instance API settles (mirrors TodoApp/SingleTaskApp).
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>
/// Minimal host that boots straight into the mentions &amp; comments feed (<c>--feed</c>, #509) — a third
/// root host in the shape of <see cref="SingleTaskApp"/> (<c>--task</c>, #296), part of the split-pane
/// epic (#502, sub-issue G). Until now <see cref="NotificationsFeedScreen"/> existed only <b>inside</b>
/// the dashboard (<see cref="TodoApp"/>, opened with <c>Ctrl+E</c>), which made it modal to the
/// dashboard; a standalone host lets the feed be launched in its own window / tab (and, once #502 lands,
/// a pane) so it can sit <em>beside</em> your work rather than instead of it.
/// <para>
/// This is a <b>hosting change only</b>: it mounts the already-decoupled feed screen (plain data +
/// events + its own auto-refresh timer — it touches no dashboard list/selection) as its own root, so it
/// has zero blast radius on the dashboard. The dashboard's <c>Ctrl+E</c> in-dashboard feed is unchanged
/// (the issue's "safe answer"): <c>--feed</c> is an <em>additional</em> launch path, not a replacement.
/// </para>
/// <para>
/// The feed screen owns its own longer-cadence auto-refresh (<c>OnShown</c> →
/// <c>Application.AddTimeout</c> → <see cref="NotificationsFeedScreen.RefreshRequested"/>), so — exactly
/// as <see cref="SingleTaskApp"/> does with the detail's 30s tick — this host only wires that event to a
/// re-fetch. On top of that it consumes the cross-process nudge channel (#294/#295) so an edit made in
/// another instance surfaces here promptly rather than only on the feed's own tick.
/// </para>
/// </summary>
public sealed class FeedApp
{
    // The nudge-channel consumer (#377): a short poll cadence decoupled from the feed's own auto-refresh,
    // mirroring SingleTaskApp. UI-thread-only.
    private static readonly TimeSpan MarkerPollInterval = TimeSpan.FromSeconds(4);

    private readonly FeedService _feed;
    private readonly FeedCache _feedCache;
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly IChangeMarkerStore _changeMarkers;
    private readonly ChangeMarkerConsumer _markerConsumer;

    // The feed as first painted — a warm-cache snapshot (comments only) or the empty feed. The live data
    // lands via the initial background refresh kicked on first show (see KickInitialRefresh).
    private readonly FeedResult _seed;

    private Window _window = null!;
    // The shared status + contextual help footer (#346). Built in Build.
    private ContextualFooter _footer = null!;

    // The feed screen — this host's stack root. Help (F1) and the exit confirmation (#299) stack over it.
    private NotificationsFeedScreen _feedScreen = null!;

    // Screens stacked over the root feed: Help (F1) and the exit confirmation (#299). Empty ⇒ the feed is
    // front-most.
    private readonly List<Screen> _stack = [];

    // Coalesces overlapping feed fetches (F5/Ctrl+R or an F12 toggle racing the auto-refresh tick or a
    // nudge): while one is in flight, remember a second was asked for and run it once the first lands,
    // rather than piling up or dropping a state-changing request. UI-thread-only (mirrors TodoApp).
    private bool _refreshingFeed;
    private bool _feedRefreshPending;

    // One-in-flight guard for the marker poll so two scans can't overlap (#377). UI-thread-only.
    private bool _pollingMarkers;

    // The cross-platform "open this task in its own terminal tab" launcher — Enter on a feed row launches
    // `clickup-todo --task <id>` (#301/#435), the no-pane-yet analogue of the issue's "launches --task"
    // gesture (the split-pane destination ladder rides #502). `_launchingTab` guards against a rapid
    // second Enter spawning duplicate tabs. UI-thread-only.
    private readonly ITerminalLauncher _tabLauncher;
    private bool _launchingTab;

    private string _status;

    public FeedApp(FeedService feed, FeedCache feedCache, AppConfig config, ConfigStore configStore,
        FeedResult seed, IChangeMarkerStore? changeMarkers = null, ITerminalLauncher? tabLauncher = null)
    {
        _feed = feed;
        _feedCache = feedCache;
        _config = config;
        _configStore = configStore;
        _seed = seed;
        _tabLauncher = tabLauncher ?? new TerminalLauncher();
        _status = "Feed";

        // The nudge channel (#377). A no-op store (the file-backed state store's Null channel, or an app
        // built without a channel) has an empty InstanceId, which disarms the poll (see ArmMarkerPoll) — so
        // cross-process freshness only kicks in where a real cross-process channel exists.
        _changeMarkers = changeMarkers ?? NullChangeMarkerStore.Instance;
        _markerConsumer = new ChangeMarkerConsumer(_changeMarkers.InstanceId);
    }

    private Screen ActiveScreen => _stack.Count > 0 ? _stack[^1] : _feedScreen;

    public void Run(string? driverName = null)
    {
        // Install the frame-diffing ANSI output for the default/ansi driver, exactly as the other hosts
        // do (~0.9 KB per keypress instead of a full repaint on slow terminals/links). Best-effort;
        // CLICKUP_TODO_NO_DIFF=1 opts out.
        var diffing = (driverName is null or "ansi")
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLICKUP_TODO_NO_DIFF"))
            && DiffFlushAnsiBackend.TryInstall();
        Application.Init(driverName);
        try
        {
            _status = $"{_status} (driver: {driverName ?? "default (ansi)"}{(diffing ? ", diffed output" : "")})";
            Build();
            ArmMarkerPoll();
            KickInitialRefresh();
            Application.Run(_window);
        }
        finally
        {
            // Shutdown restores the terminal no matter how Dispose fares, so it must run even if the shared
            // teardown guard swallows Terminal.Gui 2.4.10's tabbed-view dispose bug (#346).
            try
            {
                TuiTeardown.DisposeSwallowingTeardownBug(_window, "Window");
            }
            finally
            {
                Application.Shutdown();
            }
        }
    }

    private void Build()
    {
        // Title the window from the feed state (#425): "Feed", plus a mention-count badge so a --feed tab
        // carries an at-a-glance unread-mention signal and several tabs stay distinguishable. Terminal.Gui
        // propagates the top-level window Title to the host terminal's window/tab title.
        _window = new Window { Title = TerminalTitle.ForFeed(CountMentions(_seed.Comments)) };

        _feedScreen = CreateFeedScreen(_seed);
        // The feed is this host's root, so its close is back-at-root: Esc (and the inherited Ctrl+E, which
        // in the dashboard toggled back to the list — there is no list here) hands off to the exit seam
        // (#298/#299) rather than tearing down. The root is added straight to the window (not via
        // ShowScreen), so it wires its own flash relay to the shared footer.
        _feedScreen.Closed += (_, _) => RequestExit();
        _feedScreen.FlashRequested += (_, message) => Flash(message);

        _footer = new ContextualFooter(_status);

        _window.Add(_feedScreen);
        _footer.AddTo(_window);
        // Re-fit the contextual footer whenever the window re-lays out (terminal resize); the text is only
        // reassigned when it changes, so this can't loop (mirrors the other hosts). The first laid-out
        // frame also drives the feed's OnShown (which focuses the list and arms its auto-refresh). One-shot.
        var shown = false;
        _window.SubViewsLaidOut += (_, _) =>
        {
            UpdateHelpLine();
            if (!shown)
            {
                shown = true;
                _feedScreen.OnShown();
            }
        };
        UpdateHelpLine();
    }

    /// <summary>
    /// Builds the root <see cref="NotificationsFeedScreen"/> with this host's event wiring, mirroring
    /// <c>TodoApp.CreateFeedScreen</c>: F5 / Ctrl+R and the auto-refresh tick re-fetch via
    /// <see cref="RefreshFeed"/>; F12 toggles completed-task activity (persist + re-fetch); F6 toggles the
    /// recent-activity source (persist + local re-render, no re-fetch); Enter on a row opens that comment's
    /// task in a new terminal tab; F1 opens Help.
    /// </summary>
    private NotificationsFeedScreen CreateFeedScreen(FeedResult feed)
    {
        var screen = new NotificationsFeedScreen(
            feed.Comments, feed.Activity, _config.FeedRefreshSeconds,
            showCompleted: _config.FeedShowCompleted, showActivity: _config.FeedShowActivity);
        screen.RefreshRequested += (_, _) => RefreshFeed(screen);
        screen.ToggleCompletedRequested += (_, _) => ToggleFeedShowCompleted(screen);
        screen.ToggleActivityRequested += (_, _) => ToggleFeedShowActivity(screen);
        // Enter on a feed row (#115): open that comment's task. In the dashboard this stacks the task's
        // detail in-app; a standalone feed host has no list to stack on, so — matching the issue's
        // "launches --task" intent — it opens the task in its own terminal tab (the split-pane destination
        // ladder rides #502).
        screen.OpenTaskRequested += (_, taskId) => LaunchAppTabForTask(taskId);
        screen.HelpRequested += (_, _) => OpenHelp();
        return screen;
    }

    /// <summary>Kicks the initial live feed load on boot. The host is seeded with a warm-cache snapshot (or
    /// the empty feed), so this near-immediate background refresh replaces it with live data and re-saves
    /// the cache — mirroring the dashboard's warm-open (<c>OpenNotificationsFeed</c>). Runs on the UI thread
    /// during <see cref="Run"/>, before the run loop pumps; the fetch itself is off-thread.</summary>
    private void KickInitialRefresh()
    {
        Flash(_seed.Comments.Count > 0 ? "Showing cached feed · refreshing…" : "Loading feed…");
        RefreshFeed(_feedScreen);
    }

    /// <summary>
    /// Re-fetches the feed off the UI thread and feeds it back, re-saving the cache after each successful
    /// aggregation (#123) and retitling the window from the new mention count (#425). Mirrors
    /// <c>TodoApp.RefreshFeed</c> minus the close/reopen handling (this host's feed screen is a permanent
    /// root, never torn down and reopened): skips while the feed isn't front-most (Help/exit overlay up),
    /// coalesces overlapping fetches, and flashes a fetch error without disturbing the view.
    /// </summary>
    private void RefreshFeed(NotificationsFeedScreen screen)
    {
        // Runs on the UI thread. No point fetching to update a feed that isn't showing (e.g. Help/exit up).
        // Record that a refresh was wanted so CloseScreen runs one when the feed returns front-most — an
        // auto-tick or a cross-process nudge that lands under an overlay would otherwise be dropped (its
        // marker cursor already advanced) until the feed's own next, possibly-distant, auto-refresh tick.
        if (!ReferenceEquals(ActiveScreen, screen))
        {
            _feedRefreshPending = true;
            return;
        }

        if (_refreshingFeed)
        {
            _feedRefreshPending = true;
            return;
        }
        _refreshingFeed = true;

        // Capture the completed flag + its matching cache key on the UI thread at fetch-start, so the result
        // is fetched with, and saved under, one consistent fingerprint even if F12 flips mid-fetch (the
        // pending re-fetch picks up the new flag on its own pass). Mirrors TodoApp.
        var includeClosed = _config.FeedShowCompleted;
        var cacheKey = FeedCache.KeyFor(_config);

        _ = Task.Run(async () =>
        {
            try
            {
                var feed = await _feed.LoadFeedAsync(includeClosed, mentionsOnly: false);
                Application.Invoke(() =>
                {
                    // Cache the freshly-aggregated comments under the fingerprint they were fetched with;
                    // the activity source (#117) rides the in-memory result but isn't persisted (display-only).
                    _feedCache.Save(cacheKey, feed.Comments);
                    screen.UpdateFeed(feed);
                    RetitleFromFeed(feed.Comments);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => Flash($"Could not refresh feed: {ErrorText.Short(ex)}"));
            }
            finally
            {
                Application.Invoke(() =>
                {
                    _refreshingFeed = false;
                    // Run the queued refresh (e.g. an F12 toggle that arrived mid-fetch) so its new flag
                    // actually takes effect — only while the feed is still front-most.
                    if (_feedRefreshPending && ReferenceEquals(ActiveScreen, screen))
                    {
                        _feedRefreshPending = false;
                        RefreshFeed(screen);
                    }
                    else
                    {
                        _feedRefreshPending = false;
                    }
                });
            }
        });
    }

    /// <summary>
    /// F12 "Show Completed": whether the feed includes activity from completed (closed-type) tasks. The
    /// closed tasks were never fetched while off, so a client-side re-render can't surface them — this
    /// persists the flag (<see cref="AppConfig.FeedShowCompleted"/>) and re-fetches. Mirrors
    /// <c>TodoApp.ToggleFeedShowCompleted</c>. No-op if the feed isn't front-most.
    /// </summary>
    private void ToggleFeedShowCompleted(NotificationsFeedScreen screen)
    {
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        var on = !_config.FeedShowCompleted;
        _config.FeedShowCompleted = on;
        _configStore.Save(_config);
        screen.SetShowCompleted(on);
        Flash(on ? "Feed: showing completed tickets (F12)." : "Feed: completed tickets hidden (F12).");
        RefreshFeed(screen);
    }

    /// <summary>
    /// F6 "show activity": whether the recent-activity source (#117) is merged into the feed. Unlike F12
    /// the activity is already loaded, so this is a pure client-side re-render (no re-fetch) — persist the
    /// flag (<see cref="AppConfig.FeedShowActivity"/>) and reflect it back via
    /// <see cref="NotificationsFeedScreen.SetShowActivity"/>. Mirrors <c>TodoApp.ToggleFeedShowActivity</c>.
    /// No-op if the feed isn't front-most.
    /// </summary>
    private void ToggleFeedShowActivity(NotificationsFeedScreen screen)
    {
        if (!ReferenceEquals(ActiveScreen, screen))
            return;

        var on = !_config.FeedShowActivity;
        _config.FeedShowActivity = on;
        _configStore.Save(_config);
        screen.SetShowActivity(on);
        Flash(on ? "Feed: recent activity enabled (F6)." : "Feed: recent activity disabled (F6).");
    }

    /// <summary>Retitles the window from the feed's current mention count (#425), pushing a new value only
    /// when it actually moved — Terminal.Gui also dedups on its own last title, so this is belt-and-braces
    /// against churn.</summary>
    private void RetitleFromFeed(IReadOnlyList<CommentItem> comments)
    {
        if (TerminalTitle.RetitleFeed(_window.Title, CountMentions(comments)) is { } title)
            _window.Title = title;
    }

    /// <summary>The number of feed comments that mention the signed-in user — the at-a-glance badge the
    /// window title carries (#425), counted over the whole loaded feed regardless of the F3 filter.</summary>
    private static int CountMentions(IReadOnlyList<CommentItem> comments)
    {
        var n = 0;
        foreach (var c in comments)
            if (c.MentionsMe)
                n++;
        return n;
    }

    // ── Cross-process nudge channel — consumer (#377) ─────────────────────────

    /// <summary>
    /// Arms the nudge-channel consumer for the feed host (#377), mirroring <c>SingleTaskApp.ArmMarkerPoll</c>:
    /// seed the cursor to the current max marker seq so a fresh host never replays history (#295 edge case
    /// 1), then start a repeating marker poll on <see cref="MarkerPollInterval"/> — its own short cadence,
    /// decoupled from the feed's longer auto-refresh. A no-op store (the file-backed state store's Null
    /// channel) has an empty InstanceId, so nothing is armed and the host keeps only its auto-refresh
    /// freshness. Runs on the UI thread during <see cref="Run"/>, before the run loop pumps.
    /// </summary>
    private void ArmMarkerPoll()
    {
        if (string.IsNullOrEmpty(_changeMarkers.InstanceId))
            return; // no cross-process channel (e.g. the JSON file store) — nothing to consume.

        _markerConsumer.Initialize(_changeMarkers.ReadAll());
        Application.AddTimeout(MarkerPollInterval, () =>
        {
            PollMarkers();
            return true;
        });
    }

    /// <summary>
    /// One marker-poll tick (#377): read the markers <b>off</b> the UI thread (a <c>changes</c> ReadAll
    /// briefly takes LiteDB's shared-mode cross-process lock), then run the pure cursor scan <b>on</b> the
    /// UI thread. The feed is a cross-task <b>aggregate</b> — any external edit could add or refresh a row,
    /// and there is no single held version to suppress on — so every task is treated as "in view" and
    /// nothing is suppressed: a non-empty advance means "something changed in another instance", which
    /// triggers exactly one coalesced feed refresh (its own in-flight guard applies). A single in-flight
    /// guard keeps two scans from overlapping; best-effort throughout — a read failure is swallowed, since
    /// a nudge rides on an edit that already succeeded elsewhere.
    /// </summary>
    private void PollMarkers()
    {
        if (_pollingMarkers)
            return;
        _pollingMarkers = true;

        _ = Task.Run(() =>
        {
            IReadOnlyList<ChangeMarker> markers;
            try { markers = _changeMarkers.ReadAll(); }
            catch { markers = []; }

            Application.Invoke(() =>
            {
                try
                {
                    if (_markerConsumer.Advance(markers, static _ => true, static _ => null).Count > 0)
                        RefreshFeed(_feedScreen);
                }
                finally
                {
                    _pollingMarkers = false;
                }
            });
        });
    }

    // ── Open a task in a new terminal tab (Enter on a row, #435) ──────────────

    /// <summary>
    /// Enter on a feed row: opens that comment's task in its own terminal tab —
    /// <c>clickup-todo --task &lt;id&gt;</c> (#301) — through the same cross-platform launcher and
    /// copy-command fallback the other hosts use (#384/#435), sharing the option/message helper
    /// (<see cref="AppTabLaunch"/>) so the paths can't drift. Re-entrancy-guarded so a rapid second Enter
    /// can't spawn duplicate tabs; the launch runs off the UI thread and reports back on the shared footer.
    /// The launched <c>--task</c> tab titles itself from the task, so the taskId here is only the flash label.
    /// </summary>
    private void LaunchAppTabForTask(string taskId)
    {
        if (_launchingTab)
        {
            Flash("A task tab is already opening…");
            return;
        }

        // Resolve the command before arming the guard: ForTask is pure and could throw on a blank id, and
        // doing it first means such a throw can't leave _launchingTab stuck true (mirrors the other hosts).
        var command = AppLaunchCommand.ForTask(taskId);
        var options = AppTabLaunch.Options(
            _config.AgentDispatch.PreferredTerminal, _config.AgentDispatch.CustomTerminalCommand);
        _launchingTab = true;
        Flash(AppTabLaunch.Opening(taskId));
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _tabLauncher.LaunchAppAsync(command, options);
                Application.Invoke(() =>
                {
                    _launchingTab = false;
                    if (result.Success)
                        Flash(AppTabLaunch.Opened(taskId, result));
                    else
                        FlashLaunchFallback(command);
                });
            }
            catch (Exception ex)
            {
                Application.Invoke(() => { _launchingTab = false; FlashLaunchFallback(command, ErrorText.Short(ex)); });
            }
        });
    }

    /// <summary>The no-terminal fallback (#301): flash the exact relaunch command and copy it to the
    /// clipboard so the user can open the task tab themselves.</summary>
    private void FlashLaunchFallback(AppLaunchCommand command, string? reason = null)
        => Flash(AppTabLaunch.Fallback(command, TryCopyToClipboard(command.ToDisplayCommand()), reason));

    /// <summary>Best-effort clipboard copy for the fallback; a headless/unsupported clipboard yields false
    /// so the caller shows the run-it-yourself form instead (mirrors the other hosts).</summary>
    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            return Clipboard.TrySetClipboardData(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Help overlay (F1) ─────────────────────────────────────────────────────

    /// <summary>F1 stacks a <see cref="HelpScreen"/> over the feed; Esc pops back to it.</summary>
    private void OpenHelp()
    {
        if (ActiveScreen is HelpScreen)
            return;
        ShowScreen(new HelpScreen());
    }

    private void ShowScreen(Screen screen, Action? onClosed = null)
    {
        // Hide the currently-visible layer so only the new top draws/focuses (one visible screen — #3).
        (ActiveScreen as View).Visible = false;
        _stack.Add(screen);

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!_stack.Contains(screen))
                return;
            screen.Closed -= handler;
            // Defer teardown out of the screen's own key handler (disposing mid-keypress can leave
            // Terminal.Gui's focus machinery pointing at a freed view), like the other hosts. Run the
            // caller's onClosed first while the screen is intact.
            Application.Invoke(() =>
            {
                onClosed?.Invoke();
                CloseScreen(screen);
            });
        };
        screen.Closed += handler;
        screen.FlashRequested += (_, message) => Flash(message);

        _window.Add(screen);
        UpdateHelpLine();
        screen.OnShown();
    }

    private void CloseScreen(Screen screen)
    {
        if (!_stack.Remove(screen))
            return;

        _window.Remove(screen);
        try
        {
            screen.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            Debug.WriteLine($"Screen dispose threw (Terminal.Gui teardown bug), ignoring: {ex}");
        }

        var below = ActiveScreen as View;
        below.Visible = true;
        below.SetFocus();
        UpdateHelpLine();
        // A feed screen that was skipped for refresh while an overlay was up can now catch up: if the
        // feed is front-most again and a refresh was queued mid-overlay, run it.
        if (_stack.Count == 0 && _feedRefreshPending && !_refreshingFeed)
        {
            _feedRefreshPending = false;
            RefreshFeed(_feedScreen);
        }
    }

    /// <summary>
    /// The single "quit from the feed root" chokepoint — the exit-confirmation seam (#298, #299). <c>Esc</c>
    /// is the canonical Back key; the feed is this host's root, so Back <em>at the root</em> is a quit (there
    /// is no list to return to). It asks first via the same <see cref="ExitConfirmScreen"/> the other hosts
    /// use — mounted over the hidden feed; yes stops the app, no restores the feed. Re-entrancy-guarded so
    /// repeated Esc can't stack two questions. #407: when confirmation is off, Esc/close quits directly.
    /// </summary>
    private void RequestExit()
    {
        if (ActiveScreen is ExitConfirmScreen)
            return;

        if (!_config.ConfirmOnExit)
        {
            Application.RequestStop();
            return;
        }

        var confirm = new ExitConfirmScreen();
        ShowScreen(confirm, () =>
        {
            if (confirm.Confirmed)
                Application.RequestStop();
        });
    }

    // ── Footer / status ────────────────────────────────────────────────────────

    private void UpdateHelpLine() => _footer.RenderHelp(ActiveScreen.HelpItems);

    private void Flash(string message) => _footer.Flash(message);
}
