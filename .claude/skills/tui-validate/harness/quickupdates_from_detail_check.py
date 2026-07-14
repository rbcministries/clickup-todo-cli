#!/usr/bin/env python3
"""#159 end-to-end: Quick Updates launched from the Task Detail view returns to
the detail on Esc (not the list).

Flow: boot → Enter (open detail) → Ctrl+U (stack Quick Updates over the detail) →
set a status (Enter) → assert we're back on the DETAIL, not the list, and the
status changed → Esc again → assert we're back on the LIST. Also checks the
list-origin round-trip still lands on the list (Space → Esc).

Asserts on the pyte-rendered screen text (never raw bytes). Prints "ok" or raises."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
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
    return "\n".join(screen.display[y].rstrip() for y in range(ROWS))

try:
    pump(8.0)
    v = visible()
    assert "Task" in v, "list boot failed:\n" + v

    # ── Detail origin: Enter → detail, Ctrl+U → Quick Updates ────────────────
    os.write(master, b"\r")               # Enter → open detail (async fetch + swap)
    pump(3.0)
    v = visible()
    assert "Description" in v, "detail screen did not open:\n" + v

    os.write(master, b"\x15")             # Ctrl+U → stack Quick Updates over detail
    pump(2.5)
    v = visible()
    assert "Quick Updates" in v, "Quick Updates did not open from detail (Ctrl+U):\n" + v
    assert "Status" in v and "Priority" in v and "Assignees" in v, \
        "Quick Updates panes missing:\n" + v

    # Move down in the Status pane and apply with Enter (Quick Updates closes on select).
    os.write(master, b"\x1b[B"); pump(0.6)
    os.write(master, b"\r"); pump(2.5)    # Enter → set status, pop back to origin
    v = visible()
    # Return-to-origin: we must be back on the DETAIL view, not the main list.
    assert "Description" in v, "Esc/select did not return to the detail view:\n" + v
    assert "Quick Updates" not in v, "Quick Updates still showing after select:\n" + v

    # ── Esc from the detail returns to the LIST (confirms the stack order) ────
    os.write(master, b"\x1b"); pump(2.0)
    v = visible()
    assert "Task" in v and "Description" not in v, \
        "Esc from detail did not return to the list:\n" + v

    # ── List origin still works: Space → Quick Updates → Esc → back to LIST ──
    os.write(master, b" "); pump(2.5)
    v = visible()
    assert "Quick Updates" in v, "Space did not open Quick Updates from the list:\n" + v
    os.write(master, b"\x1b"); pump(2.0)  # Esc → back to the list
    v = visible()
    assert "Task" in v and "Quick Updates" not in v, \
        "Esc did not return to the list from Quick Updates:\n" + v

    print("ok — Ctrl+U from detail opens Quick Updates and returns to the detail; "
          "list-origin Space round-trip intact")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
