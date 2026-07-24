#!/usr/bin/env python3
"""Drives the main-list MOUSE fold-arrow path (#287): a single left-click on a parent task's ▶/▼
arrow toggles its subtasks — the mouse equivalent of →/←. Also asserts the guardrail that a click on
the row *body* (the title, not the arrow column) does NOT toggle, so it coexists with A's
double-click-to-open (#286). Mouse is injected as SGR-1006 sequences (ESC[<b;x;yM/m) written to the
PTY; the Terminal.Gui ansi driver enables mouse reporting on boot.

The fold arrow's exact column depends on the badges/indent ahead of it, so rather than compute it we
locate the ▶/▼ glyph on the emulated screen and click that column — the same column the user sees.

Run under the in-process fake backend (no network). A small task set (E2E_TASKS=8) keeps the layout
deterministic: the fixture makes every 4th task a subtask of the task 3 before it, so t0 is a parent
of t3 (Task 3 nests under Task 0)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", E2E_TASKS="8")
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

def find_arrow_row(glyph):
    """The (row, column) of the first on-screen fold arrow, or (None, None)."""
    for y in range(ROWS):
        line = screen.display[y]
        col = line.find(glyph)
        if col >= 0 and "Task" in line:
            return y, col
    return None, None

try:
    pump(8.0)
    assert "Task 0" in visible(), "list boot failed:\n" + visible()

    # Enable the nested subtasks view (F4: Hidden → mine+unassigned → all → …). Press until a collapsed
    # parent's ▶ appears — one press suffices for the default, but loop so we're robust to the default.
    for _ in range(3):
        if "▶" in visible():
            break
        os.write(master, b"\x1bOS")  # F4
        pump(3.0)
    v = visible()
    assert "▶" in v, "subtasks view did not show a collapsed parent arrow:\n" + v
    # Collapsed by default → the child (Task 3, which nests under Task 0) is hidden.
    assert "Task 3" not in v, "child row unexpectedly visible before expanding:\n" + v

    # 1) Click the parent's ▶ arrow → expands → the child row (Task 3) appears.
    row, col = find_arrow_row("▶")
    assert row is not None, "no ▶ arrow found:\n" + v
    assert "Task 0" in screen.display[row], "first arrow row is not the Task 0 parent:\n" + v
    click(col, row)
    pump(2.5)
    v = visible()
    assert "Task 3" in v, "clicking the ▶ arrow did not expand the parent:\n" + v
    # The marker flipped to ▼ on the (still first) parent row.
    drow, dcol = find_arrow_row("▼")
    assert drow is not None and "Task 0" in screen.display[drow], "arrow did not flip to ▼:\n" + v

    # 2) Click the ▼ arrow again → collapses → the child row disappears.
    click(dcol, drow)
    pump(2.5)
    v = visible()
    assert "Task 3" not in v, "clicking the ▼ arrow did not collapse the parent:\n" + v

    # 3) Guardrail: a single click on the parent's *title* (well right of the arrow) must NOT toggle —
    # it only selects, leaving the row body free for A's double-click-to-open.
    row, col = find_arrow_row("▶")
    title_col = screen.display[row].find("Task 0")
    assert title_col > col, "title column not right of the arrow:\n" + v
    click(title_col + 2, row)
    pump(2.0)
    v = visible()
    assert "Task 3" not in v, "clicking the title wrongly toggled the fold:\n" + v

    print("ok")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
