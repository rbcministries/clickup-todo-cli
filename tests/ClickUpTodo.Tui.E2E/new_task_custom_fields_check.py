#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the New Task screen's Custom Field page (#395/§2 of #368):
Ctrl+N → fill the required Name → Save fetches the primary list's Custom Fields (seeded via
E2E_CUSTOM_FIELDS) and, because the list has fillable fields, swaps to a "Custom fields" page →
per-type widgets render (a text field, a required number field marked "*", a drop-down with its
options) → Save with the required number empty is BLOCKED with a flash naming the field → fill the
number + pick a drop-down option → Save creates the task and returns to the list. Asserts each step
on the pyte screen. Requires the fake backend's E2E_CUSTOM_FIELDS=1 field seeding."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)

master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_CUSTOM_FIELDS="1")
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

    # Ctrl+N opens the New Task screen (base page).
    send(b"\x0e", 2.0)
    assert "New task" in visible(), f"New Task screen didn't open:\n{visible()}"
    send(b"Task with custom fields", 1.0)
    assert "Task with custom fields" in visible(), f"typed name not shown:\n{visible()}"
    print("OPEN ok — New Task base page, name entered")

    # Tab to the Save button (Name -> Description -> Assignees -> List -> Priority -> Due -> Save),
    # then Space activates it. Save fetches the primary list's Custom Fields and, since the list has
    # fillable fields, advances to the Custom fields page.
    for _ in range(6):
        send(b"\t", 0.5)
    send(b" ", 2.5)  # Space activates Save -> fetch fields -> Custom fields page
    v = visible()
    assert "Custom fields" in v, f"Save did not advance to the Custom fields page:\n{v}"
    for token in ("Notes", "Estimate *", "Stage", "(none)", "Alpha", "Beta"):
        assert token in v, f"custom-field widget {token!r} did not render:\n{v}"
    print("FIELDS ok — Custom fields page rendered per-type widgets (text, required number, drop-down)")

    # Save with the required "Estimate" number still empty is blocked, flashing the missing field.
    send(b"\t", 0.4)   # Notes -> Estimate
    send(b"\t", 0.4)   # Estimate -> Stage
    send(b"\t", 0.4)   # Stage -> Save
    send(b" ", 1.5)    # Space activates Save -> required-field block
    v = visible()
    assert "New task" in v or "Custom fields" in v, f"required block unexpectedly closed the screen:\n{v}"
    assert "Estimate" in v and "required" in v.lower(), \
        f"required-field block did not flash the missing field 'Estimate':\n{v}"
    print("REQUIRED ok — Save blocked with the required 'Estimate' field empty")

    # Go back to the Estimate field and give it a value; pick a drop-down option; then Save creates.
    send(b"\x1b[Z", 0.4)  # Shift+Tab: Save -> Stage
    send(b"\x1b[Z", 0.4)  # Shift+Tab: Stage -> Estimate
    send(b"5", 0.5)
    assert "5" in visible(), f"typed estimate not shown:\n{visible()}"
    send(b"\t", 0.4)      # Estimate -> Stage drop-down
    send(b"\x1b[B", 0.5)  # Down: select "Alpha"
    send(b"\t", 0.4)      # Stage -> Save
    send(b" ", 3.0)       # Space activates Save -> create -> close
    v = visible()
    assert "Custom fields" not in v, f"Save did not close the Custom fields page:\n{v}"
    assert "New task" not in v, f"Save did not close the New Task screen:\n{v}"
    assert "Task" in v, f"did not return to the task list after Save:\n{v}"
    print("CREATE ok — task created with custom-field values and returned to the list")
    print("NEW TASK CUSTOM FIELDS E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
