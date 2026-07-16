#!/usr/bin/env python3
"""Boots the TUI under a PTY in the #232 foreign scenario (E2E_FOREIGN=1) and drives Quick
Updates on tasks that are NOT the user's own work — a pulled-in teammate-owned subtask
(_foreignSubtasks) and a context parent (_contextParents) — proving the #160 ownership guard
is gone and that a committed edit round-trips and sticks in place without dropping the row or
its context marker.

The fake backend (Program.cs, foreign branch) seeds:
  * fp   — an assigned parent whose include_subtasks fetch surfaces a teammate-owned child
  * fsub — that child (assignee Grace Hopper, id 102 != me), marked "(not assigned to you)"
  * mine — an assigned task whose parent (cpar) is absent, pulling in the context parent
  * cpar — the context parent header, marked "(parent — not assigned to you)"
and echoes a committed status/priority PUT straight back so SetTaskStatusAsync's read-back is
truthful (the ✓ and the row settle on the server-confirmed value, not just the optimistic move).

Run:  E2E_FOREIGN=1 timeout 90 python3 -u foreign_quickupdates_check.py $DLL
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
    return "\n".join(line.rstrip() for line in screen.display).rstrip()

def row_with(substr):
    """The single rendered screen line containing substr (or '' if none) — used to assert
    several things about one task's row together (status + marker on the same line)."""
    for line in screen.display:
        if substr in line:
            return line.rstrip()
    return ""

def send(seq, wait=1.2):
    os.write(master, seq)
    pump(wait)

FOREIGN = "· (not assigned to you)"
CONTEXT = "· (parent — not assigned to you)"

try:
    pump(9.0)
    v = visible()
    assert "Assigned parent" in v, "app never rendered the foreign scenario list:\n" + v[-1500:]
    # The context parent renders from the first paint (it isn't foldable away).
    assert CONTEXT in v, f"context-parent marker missing on boot:\n{v}"
    # The foreign subtask starts collapsed under its parent (fp shows a ▶ fold marker).
    assert "Teammate-owned subtask" not in v, f"foreign subtask should start collapsed:\n{v}"
    print("BOOT ok — context parent shown; foreign subtask collapsed under fp")

    # Ctrl+→ expands all folds, revealing the teammate-owned subtask nested under fp.
    send(b"\x1b[1;5C", 1.5)
    v = visible()
    assert "Teammate-owned subtask" in v, f"expand-all did not reveal the foreign subtask:\n{v}"
    fsub = row_with("Teammate-owned subtask")
    assert FOREIGN in fsub, f"foreign subtask missing its (not assigned to you) marker:\n{fsub}"
    assert "Grace Hopper" in fsub, f"foreign subtask missing its teammate assignee:\n{fsub}"
    print("EXPAND ok — foreign subtask visible with (not assigned to you) + Grace Hopper")

    # Down selects the foreign subtask; Space opens Quick Updates on it. The #160 guard is gone,
    # so it must OPEN (pre-#160 it flashed "This subtask isn't assigned to you — status unchanged").
    send(b"\x1b[B", 1.0)
    send(b" ", 2.0)
    v = visible()
    assert "Quick Updates — Teammate-owned subtask" in v, \
        f"Quick Updates did not open on the foreign subtask (blocked?):\n{v}"
    for token in ("to do", "in progress", "blocked", "in review"):
        assert token in v, f"status list missing {token!r}:\n{v}"
    assert "✓ to do" in v, f"foreign subtask's current status ✓ not seeded on 'to do':\n{v}"
    print("OPEN ok — Quick Updates opened on the not-mine subtask; statuses loaded (✓ to do)")

    # Down×2 → 'blocked', Enter commits. The ✓ moves to 'blocked' only after the host confirms
    # with the server-returned status (the fake echoes the PUT), so ✓ blocked proves a real
    # round-trip, not just the optimistic move.
    send(b"\x1b[B", 0.6)
    send(b"\x1b[B", 0.6)
    send(b"\r", 2.5)
    v = visible()
    assert "✓ blocked" in v, f"commit did not move the ✓ to 'blocked' (round-trip failed):\n{v}"
    assert "Set status to 'blocked'." in v, f"missing server-confirmed status-line message:\n{v}"
    print("COMMIT ok — ✓ blocked (server-confirmed round-trip)")

    # Esc back to the list: the foreign subtask row must still be present, show the round-tripped
    # 'blocked' status IN PLACE, and keep its (not assigned to you) marker — i.e. the in-place
    # reconcile (UpdateTaskRow) neither drops the not-mine row nor its context marker (#232).
    send(b"\x1b", 2.0)
    v = visible()
    assert "Quick Updates" not in v, f"Esc did not close Quick Updates:\n{v}"
    fsub = row_with("Teammate-owned subtask")
    assert fsub, f"foreign subtask row was dropped from the view after the edit:\n{v}"
    assert "blocked" in fsub, f"committed 'blocked' status did not stick on the row:\n{fsub}"
    assert FOREIGN in fsub, f"in-place update dropped the (not assigned to you) marker:\n{fsub}"
    # The rest of the view is intact — nothing else got dropped.
    assert "Assigned parent" in v and CONTEXT in v and "My subtask" in v, \
        f"other rows were disturbed by the edit:\n{v}"
    print("STICK ok — 'blocked' round-tripped in place; marker kept; no rows dropped")

    # The other not-mine row — the context parent — is editable too (cursor is back on fsub; Down
    # lands on cpar). Just prove Quick Updates opens on it (the #160 guard is gone here as well).
    send(b"\x1b[B", 1.0)
    send(b" ", 2.0)
    v = visible()
    assert "Quick Updates — Context parent" in v, \
        f"Quick Updates did not open on the context parent (blocked?):\n{v}"
    print("CONTEXT-PARENT ok — Quick Updates opened on the context parent too")

    send(b"\x1b", 1.5)
    assert "Quick Updates" not in visible(), "Esc did not close the context-parent Quick Updates"
    print("FOREIGN QUICK UPDATES E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
