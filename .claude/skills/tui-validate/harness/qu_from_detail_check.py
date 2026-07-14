#!/usr/bin/env python3
"""#159 end-to-end: Quick Updates launchable from Task Detail, return to origin.

Drives the real app under a PTY:
  A. list -> Space opens Quick Updates -> Esc returns to the list.
  B. list -> Enter opens detail -> Ctrl+U stacks Quick Updates over it -> Esc pops
     back to the *detail* (not the list).
  C. detail -> Ctrl+U -> down to 'complete' -> Enter applies and pops back to the
     detail, which now reflects the new status (optimistic, #159).

Asserts on the pyte-rendered screen. Exits nonzero with the offending screen on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
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
            answer(chunk)
            stream.feed(chunk)

def visible():
    return "\n".join(screen.display).replace(" ", " ")

def require(cond, msg):
    if not cond:
        print("FAIL:", msg)
        print("----- screen -----")
        print(visible())
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        sys.exit(1)

def qu_open(v):
    # The Quick Updates screen is the only one with all three frame titles at once.
    return "Quick Updates" in v and "Priority" in v and "Assignees" in v

CTRL_U = b"\x15"
DOWN = b"\x1b[B"

try:
    pump(8.0)
    require("Task" in visible(), "list did not boot")
    require(not qu_open(visible()), "Quick Updates unexpectedly open at boot")

    # A. list -> Space -> Quick Updates -> Esc -> list
    os.write(master, b" ")
    pump(2.5)
    require(qu_open(visible()), "Space from the list did not open Quick Updates")
    os.write(master, b"\x1b")
    pump(2.0)
    require("Task" in visible() and not qu_open(visible()),
            "Esc from Quick Updates (list origin) did not return to the list")

    # B. list -> Enter -> detail -> Ctrl+U -> Quick Updates -> Esc -> detail
    os.write(master, b"\r")
    pump(3.0)
    require("Description" in visible(), "Enter did not open the detail view")
    require("in review" in visible(), "detail did not show the seeded status 'in review'")
    os.write(master, CTRL_U)
    pump(2.5)
    require(qu_open(visible()), "Ctrl+U in the detail view did not open Quick Updates")
    os.write(master, b"\x1b")
    pump(2.0)
    require("Description" in visible() and not qu_open(visible()),
            "Esc from Quick Updates (detail origin) did not return to the detail view")

    # C. detail -> Ctrl+U -> down to 'complete' (last status) -> Enter -> detail reflects it
    os.write(master, CTRL_U)
    pump(2.0)
    require(qu_open(visible()), "second Ctrl+U did not reopen Quick Updates")
    for _ in range(5):            # clamp to the last status row: 'complete'
        os.write(master, DOWN)
        pump(0.3)
    os.write(master, b"\r")       # Enter applies + closes
    pump(2.5)
    require("Description" in visible() and not qu_open(visible()),
            "Enter in Quick Updates (detail origin) did not pop back to the detail view")
    require("complete" in visible(),
            "detail view did not reflect the status changed via Quick Updates (#159)")

    print("ok — #159 list/detail origins + return-to-origin + status reflection")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
