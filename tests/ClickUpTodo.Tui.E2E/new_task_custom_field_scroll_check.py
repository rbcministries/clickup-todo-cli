#!/usr/bin/env python3
"""Boots the TUI under a SHORT PTY and exercises the New Task Custom fields page's scroll (#446): with a
tall fillable set (E2E_CUSTOM_FIELDS_MANY — nine text fields + a required "Last Field") the widget stack
is taller than the emulated screen, so "Last Field" starts BELOW the fold. Asserts:

  * on opening the Custom fields page, an early field ("Alpha") is visible but "Last Field" is NOT
    (it's below the fold — proving the stack overflows the viewport);
  * Tab-ing down to "Last Field" scrolls it into view (scroll-on-focus — the reachability guarantee);
  * Save with the below-the-fold required field empty is BLOCKED, flashing its name;
  * filling it (Shift+Tab back to it, which also keeps it scrolled in) and Save creates the task, and
    the entered value reaches the create POST.

A short ROWS is the whole point — a tall terminal (as new_task_custom_fields_check.py uses) never
overflows. Requires E2E_CUSTOM_FIELDS_MANY=1."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

# Short enough that a 32-row field stack overflows, but tall enough that the base page still lays out
# and Tab reaches Save (the assignees pane anchors relative to Save with Dim.Fill(17)).
ROWS, COLS = 24, 100
DLL = sys.argv[1]

CAPTURE = tempfile.NamedTemporaryFile(prefix="cf_scroll_post_", suffix=".json", delete=False).name
open(CAPTURE, "w").close()

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_CUSTOM_FIELDS_MANY="1", E2E_CAPTURE_FILE=CAPTURE)
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

    # Ctrl+N opens the New Task screen; fill the required Name.
    send(b"\x0e", 2.0)
    assert "New task" in visible(), f"New Task screen didn't open:\n{visible()}"
    send(b"Tall custom field set", 1.0)

    # Tab to Save (Name -> Description -> Assignees -> List -> Priority -> Due -> Save), then Space.
    for _ in range(6):
        send(b"\t", 0.4)
    send(b" ", 2.5)  # Save -> fetch fields -> Custom fields page
    v = visible()
    assert "Custom fields" in v, f"Save did not advance to the Custom fields page:\n{v}"
    assert "Alpha" in v, f"the first custom field did not render:\n{v}"
    assert "Last Field" not in v, \
        f"'Last Field' should start below the fold on a short terminal, but was visible:\n{v}"
    print("OVERFLOW ok — Custom fields page taller than the screen; 'Last Field' below the fold")

    # Tab down to the required "Last Field" (9 text fields precede it): scroll-on-focus must bring it in.
    for _ in range(9):
        send(b"\t", 0.35)
    v = visible()
    assert "Last Field" in v, f"Tab-ing to 'Last Field' did not scroll it into view:\n{v}"
    print("SCROLL ok — Tab scrolled the below-the-fold required field into view (scroll-on-focus)")

    # Tab to Save and try to Save with the required field still empty: blocked, flashing its name.
    send(b"\t", 0.4)   # Last Field -> Save
    send(b" ", 1.5)    # Space activates Save -> required block
    v = visible()
    assert "New task" in v or "Custom fields" in v, f"required block unexpectedly closed the screen:\n{v}"
    assert "Last Field" in v and "required" in v.lower(), \
        f"required-field block did not flash the below-the-fold field 'Last Field':\n{v}"
    print("REQUIRED ok — Save blocked on the below-the-fold required field")

    # Shift+Tab back to it (focus keeps it scrolled in), fill it, then Save creates.
    send(b"\x1b[Z", 0.5)  # Save -> Last Field
    send(b"scrolled-value", 0.6)
    assert "scrolled-value" in visible(), f"typed value not shown in the field:\n{visible()}"
    send(b"\t", 0.4)      # Last Field -> Save
    send(b" ", 3.0)       # Save -> create -> close
    v = visible()
    assert "Custom fields" not in v, f"Save did not close the Custom fields page:\n{v}"
    assert "New task" not in v, f"Save did not close the New Task screen:\n{v}"
    assert "Task" in v, f"did not return to the task list after Save:\n{v}"
    print("CREATE ok — task created and returned to the list")

    with open(CAPTURE) as f:
        posted = f.read()
    assert '"custom_fields"' in posted, f"create POST carried no custom_fields array:\n{posted}"
    assert '"cf_last"' in posted and "scrolled-value" in posted, \
        f"the below-the-fold field's value did not reach the create POST:\n{posted}"
    print("ROUNDTRIP ok — the below-the-fold field's value reached the create POST (cf_last=scrolled-value)")
    print("NEW TASK CUSTOM FIELD SCROLL E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
    try: os.unlink(CAPTURE)
    except Exception: pass
