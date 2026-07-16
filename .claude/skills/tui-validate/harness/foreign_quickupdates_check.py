#!/usr/bin/env python3
"""Boots the TUI under a PTY with the E2E_FOREIGN=1 scenario and exercises Quick Updates on the
two kinds of *not-mine* row that #160 (PR #233) unblocked but couldn't assert automatically (#232):

  - a **foreign subtask** (a teammate-owned subtask pulled in under my parent, #70/#179), and
  - a **context parent** (a parent absent from my snapshot, pulled in as a header for its subtask, #46).

For each, it navigates to the row, opens Quick Updates with Space (asserting it is NOT blocked — the
pre-#160 guards flashed "not assigned to you — unchanged" and refused to open), commits a Status, and
asserts the row shows the server-confirmed status **in place** and is **not dropped** from the list.

The fake backend (Program.cs, behind E2E_FOREIGN) seeds the rows and echoes the requested status on
the PUT so the write round-trips; text badges (config in Program.cs) render the status as a readable
word so it can be asserted on the row. The scenario is opt-in, so the default A/B renders are undisturbed.

Deterministic row order (default sort = due, then name; nesting via SubtaskArranger):
    row0  Aardvark parent task              (t1, mine)
    row1    Delta foreign subtask …         (fsub)  · (not assigned to you)
    row2  Gamma context parent …            (cpar)  · (parent — not assigned to you)
    row3    Beta task under context parent  (t2)
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", E2E_FOREIGN="1")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

def answer(data):
    if b"\x1b[18t" in data:
        os.write(master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
    if b"\x1b[6n" in data:
        os.write(master, b"\x1b[1;1R")

def pump(seconds):
    out = b""
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        r, _, _ = select.select([master], [], [], 0.05)
        if r:
            try:
                chunk = os.read(master, 65536)
            except OSError:
                break
            if not chunk:
                break
            out += chunk
            answer(chunk)
            stream.feed(chunk)
    return out

def visible():
    return "\n".join(line.rstrip() for line in screen.display).rstrip()

def line_with(substr):
    """The first rendered screen line containing substr, or None."""
    for line in screen.display:
        if substr in line:
            return line.rstrip()
    return None

def send(seq, wait=1.2):
    os.write(master, seq)
    return pump(wait)

FOREIGN_MARK = "(not assigned to you)"
CONTEXT_MARK = "(parent — not assigned to you)"

def edit_not_mine_row(name, marker, label):
    """From the row above it, Down onto the target row, open Quick Updates (assert not blocked and
    that it opened on the right task), commit 'in progress', exit, and assert the row shows the
    confirmed status in place and is not dropped."""
    send(b"\x1b[B", 0.8)  # CursorDown onto the target row

    # Space opens Quick Updates. If the row were still write-blocked (pre-#160) it would flash
    # "not assigned to you — unchanged" and no screen would open.
    send(b" ", 2.0)
    v = visible()
    assert "Quick Updates" in v, f"[{label}] Quick Updates did not open (blocked?):\n{v}"
    assert name in v, f"[{label}] Quick Updates opened on the wrong task (title lacks {name!r}):\n{v}"
    assert "unchanged" not in v, f"[{label}] a not-assigned block flash is showing:\n{v}"
    print(f"OPEN ok [{label}] — Quick Updates opened on {name!r}, not blocked")

    # Down to 'in progress' (row 1 of the plist statuses; current is 'to do'), Enter commits it. The
    # fake echoes the requested status, so the write confirms 'in progress'.
    send(b"\x1b[B", 0.8)
    send(b"\r", 2.5)
    v = visible()
    assert "in progress" in v, f"[{label}] committed status not reflected on the Quick Updates pane:\n{v}"
    print(f"COMMIT ok [{label}] — 'in progress' committed and confirmed")

    # Esc back to the list; the edited not-mine row must show the confirmed status in place and still
    # be present (not dropped from the list).
    send(b"\x1b", 1.5)
    v = visible()
    assert "Quick Updates" not in v, f"[{label}] Esc did not close Quick Updates:\n{v}"
    row = line_with(name)
    assert row is not None, f"[{label}] the edited row {name!r} was dropped from the list:\n{v}"
    assert "in progress" in row, f"[{label}] the row does not show the confirmed status in place:\n{row!r}\n\n{v}"
    assert "(sending" not in row, f"[{label}] the write did not settle (row still 'sending…'):\n{row!r}"
    # #160 keeps the informational context marker after an edit (only the write restriction is lifted);
    # without the UpdateTaskRow fix the optimistic in-place update dropped it until the next full render.
    assert marker in row, f"[{label}] the '{marker}' marker was dropped from the row after the edit (#160/#232):\n{row!r}"
    print(f"IN-PLACE ok [{label}] — row shows 'in progress' in place, keeps its marker, and was not dropped:\n    {row!r}")

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(1.0)
    v = visible()

    # The context parent is always expanded (it exists only to show its child), so its row + marker
    # are visible immediately. The mine parent t1 starts collapsed (▶), so its foreign subtask is
    # hidden until expanded.
    assert CONTEXT_MARK in v, f"context-parent row missing its '(parent — …)' marker:\n{v}"
    assert "Gamma context parent" in v, f"context parent row not rendered:\n{v}"
    assert "Aardvark parent task" in v, f"mine parent row (t1) not rendered:\n{v}"
    print("SEED ok — context-parent row rendered with its marker; mine parent present")

    # Cursor starts on row0 (t1); → expands it (#76 per-parent fold), revealing its foreign subtask.
    send(b"\x1b[C", 1.0)
    v = visible()
    assert FOREIGN_MARK in v, f"expanding t1 did not reveal the foreign subtask's marker:\n{v}"
    assert "Delta foreign subtask" in v, f"foreign subtask row not revealed after expand:\n{v}"
    print("EXPAND ok — foreign subtask revealed under its parent with its '(not assigned to you)' marker")

    # Cursor is on row0 (t1); one Down lands on the foreign subtask (row1). Edit it.
    edit_not_mine_row("Delta foreign subtask", FOREIGN_MARK, "foreign-subtask")
    # Cursor is now on row1; one Down lands on the context parent (row2). Edit it.
    edit_not_mine_row("Gamma context parent", CONTEXT_MARK, "context-parent")

    print("FOREIGN QUICK UPDATES E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
