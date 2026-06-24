using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Diagnostic for #3 — isolate the repaint lag. The bare/heartbeat/refresh/both variants are all
// snappy, so the lag is in the real app's structure. The "panes" variant mirrors TodoApp's actual
// layout (two FrameViews + two ListViews + chord/Tab key handler + focus toggle + refresh + heartbeat)
// with dummy data and no network — to confirm whether the two-pane UI / key handling is the cause.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

public static class BareRepro
{
    private static ObservableCollection<string> Items(int gen, int count) =>
        new(Enumerable.Range(1, count).Select(i => $"Item {i:000} — sample task title for type-ahead (gen {gen})"));

    public static void Run(string? driverName, string mode)
    {
        if (mode == "panes")
        {
            RunPanes(driverName);
            return;
        }

        var heartbeat = mode is "heartbeat" or "both";
        var refresh = mode is "refresh" or "both";

        Application.Init(driverName);
        using var cts = new CancellationTokenSource();
        try
        {
            var win = new Window { Title = $"BARE repro [{mode}] — driver: {driverName ?? "default"} — Ctrl+Q quits" };
            var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
            list.SetSource(Items(0, 200));
            var help = new Label
            {
                X = 0,
                Y = Pos.AnchorEnd(1),
                Width = Dim.Fill(),
                Text = $"mode={mode}: heartbeat={heartbeat} refresh={refresh}. ↑/↓ + type to test. Ctrl+Q/Esc quits.",
            };
            list.KeyDown += (_, key) => QuitOn(key);

            if (heartbeat)
                Application.AddTimeout(TimeSpan.FromMilliseconds(50), () => { Application.LayoutAndDraw(); return true; });
            if (refresh)
                StartRefreshSim(cts, gen => Application.Invoke(() => list.SetSource(Items(gen, 200))));

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

    private static void RunPanes(string? driverName)
    {
        Application.Init(driverName);
        using var cts = new CancellationTokenSource();
        try
        {
            var win = new Window { Title = "PANES repro — mirrors the real two-pane layout — Ctrl+Q quits" };

            var focusFrame = new FrameView { Title = "★ Current Focus", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Absolute(7) };
            var focusList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            focusFrame.Add(focusList);

            var todoFrame = new FrameView { Title = "To-Do", X = 0, Y = Pos.Bottom(focusFrame), Width = Dim.Fill(), Height = Dim.Fill(2) };
            var todoList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            todoFrame.Add(todoList);

            var statusLabel = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = "panes repro" };
            var help = new Label { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1), Text = "↑/↓ move · Tab pane · type to search · Ctrl+Q quit" };

            focusList.SetSource(new ObservableCollection<string>(["Pinned item one — sample focus task"]));
            todoList.SetSource(Items(0, 80));

            void OnKey(object? _, Key key)
            {
                if (key.IsCtrl)
                {
                    var b = key.KeyCode & ~KeyCode.CtrlMask;
                    if (b is KeyCode.Q or KeyCode.C) { key.Handled = true; Application.RequestStop(); }
                    return;
                }
                switch (key.KeyCode)
                {
                    case KeyCode.Tab:
                        key.Handled = true;
                        if (focusList.HasFocus) todoList.SetFocus(); else focusList.SetFocus();
                        break;
                    case KeyCode.Esc:
                        key.Handled = true; Application.RequestStop(); break;
                    // Mirror the real app: these shortcuts are "handled" (no-op here).
                    case KeyCode.Space or KeyCode.Enter or KeyCode.F1 or KeyCode.F2:
                        key.Handled = true; break;
                }
            }
            focusList.KeyDown += OnKey;
            todoList.KeyDown += OnKey;

            // Match the real app: heartbeat + background refresh that rebuilds the list via Invoke.
            Application.AddTimeout(TimeSpan.FromMilliseconds(50), () => { Application.LayoutAndDraw(); return true; });
            StartRefreshSim(cts, gen => Application.Invoke(() =>
            {
                var sel = todoList.SelectedItem;
                todoList.SetSource(Items(gen, 80));
                if (sel is int i && i < 80) todoList.SelectedItem = i;
            }));

            win.Add(focusFrame, todoFrame, statusLabel, help);
            todoList.SetFocus();
            Application.Run(win);
            win.Dispose();
        }
        finally
        {
            cts.Cancel();
            Application.Shutdown();
        }
    }

    private static void StartRefreshSim(CancellationTokenSource cts, Action<int> onTick) =>
        _ = Task.Run(async () =>
        {
            var gen = 1;
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(2000, cts.Token); await Task.Delay(800, cts.Token); }
                catch (OperationCanceledException) { break; }
                onTick(gen++);
            }
        }, cts.Token);

    private static void QuitOn(Key key)
    {
        if (key.KeyCode == KeyCode.Esc || (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Q))
        {
            key.Handled = true;
            Application.RequestStop();
        }
    }
}
