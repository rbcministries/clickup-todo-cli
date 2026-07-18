#!/usr/bin/env python3
"""Clickable contextual help bar (#289). Boots the TUI under a PTY, renders through
a real VT emulator (pyte), and injects SGR-1006 mouse clicks on the bottom footer:

  1. a left-click on an ACTION hint ("Ctrl+N new task") fires its shortcut — the
     New Task screen opens (the footer becomes the New Task set);
  2. a left-click on a MOVEMENT hint ("↑/↓ move") does nothing — still on the list;
  3. a rapid double-click on an action fires it exactly ONCE (Terminal.Gui raises
     LeftButtonClicked then LeftButtonDoubleClicked; the handler acts only on the
     former), so one Esc returns to the list.

The footer stays a single non-focusable Label, so clicking it never moves focus;
the click is re-raised as the item's key chord to the focused ListView/screen.

Exits nonzero / prints a traceback on failure (harness convention)."""
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
    return [line.rstrip() for line in screen.display]

def find(substr):
    for y, line in enumerate(visible()):
        c = line.find(substr)
        if c >= 0:
            return y, c
    raise AssertionError(f"{substr!r} not on screen:\n" + "\n".join(visible()))

def click(col0, row0, times=1):  # 0-based screen coords → SGR-1006 (1-based)
    for _ in range(times):
        os.write(master, b"\x1b[<0;%d;%dM" % (col0 + 1, row0 + 1))  # press
        os.write(master, b"\x1b[<0;%d;%dm" % (col0 + 1, row0 + 1))  # release

# The New Task footer is the unambiguous signal that the compose screen is open.
NEW_TASK = "Enter/Save saves"

try:
    pump(9.0)
    assert "Task" in "\n".join(visible()), "boot failed:\n" + "\n".join(visible()[-5:])

    # 1) Click the middle of the "Ctrl+N new task" action hint → New Task opens.
    y, c = find("Ctrl+N new task")
    click(c + 3, y)
    pump(2.0)
    assert NEW_TASK in "\n".join(visible()), \
        "clicking 'Ctrl+N new task' did not open New Task:\n" + "\n".join(visible()[-6:])
    print("PASS 1: clicking an action hint fires its shortcut (New Task opened)")

    os.write(master, b"\x1b")  # Esc back to the list
    pump(1.5)
    assert NEW_TASK not in "\n".join(visible()), "Esc did not close New Task"

    # 2) Click a movement hint → no action fires; still on the list.
    y2, c2 = find("↑/↓ move")
    click(c2 + 1, y2)
    pump(1.5)
    scr = "\n".join(visible())
    assert NEW_TASK not in scr and "Task" in scr, \
        "clicking a movement hint wrongly fired something:\n" + scr[-800:]
    print("PASS 2: clicking a movement hint does nothing")

    # 3) Rapid double-click on the action fires it exactly once (one Esc returns).
    y3, c3 = find("Ctrl+N new task")
    click(c3 + 3, y3, times=2)
    pump(2.0)
    assert NEW_TASK in "\n".join(visible()), "double-click did not open New Task"
    os.write(master, b"\x1b")  # a single Esc
    pump(1.5)
    assert NEW_TASK not in "\n".join(visible()), \
        "double-click fired twice (two New Task screens stacked)"
    print("PASS 3: a double-click fires the action exactly once")

    print("\nALL HELP-BAR CLICK CHECKS PASSED")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
