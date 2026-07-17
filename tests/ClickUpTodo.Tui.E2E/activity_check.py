#!/usr/bin/env python3
"""Feed recent-activity source validation (#117). Boots the TUI, opens the mentions &
comments feed (Ctrl+E), and asserts the F6 "show/hide activity" display state:

  - The feed opens comments-only: seeded task titles (recent activity) are ABSENT and
    the title has no "(+activity)" suffix.
  - F6 merges the recent-activity source in: a seeded assigned-task title appears and
    the title shows "(+activity)".
  - F6 again drops the activity rows back out (comments-only again).
  - Ctrl+E returns to the dashboard.

Activity is a client-side projection of the assigned tasks the feed already fetches, so
F6 is a local re-render (no re-fetch) — the rows appear/disappear without a network wait.

Usage: activity_check.py <ClickUpTodo.Tui.E2E.dll> [out.txt]
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else "/tmp/activity.txt"

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

CTRL_E = b"\x05"   # List <-> Feed navigation
F6 = b"\x1b[17~"   # show/hide recent activity
ESC = b"\x1b"

# A distinctive fragment of the seeded assigned-task titles ("Task N — follow up on the
# <List> item with a realistic title"). It appears on the dashboard and — once F6 is on —
# as recent-activity rows in the feed, but never in a comment preview.
TASK_TITLE_FRAGMENT = "follow up on the"

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
    return "\n".join(line.rstrip() for line in screen.display).rstrip()

def bg_colors():
    found = set()
    for r in range(ROWS):
        row = screen.buffer[r]
        for c in range(COLS):
            found.add(row[c].bg)
    return found

def check(cond, msg):
    if not cond:
        print("FAIL:", msg)
        print("---- visible screen ----")
        print(visible())
        print("---- bg colors seen ----")
        print(sorted(bg_colors()))
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        sys.exit(1)

try:
    pump(8.0)
    check("Task" in visible(), "dashboard did not boot")

    # Open the feed (Ctrl+E). It opens comments-only (F6 defaults off).
    os.write(master, CTRL_E); pump(2.5)
    v = visible()
    check("Feed" in v, "feed screen title not shown")
    check("Ben Seymour" in v, "feed rows (comment authors) not rendered")
    check(TASK_TITLE_FRAGMENT not in v, "recent-activity task titles showed before F6 was pressed")
    check("+activity" not in v, "title showed the (+activity) suffix before F6 was pressed")
    with open(OUT, "w") as f:
        f.write(v)  # comments-only feed view

    # F6 → merge the recent-activity source in. A seeded assigned-task title now appears,
    # and the title reflects the state. No re-fetch: the activity was loaded with the feed.
    os.write(master, F6); pump(2.0)
    v = visible()
    check("+activity" in v, "title did not gain the (+activity) suffix after F6")
    check(TASK_TITLE_FRAGMENT in v, "recent-activity task rows did not appear after F6")
    # The activity chip is a cool-blue (#4aa3df) truecolor background cell.
    check("4aa3df" in bg_colors(), "activity chip color not present after F6")
    with open(OUT + ".activity", "w") as f:
        f.write(v)  # feed with recent activity merged in

    # F6 again → hide activity; the task rows drop back out and the suffix clears.
    os.write(master, F6); pump(2.0)
    v = visible()
    check(TASK_TITLE_FRAGMENT not in v, "recent-activity task rows did not drop out after toggling F6 off")
    check("+activity" not in v, "title kept the (+activity) suffix after toggling F6 off")

    # Ctrl+E → back to the dashboard.
    os.write(master, CTRL_E); pump(1.5)
    check("Task" in visible(), "did not return to the dashboard")

    print("ok — feed opens comments-only, F6 merges recent-activity rows in and back out, Ctrl+E returns")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
