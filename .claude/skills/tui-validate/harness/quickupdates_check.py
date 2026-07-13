#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the Quick Updates screen (#156):
Space opens it → Tab cycles Status→Priority→Assignees (wrapping) → Esc exits.
Asserts the three pane titles render on open and that Esc returns to the list."""
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
    v = visible()
    for token in ("Quick Updates", "Status", "Priority", "Assignees"):
        assert token in v, f"missing {token!r} after opening Quick Updates:\n{v}"
    print("OPEN ok — panes:", [t for t in ("Status", "Priority", "Assignees") if t in v])

    # Tab three times: Status→Priority→Assignees→Status (wraps). Screen must stay intact.
    for i in range(3):
        send(b"\t", 0.8)
        assert "Quick Updates" in visible(), f"screen lost after Tab #{i+1}:\n{visible()}"
    print("TAB x3 ok — screen intact and still on Quick Updates")

    # Shift+Tab (CSI Z) cycles backward; still intact.
    send(b"\x1b[Z", 0.8)
    assert "Quick Updates" in visible(), "screen lost after Shift+Tab"
    print("SHIFT+TAB ok")

    # Esc exits back to the task list.
    send(b"\x1b", 1.5)
    v = visible()
    assert "Quick Updates" not in v, f"Esc did not close Quick Updates:\n{v}"
    assert "Task" in v, f"did not return to the task list after Esc:\n{v}"
    print("ESC ok — returned to the task list")
    print("QUICK UPDATES E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
