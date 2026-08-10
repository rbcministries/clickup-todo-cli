#!/usr/bin/env python3
"""#404 spike A/B: F1 Help hosted as the hand-mounted _screens HelpScreen (A) vs. as a native
Terminal.Gui Dialog run on a nested Application.Run loop (B, CLICKUP_TODO_NATIVE_MODAL=1).

This is the measurement instrument for the native-modals spike (docs/plans/native-modals-spike.md).
It answers the architectural-viability questions the #402 transient-modal decision hinges on:

  1. Open→paint latency   — F1 until the help body is visible (A vs B).
  2. Bytes to open        — output volume of the F1 open (A vs B); diff-flush is on for both.
  3. Close + dispose safe  — Esc restores the task list and the process is STILL ALIVE, i.e. the
                             nested-run dialog dispose did not trip the Terminal.Gui 2.4.10 bug (#346).
  4. Outer responsiveness  — after the modal cycle a Down-arrow still redraws the list promptly
                             (the #3 single-ListView latency invariant), A vs B.

Both legs must render the help ("TASK LIST" from HelpScreen.ShortcutsText, shared by both hosts).
The check PASSES on functional correctness (open/close/alive/responsive for both hosts); the latency
and byte numbers are printed for the findings doc, with a generous regression ceiling so the check is
a stable guard rather than a flaky micro-benchmark.

Self-contained; sets its own env. Run:
  DLL=tests/ClickUpTodo.Tui.E2E/bin/Release/net10.0/ClickUpTodo.Tui.E2E.dll
  timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/native_modal_spike_check.py $DLL
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

F1 = b"\x1bOP"
ESC = b"\x1b"
DOWN = b"\x1b[B"

HELP_MARKER = "TASK LIST"          # a distinctive line in HelpScreen.ShortcutsText
HELP_TITLE = "Keyboard shortcuts"  # the screen/dialog title
LIST_MARKER = "next section"       # the main-list footer hint, present only on the list
# A latency ceiling well above the ~50 ms drive.py baseline: a stable guard, not a micro-benchmark.
LATENCY_CEILING_MS = 1500


class App:
    def __init__(self, **extra_env):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", **extra_env)
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def answer(self, data):
        # Terminal.Gui's ANSI driver asks for size/cursor/DA; an unanswered query = a blank app.
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")
        if b"\x1b[0c" in data or b"\x1b[c" in data:
            os.write(self.master, b"\x1b[?62;22c")

    def pump(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            r, _, _ = select.select([self.master], [], [], 0.05)
            if r:
                try:
                    chunk = os.read(self.master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                self.answer(chunk)
                self.stream.feed(chunk)

    def visible(self):
        return "\n".join(line.rstrip() for line in self.screen.display).rstrip()

    def boot(self, seconds=30):
        """Pump until the task list has loaded (Task 1 visible) or timeout."""
        t0 = time.monotonic()
        while time.monotonic() - t0 < seconds:
            self.pump(0.5)
            if "Task 1" in self.visible():
                return True
        return False

    def send_measured(self, seq, marker, quiet=0.4, timeout=8.0):
        """Send seq; return (latency_ms_to_marker_or_None, bytes_emitted, visible_after).

        Measures the time from the write until a chunk carrying `marker`'s bytes arrives, and the
        total bytes emitted until output goes quiet for `quiet` seconds."""
        os.write(self.master, seq)
        t0 = time.monotonic()
        total = 0
        latency = None
        last = time.monotonic()
        mbytes = marker.encode()
        while True:
            now = time.monotonic()
            if now - t0 > timeout:
                break
            if now - last > quiet and total > 0:
                break
            r, _, _ = select.select([self.master], [], [], 0.05)
            if not r:
                continue
            try:
                chunk = os.read(self.master, 65536)
            except OSError:
                break
            if not chunk:
                break
            self.answer(chunk)
            self.stream.feed(chunk)
            total += len(chunk)
            last = time.monotonic()
            if latency is None and mbytes in chunk:
                latency = (last - t0) * 1000.0
        return latency, total, self.visible()

    def alive(self):
        return self.proc.poll() is None

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def open_close_cycle(app, label, cycle):
    """F1 (open) → assert help → Esc (close) → assert list + process alive. Returns
    (open_ms, open_bytes, close_bytes)."""
    open_ms, open_bytes, v = app.send_measured(F1, HELP_MARKER)
    assert HELP_MARKER in v, f"{label} cycle {cycle}: F1 did not open help ({HELP_MARKER!r} missing):\n{v[:2000]}"
    assert HELP_TITLE in v, f"{label} cycle {cycle}: help title {HELP_TITLE!r} missing:\n{v[:2000]}"
    assert open_ms is not None, f"{label} cycle {cycle}: never saw the help paint marker after F1"
    assert open_ms <= LATENCY_CEILING_MS, \
        f"{label} cycle {cycle}: F1 open latency {open_ms:.0f}ms exceeds {LATENCY_CEILING_MS}ms ceiling"

    _, close_bytes, v = app.send_measured(ESC, LIST_MARKER)
    assert app.alive(), f"{label} cycle {cycle}: process died closing the modal (dispose bug #346?):\n{v[:2000]}"
    assert HELP_MARKER not in v, f"{label} cycle {cycle}: Esc did not dismiss the help:\n{v[:2000]}"
    assert LIST_MARKER in v or "Task 1" in v, f"{label} cycle {cycle}: task list not restored after Esc:\n{v[:2000]}"
    return open_ms, open_bytes, close_bytes


def run_leg(label, **env):
    """One host: boot → two F1/Esc modal cycles (repeatability + no cumulative crash) →
    steady-state list-nav latency (median of several Down presses, the #3 invariant).
    Returns a dict of the measured numbers for the A/B grid."""
    app = App(E2E_TASKS="200", E2E_REFRESH="600", **env)
    try:
        assert app.boot(), f"{label}: app did not boot (Task 1 never rendered):\n{app.visible()[:1500]}"

        # 1-2. Two open/close cycles — a native nested run must be re-enterable and must not leave
        #      cumulative cruft that crashes or degrades the app the second time round (#38).
        open_ms, open_bytes, close_bytes = open_close_cycle(app, label, 1)
        print(f"  [{label}] cycle 1 done (open {open_ms:.0f}ms)")
        open_close_cycle(app, label, 2)
        print(f"  [{label}] cycle 2 done")
        assert app.alive(), f"{label}: process died after two modal cycles"

        # 3. Outer responsiveness — after the modal cycles, list nav stays snappy. Median of N Down
        #    presses so the number is steady-state, not the one-off repaint right after the close.
        navs = []
        for _ in range(5):
            nav_ms, _, _ = app.send_measured(DOWN, "Task", timeout=5.0)
            if nav_ms is not None:
                navs.append(nav_ms)
        assert app.alive(), f"{label}: process died navigating after the modal cycles"
        assert navs, f"{label}: no list redraw after Down following the modal cycles"
        nav_median = sorted(navs)[len(navs) // 2]
        assert nav_median <= LATENCY_CEILING_MS, \
            f"{label}: post-modal list-nav median {nav_median:.0f}ms exceeds {LATENCY_CEILING_MS}ms ceiling"

        return {"open_ms": open_ms, "open_bytes": open_bytes,
                "close_bytes": close_bytes, "nav_ms": nav_median}
    finally:
        app.kill()


apps_ok = True
try:
    print("── Leg A: hand-mounted _screens HelpScreen (production default) ──")
    a = run_leg("A/_screens")
    print(f"  open {a['open_ms']:.0f}ms · open {a['open_bytes']}B · close {a['close_bytes']}B · "
          f"post-modal nav {a['nav_ms']:.0f}ms")

    print("── Leg B: native Terminal.Gui Dialog on a nested Application.Run (CLICKUP_TODO_NATIVE_MODAL=1) ──")
    b = run_leg("B/native", CLICKUP_TODO_NATIVE_MODAL="1")
    print(f"  open {b['open_ms']:.0f}ms · open {b['open_bytes']}B · close {b['close_bytes']}B · "
          f"post-modal nav {b['nav_ms']:.0f}ms")

    print("\n===== #404 native-modal spike A/B grid =====")
    print(f"{'metric':<26}{'A _screens':>14}{'B native':>14}")
    print(f"{'F1 open→paint (ms)':<26}{a['open_ms']:>14.0f}{b['open_ms']:>14.0f}")
    print(f"{'F1 open bytes':<26}{a['open_bytes']:>14}{b['open_bytes']:>14}")
    print(f"{'Esc close bytes':<26}{a['close_bytes']:>14}{b['close_bytes']:>14}")
    print(f"{'post-modal nav (ms)':<26}{a['nav_ms']:>14.0f}{b['nav_ms']:>14.0f}")
    print("\nok — both hosts open, render, close cleanly (process alive), and leave the list responsive")
except AssertionError as e:
    apps_ok = False
    print("FAIL:", e)

raise SystemExit(0 if apps_ok else 1)
