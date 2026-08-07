#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the New Task screen (#213 + #215 + #240):
Ctrl+N opens it → the fields render with the current user seeded as a locked ✓
default → the List selector is seeded with the cursor's list as the ✓ (home) primary
(#240) → the locked default assignee refuses removal → the optional Priority selector
(four canonical priorities + "(no priority)") and Due-date field (#215) render and
accept input → typing a name + setting priority/due + Save creates in the primary/home list and
returns to the list. Multi-list create (#241) is now ENABLED (#524): a second list the
user adds is applied on Save — the task is created in its primary/home list and added to
each additional selected list, so a subsequent detail fetch shows a multi-list "Lists:"
membership line including the extra list. Asserts each step on the pyte screen."""
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

    # Changing the single target list must still work: de-select the seeded "Personal Tasks (home)" and
    # pick a DIFFERENT list. Drop into the list (row 0 = the ✓ seeded home) and remove it; its "(home)"
    # marker clears with the seed, and ResolvePrimary then falls through to whatever is selected next.
    send(b"\x1b[B", 0.6)   # Down: search box -> list row 0 (the ✓ seeded home)
    send(b"\r", 1.0)       # Enter: de-select the seeded home
    v = visible()
    # De-selected = the ✓ is gone. (The "(home)" label lingers on the now-unselected candidate — a cosmetic
    # artifact of the shared selector; the create target is whatever is checked ✓, resolved by Primary.)
    assert "✓ Personal Tasks" not in v, f"seeded home wasn't de-selectable (#241 change-list):\n{v}"
    send(b"\x1b[A", 0.6)   # Up: list row 0 -> back to the search box
    send(b"Q3", 1.8)       # debounced substring match on "Q3 Website Refresh"
    send(b"\r", 1.2)       # Enter: add it -> now the sole selection, so the create target
    v = visible()
    assert "✓ Q3 Website Refresh" in v, f"couldn't pick a different list after de-selecting the seed (#241):\n{v}"
    print("CHANGE-LIST ok — de-selected the seeded home and chose a different list as the create target")

    # Add a further list; type-ahead-add "Ministry Ops" so two lists are selected at Save (Q3 is the create
    # target as the first selection). Multi-list create is enabled (#524), so Save applies the extra —
    # proven below by the subsequent detail fetch showing the multi-list membership.
    send(b"Ministry", 1.8)  # search cleared after the previous add, so this starts fresh
    send(b"\r", 1.2)
    v = visible()
    assert "✓ Ministry Ops" in v and "✓ Q3 Website Refresh" in v, \
        f"expected both lists selected before Save (Q3 as the create target):\n{v}"
    print("LIST ok — a second list is added in the UI; it will be applied on Save (#524)")
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

    # #241 enabled (#524): two lists were selected, so Save created the task in its primary list (Q3) and
    # added it to the additional list (Ministry Ops) via the membership write. The fake doesn't persist the
    # created "tnew" into the team feed, so the detail we open below is the *cursor* task, not tnew itself;
    # but the fake's `locations` set (mutated by the membership POST) is process-global, so the app's
    # POST /list/list3/task/tnew is what any later detail GET reflects. Opening a detail and cycling to the
    # Other tab (Ctrl+→, #315) therefore proves an add fired for Ministry Ops — a *multi*-list membership
    # renders as a "Lists:" line (home unioned with `locations`); assert it appears and names the extra list.
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
    assert "Lists:" in other, \
        f"expected a multi-list membership line — multi-list create is enabled (#524):\n{other}"
    assert "Ministry Ops" in other, \
        f"the applied second list did not persist into the created task's membership (#241/#524):\n{other}"
    print("MEMBERSHIP ok — the second list was applied on Save; task filed in both lists (#241/#524 enabled)")
    print("NEW TASK E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
