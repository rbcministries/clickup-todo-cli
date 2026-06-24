using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Diagnostic for #3 — bare Terminal.Gui app to isolate the repaint lag. The bare variant (no network,
// no refresh thread, no heartbeat) is snappy, so the lag comes from something TodoApp adds. These
// variants add one suspect at a time to find the culprit:
//   bare       just a ListView
//   heartbeat  + a 50ms Application.LayoutAndDraw() timeout
//   refresh    + a background thread that calls Application.Invoke(SetSource) on an interval
//   both       + heartbeat and refresh
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

public static class BareRepro
{
    public static void Run(string? driverName, string mode)
    {
        var heartbeat = mode is "heartbeat" or "both";
        var refresh = mode is "refresh" or "both";

        Application.Init(driverName);
        using var cts = new CancellationTokenSource();
        try
        {
            var win = new Window { Title = $"BARE repro [{mode}] — driver: {driverName ?? "default"} — Ctrl+Q quits" };

            var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
            ObservableCollection<string> Build(int gen) => new(
                Enumerable.Range(1, 200).Select(i => $"Item {i:000} — sample task title (gen {gen})"));
            list.SetSource(Build(0));

            var help = new Label
            {
                X = 0,
                Y = Pos.AnchorEnd(1),
                Width = Dim.Fill(),
                Text = $"mode={mode}: heartbeat={heartbeat} refresh={refresh}. ↑/↓ + type to test. Ctrl+Q/Esc quits.",
            };

            list.KeyDown += (_, key) =>
            {
                if (key.KeyCode == KeyCode.Esc || (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Q))
                {
                    key.Handled = true;
                    Application.RequestStop();
                }
            };

            if (heartbeat)
                Application.AddTimeout(TimeSpan.FromMilliseconds(50), () => { Application.LayoutAndDraw(); return true; });

            if (refresh)
            {
                // Mimic TodoApp's refresh: periodic background work that marshals a SetSource via Invoke.
                _ = Task.Run(async () =>
                {
                    var gen = 1;
                    while (!cts.IsCancellationRequested)
                    {
                        try { await Task.Delay(2000, cts.Token); await Task.Delay(800, cts.Token); }
                        catch (OperationCanceledException) { break; }
                        var snapshot = Build(gen++);
                        Application.Invoke(() => list.SetSource(snapshot));
                    }
                }, cts.Token);
            }

            win.Add(list, help);
            list.SetFocus();
            Application.Run(win);
            win.Dispose();
        }
        finally
        {
            cts.Cancel();
            Application.Shutdown();
        }
    }
}
