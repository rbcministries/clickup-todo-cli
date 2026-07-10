#!/usr/bin/env python3
"""Feed screen validation (#114). Boots the TUI, opens the mentions & comments
feed (Ctrl+E — the List <-> Feed navigation key), and asserts: rows render
(author/preview), the mention row carries the amber " @ " chip (a truecolor bg
cell), and the F3 mentions-only toggle narrows the list to the mention and widens
back. Then Ctrl+E toggles back to the dashboard.

Usage: feed_check.py <e2e.dll> [out.txt]
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else "/tmp/feed.txt"

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

CTRL_E = b"\x05"   # List <-> Feed navigation
F3 = b"\x1bOR"
ESC = b"\x1b"

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

    # Open the feed (Ctrl+E).
    os.write(master, CTRL_E); pump(2.5)
    v = visible()
    check("Feed" in v, "feed screen title not shown")
    check("Ben Seymour" in v, "feed rows (comment authors) not rendered")
    check("@bench" in v, "mention comment preview not rendered")
    with open(OUT, "w") as f:
        f.write(v)  # the "all comments" feed view

    # The mention chip is an amber (#f5c518) truecolor background cell.
    check("f5c518" in bg_colors(), "amber mention chip color not present")

    # F3 → mentions only: the non-mention author (Ben Seymour) drops; the mention (Alex Kim) stays.
    os.write(master, F3); pump(1.5)
    v = visible()
    check("mentions only" in v.lower(), "title did not switch to mentions-only")
    check("Alex Kim" in v, "the mention row disappeared under the filter")
    check("Ben Seymour" not in v, "non-mention rows were not filtered out")
    with open(OUT + ".mentions", "w") as f:
        f.write(v)  # the mentions-only feed view

    # F3 again → widen back to all comments.
    os.write(master, F3); pump(1.5)
    v = visible()
    check("Ben Seymour" in v, "toggling back did not restore all comments")

    # Ctrl+E → toggle back to the dashboard, cursor intact.
    os.write(master, CTRL_E); pump(1.5)
    check("Task" in visible(), "did not return to the dashboard")

    with open(OUT + ".dashboard", "w") as f:
        f.write(visible())  # the restored dashboard (OUT keeps the all-comments feed view)
    print("ok — feed renders, mention chip present, F3 filter narrows/widens, Ctrl+E toggles back")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
