#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the New Task screen (#213 + #215 + #240):
Ctrl+N opens it → the fields render with the current user seeded as a locked ✓
default → the List selector is seeded with the cursor's list as the ✓ (home) primary
(#240) → the locked default assignee refuses removal → the optional Priority selector
(four canonical priorities + "(no priority)") and Due-date field (#215) render and
accept input → typing a name + setting priority/due + Save creates in the primary/home list and
returns to the list. Multi-list create (#241) is shipped DISABLED pending the
list-change migration (#365): focusing the List selector flashes the disabled note,
and a second list the user adds is ignored on Save — a subsequent detail fetch shows
the home list only. Asserts each step on the pyte screen."""
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
    for token in ("New task", "Name", "Description", "Assignees", "List"):
        assert token in v, f"missing {token!r} after Ctrl+N:\n{v}"
    # The current user is seeded as a locked ✓ default assignee.
    assert "Ben Seymour" in v, f"locked self assignee not seeded:\n{v}"
    assert "✓ Ben Seymour" in v, f"self not shown as a selected (✓) assignee:\n{v}"
    # The List selector is seeded with the cursor's list as the ✓ (home) primary (#240). The default
    # snapshot's tasks live in "Personal Tasks" (and the personal-list fallback is "Personal Tasks" too),
    # so either way the home create target renders as the selected primary.
    assert "Personal Tasks (home)" in v, f"seeded primary/home list not shown (#240):\n{v}"
    assert "✓ Personal Tasks (home)" in v, f"home list not shown as a selected (✓) list (#240):\n{v}"
    print("OPEN ok — New task screen with locked ✓ self assignee + ✓ (home) list seed")

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

    # Back up to the Assignees selector's search box (its single tab stop), then Tab through the List
    # selector and the two optional fields to the Save button. Tab order is Assignees -> List -> Priority
    # -> Due date -> Save (#215/#240).
    send(b"\x1b[A", 0.6)  # Up: Assignees list -> its search box
    send(b"\t", 0.6)      # Assignees search box -> List selector search box (Tab bubbles out of composite)
    # #241 is shipped disabled pending #365: the moment the List selector takes focus, the disabled-state
    # note flashes so a user who tries to add a second list knows it won't be applied.
    assert "multiple lists" in visible().lower(), \
        f"disabled-multi-list note not flashed when the List selector took focus (#241/#365):\n{visible()}"
    print("LIST-NOTE ok — focusing the List selector flashes the disabled-multi-list note (#241/#365)")
    # The selector still lets the user pick a second list (the UI is unchanged); type-ahead-add "Ministry
    # Ops" to prove it's accepted into the selection here — but Save will ignore it while disabled.
    send(b"Ministry", 1.8)  # debounced (~1s) substring match on "Ministry Ops"
    send(b"\r", 1.2)        # Enter in the search box adds the highlighted match
    v = visible()
    assert "✓ Ministry Ops" in v, f"type-ahead add of a second list didn't take in the selector UI:\n{v}"
    assert "✓ Personal Tasks (home)" in v, f"primary/home list changed after adding another list (#240):\n{v}"
    print("LIST ok — a second list can be picked in the selector; home stays the create target")
    send(b"\t", 0.6)      # List selector search box -> Priority list
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

    # #241 disabled (#365): Save filed the task into its home list ONLY — the second list the user picked
    # was ignored, so no membership write fired. Open the cursor task's detail and cycle to the Other tab
    # (Ctrl+→, #315), where a *multi*-list membership would render as a "Lists:" line — assert it does not,
    # and that the ignored second list is absent.
    send(b"\r", 3.0)         # Enter → open detail (async fetch + screen swap)
    assert "Description" in visible(), f"detail screen did not open:\n{visible()}"
    # The tab the detail opens on comes from persisted view settings (Stream/Description/Comments/Other),
    # so step through until the Other tab's header attributes render — its "Priority:" / "Status:" labels
    # are unique to that tab's body.
    other = ""
    for _ in range(5):
        v = visible()
        if "Priority:" in v and "Status:" in v:
            other = v
            break
        send(b"\x1b[1;5C", 1.2)  # Ctrl+→ → next tab
    assert "Priority:" in other and "Status:" in other, \
        f"could not reach the Other tab after cycling:\n{visible()}"
    assert "Lists:" not in other, \
        f"a multi-list membership line rendered — multi-list should be disabled (#241/#365):\n{other}"
    assert "Ministry Ops" not in other, \
        f"the ignored second list leaked into the created task's membership (#241/#365):\n{other}"
    print("MEMBERSHIP ok — the ignored second list did not persist; task filed in its home list only (#241 disabled)")
    print("NEW TASK E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
