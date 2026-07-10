#!/usr/bin/env python3
"""Open-a-task-from-a-feed-entry validation (#115). Boots the TUI, opens the feed
(F5), moves the cursor down, presses Enter to open the selected comment's task
detail *stacked over the feed*, and asserts:

  - Enter opens the correct task's detail (the detail tabs + the task's title).
  - Esc returns to the FEED (not the dashboard) — i.e. detail was stacked on it.
  - The feed's selected row is preserved across the round-trip.
  - A second Esc from the feed returns to the dashboard.

Usage: feed_open_check.py <e2e.dll> [out.txt]
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else "/tmp/feed_open.txt"

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

F5 = b"\x1b[15~"
DOWN = b"\x1b[B"
ENTER = b"\r"
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

def selected_row_text():
    """The text of the focus-highlighted ListView row (Terminal.Gui draws the selected
    row with a filled background — here white/#ffffff — not SGR reverse), or None. The
    amber mention chip (#f5c518) is excluded so it can't be mistaken for the selection.
    Used to prove the feed's selection survives the detail round-trip."""
    best_row, best_count = None, 0
    for r in range(ROWS):
        row = screen.buffer[r]
        count = sum(1 for c in range(COLS)
                    if row[c].bg not in ("default", "f5c518"))
        if count > best_count:
            best_count, best_row = count, r
    if best_row is None:
        return None
    row = screen.buffer[best_row]
    return "".join(row[c].data for c in range(COLS)).rstrip()

def check(cond, msg):
    if not cond:
        print("FAIL:", msg)
        print("---- visible screen ----")
        print(visible())
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        sys.exit(1)

try:
    pump(8.0)
    check("Task" in visible(), "dashboard did not boot")

    # Open the feed.
    os.write(master, F5); pump(2.5)
    check("Feed" in visible(), "feed screen title not shown")
    check("Alex Kim" in visible(), "feed rows not rendered")

    # Move the cursor down two rows, then remember the selected row so we can prove it
    # is restored after we come back from detail.
    os.write(master, DOWN); pump(0.6)
    os.write(master, DOWN); pump(0.6)
    before = selected_row_text()
    check(before is not None, "no selected (reverse-video) row on the feed")

    # Enter → open the selected comment's task detail, stacked over the feed.
    os.write(master, ENTER); pump(3.0)
    v = visible()
    check("Description" in v, "Enter did not open the task detail:\n" + v)
    check("EA-7221" in v, "detail is not the comment's task (EA-7221 title missing):\n" + v)
    with open(OUT, "w") as f:
        f.write(v)  # the detail opened from the feed

    # Esc → back to the FEED (stacked), not the dashboard.
    os.write(master, ESC); pump(2.0)
    v = visible()
    check("Feed" in v, "Esc from detail did not return to the feed:\n" + v)
    check("Alex Kim" in v, "feed rows not restored after returning from detail")
    after = selected_row_text()
    check(after == before,
          f"feed selection not preserved: before={before!r} after={after!r}")

    # Esc again → back to the dashboard.
    os.write(master, ESC); pump(1.5)
    check("Task" in visible() and "Feed" not in visible(),
          "second Esc did not return to the dashboard")

    print("ok — Enter opens the task from a feed entry, Esc returns to the feed "
          "with selection preserved, Esc again returns to the dashboard")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
