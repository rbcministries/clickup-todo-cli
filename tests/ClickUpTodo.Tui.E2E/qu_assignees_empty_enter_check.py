#!/usr/bin/env python3
"""Regression check for #234: in the Quick Updates *Assignees* pane, pressing Enter while
focused in the **empty** search box of a task that already has assignees must NOT remove the
first current assignee.

Runs with E2E_QU_SEED_ASSIGNEE=1 so the fake backend seeds every task with a current
assignee (Ada Lovelace). Flow: Space opens Quick Updates → Tab to the Assignees pane → its
empty-state list shows the current assignee as a leading ✓ row (row 0, the default
selection) → Enter in the *empty* search box → she must still be ✓ (the pre-fix behaviour
picked row 0, toggled Removed, and wrote an immediate server remove). It also covers the
debounce window: typing a char then pressing Enter *before* the ~1s type-ahead debounce
renders results (the rows are still the ✓ rows, so a query-text-only gate would still remove
her). Then, to prove the fix only disables the search-box shortcut in those states (not
removal itself), Down onto the ✓ row + Enter still removes her via the list."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_QU_SEED_ASSIGNEE="1")
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

    # Space opens Quick Updates on the cursor's task (seeded with a current assignee).
    send(b" ", 2.0)
    assert "Quick Updates" in visible(), "Quick Updates did not open:\n" + visible()

    # Tab twice: Status → Priority → Assignees. Focus lands in the (empty) search box.
    send(b"\t", 0.8)
    send(b"\t", 0.8)

    # Empty-state: the current assignee is shown as a leading ✓ row (row 0 = default selection).
    v = visible()
    assert "✓ Ada Lovelace" in v, f"seeded current assignee not shown as ✓ in the empty state:\n{v}"
    print("EMPTY-STATE ok — current assignee shown as ✓ (row 0)")

    # #234: Enter in the *empty* search box must be a no-op — Ada must still be ✓ afterwards (the
    # pre-fix code would have picked row 0 → removed her, dropping the ✓). Wait long enough that a
    # (wrong) immediate-apply write would have round-tripped.
    send(b"\r", 2.5)
    v = visible()
    assert "✓ Ada Lovelace" in v, f"Enter in the empty search box removed the current assignee (#234):\n{v}"
    assert "Quick Updates" in v, f"Enter in the empty search box navigated away from the screen:\n{v}"
    print("EMPTY-BOX ENTER ok — no-op, current assignee retained (#234 fixed)")

    # Debounce window: type a char and press Enter *before* the ~1s type-ahead debounce renders results.
    # The rows are still the empty-state ✓ rows, so a query-text-only gate would remove the assignee here;
    # gating on the render state must keep it a no-op. Send "g" and "\r" together so Enter lands well
    # inside the debounce, pump briefly (< debounce), then clear the box and let the empty state settle
    # before asserting she survived.
    send(b"g\r", 0.5)
    send(b"\x7f", 1.8)   # Backspace clears the box → empty state re-renders
    v = visible()
    assert "✓ Ada Lovelace" in v, f"Enter during the debounce window removed the current assignee (#234):\n{v}"
    assert "Quick Updates" in v, f"screen lost after the debounce-window Enter:\n{v}"
    print("DEBOUNCE-WINDOW ENTER ok — no-op, current assignee retained (#234 fixed)")

    # Removal is still reachable the explicit way: Down onto the ✓ row, then Enter removes her.
    send(b"\x1b[B", 0.8)   # CursorDown into the list, onto the ✓ Ada row
    send(b"\r", 2.5)
    v = visible()
    assert "✓ Ada Lovelace" not in v, f"explicit ✓-row removal (Down+Enter) no longer works:\n{v}"
    assert "Quick Updates" in v, f"screen lost after explicit removal:\n{v}"
    print("EXPLICIT REMOVE ok — Down+Enter on the ✓ row still removes")

    # Esc returns to the task list.
    send(b"\x1b", 1.5)
    v = visible()
    assert "Quick Updates" not in v, f"Esc did not close Quick Updates:\n{v}"
    assert "Task" in v, f"did not return to the task list after Esc:\n{v}"
    print("ESC ok — returned to the task list")
    print("QU ASSIGNEES EMPTY-ENTER E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
