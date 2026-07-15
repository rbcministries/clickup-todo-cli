#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the Quick Updates *Assignees* pane (#158):
Space opens Quick Updates → Tab to the Assignees pane → its empty-state list shows the
seeded workspace members (frequency-pool top-up) → type "grac" and, after the ~1s
debounce, the list filters to Grace Hopper → Enter adds her (write round-trips; she shows
with a leading ✓) → Down+Enter on that ✓ row removes her again → Esc returns to the list.

Validates the surface this issue adds: the selector renders inside the screen's Assignees
frame, focus lands on the search box, type-ahead + debounce filter the list, and the
immediate-apply add/remove reaches the facade and reconciles the pane from the server set.
The fake backend (Program.cs) seeds workspace members and mutates a task assignee set on the
PUT so the round-trip is truthful."""
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

    # Space opens Quick Updates.
    send(b" ", 2.0)
    assert "Quick Updates" in visible(), "Quick Updates did not open:\n" + visible()

    # Tab twice: Status → Priority → Assignees. Focus should land in the search box.
    send(b"\t", 0.8)
    send(b"\t", 0.8)

    # Empty-state: the seeded workspace members fill the list from the frequency pool.
    v = visible()
    seeded = [m for m in ("Ada Lovelace", "Grace Hopper", "Alan Turing") if m in v]
    assert len(seeded) >= 2, f"empty-state assignee list missing seeded members (saw {seeded}):\n{v}"
    print("EMPTY-STATE ok — seeded members shown:", seeded)

    # Type-ahead: "grac" then wait out the ~1s debounce; the list should filter to Grace Hopper
    # and drop the non-matches.
    send(b"grac", 2.2)
    v = visible()
    assert "Grace Hopper" in v, f"type-ahead did not surface Grace Hopper:\n{v}"
    assert "Ada Lovelace" not in v, f"type-ahead did not filter out non-matches:\n{v}"
    print("TYPE-AHEAD ok — filtered to Grace Hopper")

    # Enter adds the highlighted result; the box clears, the empty state restores, and after the
    # write round-trip Grace shows selected (leading ✓).
    send(b"\r", 2.5)
    v = visible()
    assert "✓ Grace Hopper" in v, f"add did not mark Grace Hopper selected (✓):\n{v}"
    assert "Quick Updates" in v, f"screen lost after add:\n{v}"
    print("ADD ok — Grace Hopper now ✓ (write round-tripped)")

    # Down moves focus into the list onto the top (✓ Grace) row; Enter removes her. She should lose
    # the ✓ (reappearing as an unselected candidate).
    send(b"\x1b[B", 0.8)   # CursorDown
    send(b"\r", 2.5)
    v = visible()
    assert "✓ Grace Hopper" not in v, f"remove did not clear Grace Hopper's ✓:\n{v}"
    assert "Quick Updates" in v, f"screen lost after remove:\n{v}"
    print("REMOVE ok — Grace Hopper ✓ cleared")

    # Tab must cycle *out* of the composite Assignees pane (relies on its focus-chain HasFocus) back to
    # Status, wrapping — the screen stays intact and focus leaves the search box.
    send(b"\t", 0.8)
    assert "Quick Updates" in visible(), f"Tab out of the Assignees pane lost the screen:\n{visible()}"
    print("TAB-OUT ok — cycled out of the Assignees composite")

    # Esc returns to the task list.
    send(b"\x1b", 1.5)
    v = visible()
    assert "Quick Updates" not in v, f"Esc did not close Quick Updates:\n{v}"
    assert "Task" in v, f"did not return to the task list after Esc:\n{v}"
    print("ESC ok — returned to the task list")
    print("QU ASSIGNEES E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
