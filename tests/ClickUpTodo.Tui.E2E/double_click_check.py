#!/usr/bin/env python3
"""Drives the main-list MOUSE double-click path (#286): double-clicking a task row opens its
Task Detail — the mouse equivalent of Enter. Also asserts the guardrails: a double-click in the
empty space beneath a short list no-ops (resolves to no task), and a single click only selects
(never opens). Mouse is injected as SGR-1006 sequences (ESC[<b;x;yM/m) written to the PTY; the
Terminal.Gui ansi driver enables mouse reporting (?1003h + ?1006h) on boot, so we only emit the
click bytes and TG synthesises the double-click from two clicks within its threshold.

Run under the same in-process fake backend as the other checks (no network)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

def answer(data):
    if b"\x1b[18t" in data:
        os.write(master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
    if b"\x1b[6n" in data:
        os.write(master, b"\x1b[1;1R")

def pump(seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        r, _, _ = select.select([master], [], [], 0.03)
        if r:
            try:
                chunk = os.read(master, 65536)
            except OSError:
                break
            if not chunk:
                break
            answer(chunk)
            stream.feed(chunk)

def visible():
    return "\n".join(screen.display[y].rstrip() for y in range(ROWS))

def _sgr(col0, row0, press):
    # SGR-1006 mouse report, 1-based coords; button 0 = left. Uppercase M = press, lowercase m = release.
    return b"\x1b[<0;%d;%d%s" % (col0 + 1, row0 + 1, b"M" if press else b"m")

def click(col0, row0):
    os.write(master, _sgr(col0, row0, True))
    os.write(master, _sgr(col0, row0, False))

def double_click(col0, row0, gap=0.08):
    click(col0, row0)
    time.sleep(gap)
    click(col0, row0)

# The list's FrameView border sits at screen row 1; its first content row (list viewport row 0) is
# screen row 2. A short list (20 tasks, no grouping/pins) leaves empty space lower down (~row 20).
FIRST_TASK_ROW = 2
EMPTY_ROW = 20
CLICK_COL = 10

try:
    pump(8.0)
    assert "Task 0" in visible(), "list boot failed:\n" + visible()

    # 1) Double-click empty space beneath the list → resolves to no task → no-op (stays on the list).
    double_click(CLICK_COL, EMPTY_ROW)
    pump(2.0)
    v = visible()
    assert "Description" not in v, "double-click on empty space wrongly opened detail:\n" + v
    assert "Task 0" in v, "list disappeared after empty-space double-click:\n" + v

    # 2) Single click on a task row → selects only, never opens detail.
    click(CLICK_COL, FIRST_TASK_ROW)
    pump(2.0)
    v = visible()
    assert "Description" not in v, "single click wrongly opened detail:\n" + v

    # 3) Double-click the first task row → opens its Task Detail (the mouse equivalent of Enter).
    double_click(CLICK_COL, FIRST_TASK_ROW)
    pump(3.0)
    v = visible()
    assert "Description" in v, "double-click did not open Task Detail:\n" + v

    # 4) Esc closes the detail and restores the list beneath.
    os.write(master, b"\x1b")
    pump(2.0)
    v = visible()
    assert "Task 0" in v, "Esc did not restore the list:\n" + v

    print("ok")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
