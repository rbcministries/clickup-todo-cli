#!/usr/bin/env python3
"""Guard for the Quick Updates *List* pane (#242), which is IMPLEMENTED BUT TEMPORARILY
DISABLED. Changing a task's list can strand fields/statuses that don't exist on the target
list; ClickUp's PWA offers a guided migration for those cases and we don't yet, so the pane
is commented out of the composition (see QuickUpdatesScreen's summary).

This check asserts the disabled state: Ctrl+U opens Quick Updates with exactly Status /
Priority / Assignees and *no* "Lists" pane, and Tab cycles among only those three (never
surfacing a "(home)" list marker). When the pane is re-enabled, replace this with the
add/remove round-trip check (see git history for the original) — the fake backend already
models the membership POST/DELETE + `locations` for it."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
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

def send(seq, wait=1.2):
    os.write(master, seq)
    return pump(wait)

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(1.0)

    # Ctrl+U opens Quick Updates (#290).
    send(b"\x15", 2.0)
    v = visible()
    assert "Quick Updates" in v, "Quick Updates did not open:\n" + v
    for token in ("Status", "Priority", "Assignees"):
        assert token in v, f"missing {token!r} pane after opening Quick Updates:\n{v}"
    # The List pane is disabled — its frame title and home marker must not appear.
    assert "Lists" not in v, f"'Lists' pane is present — the #242 List pane should be disabled:\n{v}"
    assert "(home)" not in v, f"a '(home)' list marker rendered — the #242 List pane should be disabled:\n{v}"
    print("DISABLED-STATE ok — Status/Priority/Assignees present, no Lists pane")

    # Tab four times must never surface a Lists pane / home marker (only three panes cycle).
    for i in range(4):
        send(b"\t", 0.7)
        v = visible()
        assert "Quick Updates" in v, f"screen lost after Tab #{i+1}:\n{v}"
        assert "Lists" not in v and "(home)" not in v, f"List pane surfaced on Tab #{i+1}:\n{v}"
    print("TAB-CYCLE ok — three panes only, no Lists pane surfaced")

    # Esc returns to the task list.
    send(b"\x1b", 1.5)
    v = visible()
    assert "Quick Updates" not in v, f"Esc did not close Quick Updates:\n{v}"
    assert "Task" in v, f"did not return to the task list after Esc:\n{v}"
    print("ESC ok — returned to the task list")
    print("QU LISTS (DISABLED) E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
