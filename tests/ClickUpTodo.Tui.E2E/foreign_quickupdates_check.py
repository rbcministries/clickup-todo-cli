#!/usr/bin/env python3
"""#232 end-to-end: Quick Updates edits a task that isn't the user's own work.

Runs the real app under a PTY against the opt-in E2E_FOREIGN=1 fake backend, which seeds:
  • a foreign subtask (fs1, "not assigned to you", #70) under an assigned parent, and
  • a context parent (cp1, "parent — not assigned to you", #46) pulled in for an in-view child,
plus a modelled PUT /task/{id} so a committed Status round-trips.

Asserts, on the pyte-rendered screen:
  1. both not-mine row markers render;
  2. Space OPENS Quick Updates on the foreign subtask (the #160 write-block is gone — the pre-#160
     build flashed "not assigned to you — unchanged" and never opened);
  3. committing a changed, active status settles the Status pane's ✓ on the new value — which only
     holds if the modelled PUT echoed the committed status (ApplyStatus reconciles ✓ to the
     server-confirmed value), i.e. the edit stuck;
  4. Esc returns to the list with the foreign row still present (it "stays in place", not dropped).

Exits nonzero with the offending screen on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

FS1 = "Foreign teammate subtask ZZ"     # the foreign-subtask row / its Quick Updates title
FOREIGN_MARKER = "· (not assigned to you)"
CONTEXT_MARKER = "· (parent — not assigned to you)"

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
            answer(chunk)
            stream.feed(chunk)


def visible():
    return "\n".join(line.rstrip() for line in screen.display)


def lines():
    return [line.rstrip() for line in screen.display]


def require(cond, msg):
    if not cond:
        print("FAIL:", msg)
        print("----- screen -----")
        print(visible())
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except Exception:
            pass
        sys.exit(1)


def send(seq, wait):
    os.write(master, seq)
    pump(wait)


def qu_open():
    v = visible()
    return "Quick Updates" in v and "Priority" in v and "Assignees" in v


SPACE = b" "
ESC = b"\x1b"
DOWN = b"\x1b[B"
ENTER = b"\r"
CTRL_RIGHT = b"\x1b[1;5C"    # expand-all (the bulk counterpart to the per-parent → fold)

try:
    pump(8.0)
    require("Personal Tasks" in visible() or "Assigned parent" in visible(),
            "app never rendered the seeded list")

    # 1. Both not-mine row kinds render. A normal parent (pt1) folds its subtasks collapsed by default,
    #    so expand-all (Ctrl+→) first to reveal the foreign subtask; a context parent (cp1) always shows
    #    its child, so its marker is present without expanding.
    require(CONTEXT_MARKER in visible(), f"context-parent marker {CONTEXT_MARKER!r} not rendered")
    send(CTRL_RIGHT, 1.5)
    require(FOREIGN_MARKER in visible(), f"foreign-subtask marker {FOREIGN_MARKER!r} not rendered after expand-all")
    print("MARKERS ok — foreign subtask + context parent both rendered")

    # 2. Find and open Quick Updates on the foreign subtask. Space opens QU on the selected row; if the
    #    open screen's title isn't fs1, Esc + Down and retry (robust to row ordering). QU is a full-window
    #    screen, so while it's open the fs1 name can only be the title — a reliable oracle for "on fs1".
    opened = False
    for _ in range(10):
        send(SPACE, 2.5)
        if qu_open() and FS1 in visible():
            opened = True
            break
        if qu_open():
            send(ESC, 1.5)
        send(DOWN, 0.8)
    require(opened, "Space never opened Quick Updates on the foreign subtask "
                    "(a not-mine row should be editable since #160)")
    print("OPEN ok — Quick Updates opened on the foreign subtask (not blocked)")

    # 3. Commit a changed, active status. The Status pane preselects the current value ("to do", row 0);
    #    Down → "in progress" (row 1); Enter commits and (#207) keeps the screen open. After the async
    #    write settles, the ✓ must sit on "in progress" — proving the modelled PUT echoed it (else the
    #    host would reconcile ✓ to the server value and it would move).
    send(DOWN, 0.8)
    send(ENTER, 3.0)
    pump(1.5)
    require(qu_open(), "Quick Updates should stay open after apply-on-Enter (#207)")
    require(any("in progress" in ln and "✓" in ln for ln in lines()),
            "the committed status 'in progress' is not marked current (✓) — the write did not round-trip")
    print("COMMIT ok — 'in progress' committed and confirmed (✓) via the modelled PUT")

    # 4. Esc back to the list; the foreign row is still there (stays in place, not dropped) and now
    #    reflects the committed status in place — "in progress" renders as the (IP) abbreviation chip,
    #    unique to fs1 (pt1/ct1 are (TD), cp1 is (IR)).
    #    (Note: the in-place row update — UpdateTaskRow → BuildRow — re-formats without the
    #    isForeignSubtask flag, so the "(not assigned to you)" marker transiently drops until the next
    #    full re-render. That's pre-existing #160/#179 render behaviour, not gated by this scenario and
    #    outside #160's acceptance ("shows the confirmed status in place and isn't dropped"), so this
    #    check deliberately does NOT assert the marker persists post-edit — see the PR / issue link.)
    send(ESC, 2.0)
    require(not qu_open(), "Esc did not close Quick Updates")
    fs1_row = next((ln for ln in lines() if FS1 in ln), None)
    require(fs1_row is not None, "the foreign-subtask row was dropped from the list after the edit")
    require("(IP)" in fs1_row,
            f"the committed 'in progress' status is not reflected on the foreign row in place: {fs1_row!r}")
    print("STAYS ok — foreign row still present and shows the committed status in place")

    print("FOREIGN QUICK UPDATES E2E: PASS")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
