#!/usr/bin/env python3
"""Standalone feed host (#509): boots the harness with E2E_FEED=1 — the equivalent of
`clickup-todo --feed` — and asserts the app comes up straight in the mentions & comments
feed (the same NotificationsFeedScreen the dashboard opens with Ctrl+E), hosted as its own
root, NOT the dashboard list. The host seeds empty and kicks a live refresh on show, so the
same seeded feed content the in-dashboard feed shows (`feed_check.py`) lands here too: rows
render (comment authors + the mention preview), the amber " @ " mention chip is present, the
F3 mentions-only filter narrows/widens, and — because the feed is this host's root with no
list beneath — Esc hands off to the #299 exit confirmation (Y quits).

Unlike the A/B checks this is a single-run behavioural check (a brand-new boot path has no
stock baseline); it drives the real FeedApp under the PTY against the canned backend.

Usage: feed_launch_check.py <ClickUpTodo.Tui.E2E.dll> [out.txt]
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", E2E_FEED="1")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

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
    # Boot + the on-show live refresh (a comment fan-out over the assigned tasks) landing.
    pump(9.0)
    v = visible()
    # Booted straight into the feed host: the feed title + rows are present…
    check("Feed" in v, "feed host did not boot into the feed screen")
    check("Ben Seymour" in v, "feed rows (comment authors) not rendered")
    check("@bench" in v, "mention comment preview not rendered")
    # …and the dashboard list was never built (its task rows are absent) — this is the standalone
    # feed host, not the dashboard with the feed open over it.
    check("follow up on the" not in v, "dashboard list rows rendered in --feed mode")
    # The mention chip is an amber (#f5c518) truecolor background cell (same as feed_check.py).
    check("f5c518" in bg_colors(), "amber mention chip color not present")
    if OUT:
        with open(OUT, "w") as f:
            f.write(v)

    # F3 → mentions only: the non-mention author (Ben Seymour) drops; the mention (Alex Kim) stays.
    os.write(master, F3); pump(1.5)
    v = visible()
    check("mentions only" in v.lower(), "title did not switch to mentions-only")
    check("Alex Kim" in v, "the mention row disappeared under the filter")
    check("Ben Seymour" not in v, "non-mention rows were not filtered out")

    # F3 again → widen back to all comments.
    os.write(master, F3); pump(1.5)
    check("Ben Seymour" in visible(), "toggling F3 back did not restore all comments")

    # Esc at the feed root asks to confirm exit (#299) — there is no list beneath to fall back to.
    os.write(master, ESC); pump(2.0)
    confirm = visible()
    check("Are you sure you want to exit?" in confirm,
          "Esc at the feed root did not ask to confirm exit")
    check(proc.poll() is None, "Esc quit the feed host without confirming")

    # Y answers the confirmation and quits.
    os.write(master, b"Y")
    end = time.monotonic() + 5.0
    while time.monotonic() < end and proc.poll() is None:
        pump(0.3)
    check(proc.poll() is not None, "Y at the confirmation did not quit the feed host")

    print("ok — --feed boots the standalone feed host (rows + mention chip), F3 filter "
          "narrows/widens, Esc → exit confirmation, Y quits")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
