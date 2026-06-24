using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Diagnostic for #3 — bare Terminal.Gui app to isolate the repaint lag from the rest of clickup-todo.
// No network, no background refresh thread, no redraw heartbeat: just a ListView. If arrow/type-ahead
// repaint is still laggy here, the problem is Terminal.Gui (or the terminal) itself; if it's snappy,
// the cause is something in TodoApp (the background refresh / Application.Invoke marshaling).
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

public static class BareRepro
{
    public static void Run(string? driverName)
    {
        Application.Init(driverName);
        try
        {
            var win = new Window { Title = $"BARE repro — driver arg: {driverName ?? "default"} — Ctrl+Q quits" };

            var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
            var items = new ObservableCollection<string>(
                Enumerable.Range(1, 200).Select(i => $"Item {i:000} — sample task title for type-ahead search"));
            list.SetSource(items);

            var help = new Label
            {
                X = 0,
                Y = Pos.AnchorEnd(1),
                Width = Dim.Fill(),
                Text = "No network / no refresh / no heartbeat. ↑/↓ to move, type letters to search. Ctrl+Q or Esc quits.",
            };

            list.KeyDown += (_, key) =>
            {
                if (key.KeyCode == KeyCode.Esc || (key.IsCtrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Q))
                {
                    key.Handled = true;
                    Application.RequestStop();
                }
            };

            win.Add(list, help);
            list.SetFocus();
            Application.Run(win);
            win.Dispose();
        }
        finally
        {
            Application.Shutdown();
        }
    }
}
