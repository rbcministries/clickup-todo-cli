#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the Quick Updates *List* pane (#242):
Space opens Quick Updates → Tab x3 to the List pane (Status→Priority→Assignees→Lists) →
its empty-state shows the task's home list marked "✓ Personal Tasks (home)" (removable
primary) topped up with the frequency-pool lists → type "Q3" and, after the ~1s debounce,
the list filters to "Q3 Website Refresh" → Enter adds it (membership write round-trips; it
shows with a leading ✓) → Down-Down+Enter on that ✓ row removes it again → Esc returns.

Validates the surface this issue adds: the List selector renders inside the screen's Lists
frame, focus lands on the search box, type-ahead + debounce filter the candidate lists, and
the immediate-apply add/remove reaches the #237 membership facade and reconciles the pane
from the server-confirmed membership (read back from the detail's `locations`). The fake
backend (Program.cs) mutates an additional-locations set on the membership POST/DELETE so
the round-trip is truthful."""
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

    # Tab three times: Status → Priority → Assignees → Lists. Focus lands in the List search box.
    send(b"\t", 0.8)
    send(b"\t", 0.8)
    send(b"\t", 0.8)

    # The Lists frame renders and the empty state shows the home list as the removable "(home)" primary.
    v = visible()
    assert "Lists" in v, f"Lists pane frame missing:\n{v}"
    assert "Personal Tasks" in v and "(home)" in v, f"home list not marked '(home)':\n{v}"
    print("LIST PANE ok — home list shown as '(home)'")

    # Type-ahead: "Q3" then wait out the ~1s debounce; the candidate list should surface Q3 Website
    # Refresh (a frequency-pool list the task isn't in).
    send(b"Q3", 2.2)
    v = visible()
    assert "Q3 Website Refresh" in v, f"type-ahead did not surface Q3 Website Refresh:\n{v}"
    print("TYPE-AHEAD ok — filtered to Q3 Website Refresh")

    # Enter adds the highlighted result; the box clears and, after the membership write + detail refetch,
    # the list shows selected (leading ✓).
    send(b"\r", 2.5)
    v = visible()
    assert "✓ Q3 Website Refresh" in v, f"add did not mark Q3 Website Refresh selected (✓):\n{v}"
    assert "Quick Updates" in v, f"screen lost after add:\n{v}"
    print("ADD ok — Q3 Website Refresh now ✓ (membership write round-tripped)")

    # Down twice moves focus into the list past the home row onto the ✓ Q3 row; Enter removes it.
    send(b"\x1b[B", 0.8)   # CursorDown → into list, row 0 (home)
    send(b"\x1b[B", 0.8)   # CursorDown → row 1 (✓ Q3 Website Refresh)
    send(b"\r", 2.5)
    v = visible()
    assert "✓ Q3 Website Refresh" not in v, f"remove did not clear Q3 Website Refresh's ✓:\n{v}"
    assert "Personal Tasks" in v and "(home)" in v, f"home list should remain after removing Q3:\n{v}"
    print("REMOVE ok — Q3 Website Refresh ✓ cleared, home list intact")

    # Tab must cycle out of the composite List pane back to Status (wrapping); the screen stays intact.
    send(b"\t", 0.8)
    assert "Quick Updates" in visible(), f"Tab out of the List pane lost the screen:\n{visible()}"
    print("TAB-OUT ok — cycled out of the List composite")

    # Esc returns to the task list.
    send(b"\x1b", 1.5)
    v = visible()
    assert "Quick Updates" not in v, f"Esc did not close Quick Updates:\n{v}"
    assert "Task" in v, f"did not return to the task list after Esc:\n{v}"
    print("ESC ok — returned to the task list")
    print("QU LISTS E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
