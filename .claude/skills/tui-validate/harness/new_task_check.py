#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the New Task screen (#213 + #215):
Ctrl+N opens it → the fields render with the current user seeded as a locked ✓
default → the locked default refuses removal → the optional Priority selector (four
canonical priorities + "(no priority)") and Due-date field (#215) render and accept
input → typing a name + setting priority/due + Save creates (round-trips through the
create facade) and returns to the list. Asserts each step on the pyte screen."""
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

    # Ctrl+N opens the New Task screen.
    send(b"\x0e", 2.0)
    v = visible()
    for token in ("New task", "Name", "Description", "Assignees"):
        assert token in v, f"missing {token!r} after Ctrl+N:\n{v}"
    # The current user is seeded as a locked ✓ default assignee.
    assert "Ben Seymour" in v, f"locked self assignee not seeded:\n{v}"
    assert "✓ Ben Seymour" in v, f"self not shown as a selected (✓) assignee:\n{v}"
    print("OPEN ok — New task screen with locked ✓ self assignee")

    # Type a task name into the focused Name field.
    send(b"Buy milk from Ctrl+N", 1.0)
    assert "Buy milk from Ctrl+N" in visible(), f"typed name not shown:\n{visible()}"
    print("NAME ok — name entered")

    # Tab to the Assignees pane, drop into its list, and try to remove the locked self: refused.
    send(b"\t", 0.6)   # Name -> Description
    send(b"\t", 0.6)   # Description -> Assignees (search box)
    send(b"\x1b[B", 0.6)  # Down: into the list (the ✓ self row)
    send(b"\r", 1.0)      # Enter: attempt remove -> locked no-op flash
    v = visible()
    assert "default assignee" in v.lower(), f"locked-remove refusal not flashed:\n{v}"
    assert "✓ Ben Seymour" in v, f"locked self was removed (should be refused):\n{v}"
    print("LOCKED ok — removing the default assignee is refused")

    # The optional Priority selector and Due-date field render (#215).
    v = visible()
    for token in ("Priority", "Urgent", "(no priority)", "Due date"):
        assert token in v, f"missing optional field {token!r} (#215):\n{v}"
    print("OPTIONAL ok — Priority selector + Due-date field render")

    # Back up to the selector's search box (its single tab stop), then Tab through the two optional
    # fields to the Save button. Tab order is Assignees -> Priority -> Due date -> Save (#215).
    send(b"\x1b[A", 0.6)  # Up: list -> search box
    send(b"\t", 0.6)      # search box -> Priority list (Tab bubbles out of the composite)
    send(b"\x1b[A", 0.6)  # Up: move off "(no priority)" onto a real priority (Low)
    send(b"\t", 0.6)      # Priority -> Due date field
    send(b"2026-12-31", 0.8)  # type a valid due date
    assert "2026-12-31" in visible(), f"typed due date not shown:\n{visible()}"
    print("DUE ok — due date entered")
    send(b"\t", 0.6)      # Due date -> Save button
    send(b" ", 3.0)       # Space activates the focused Save button
    v = visible()
    assert "New task" not in v, f"Save did not close the New Task screen:\n{v}"
    assert "Task" in v, f"did not return to the task list after Save:\n{v}"
    print("SAVE ok — task created (round-tripped) and returned to the list")
    print("NEW TASK E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
