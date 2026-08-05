#!/usr/bin/env python3
"""Bare ↑/↓ in Task Detail (#452) — the regression these keys used to have.

Two mechanisms used to swallow a bare arrow in Task Detail, so it appeared inert on every tab:
  • the read-only text panes moved an invisible caret instead of scrolling the viewport, and
  • the Task Tree ListView's Command.Down bubbled up to NavSafeTabs' inert crash-guard, which
    cancelled its own MoveDown.
The app now claims bare ↑/↓ in TaskDetailScreen.OnKey and scrolls the front-most text pane one
line, or moves the Task Tree selection one row — consuming the key at a content boundary so it
never switches tabs or crashes (the NavSafeTabs guard, check 7, stays intact).

Drives, at a short terminal so the Stream body overflows its pane:
  1. Stream tab (auto-scrolled to the newest comment at the bottom): a bare ↑ scrolls the body up
     (the visible text changes), and a following bare ↓ scrolls it back down — proving one-line,
     both-directions viewport scroll, not caret movement.
  2. Task Tree tab: a bare ↓ moves the focus-highlighted row down one, and a bare ↑ moves it back —
     proving keyboard row selection, which was completely inert before the fix.

Requires E2E_TREE=1 so the five-node Task Tree tab is present. Fails on the pre-#452 code."""
import os, pty, select, struct, sys, termios, fcntl, time
import pyte, subprocess

ROWS, COLS = 18, 100
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", E2E_TREE="1", E2E_TASKS="6")
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
        r, _, _ = select.select([master], [], [], 0.03)
        if r:
            try:
                chunk = os.read(master, 65536)
            except OSError:
                break
            if not chunk:
                break
            answer(chunk)
            stream.feed(chunk)


def send(d):
    os.write(master, d)


def visible():
    return "\n".join(screen.display[y].rstrip() for y in range(ROWS))


def pane_body():
    """The text pane's inner content rows (those framed by the tab's inner border '│││…│││'),
    stripped of the border columns — the scrollable body, so a shift means the pane scrolled."""
    out = []
    for y in range(ROWS):
        t = screen.display[y]
        if t.count("│") >= 4:            # header/status rows have fewer verticals than a framed body row
            out.append(t.strip("│ ").rstrip())
    return [r for r in out if r]


TREE_NODES = ("ANCESTOR", "ROOT", "CHILDONE", "GRANDKID", "CHILDTWO")


def tree_selected_index():
    """Index (0..4) of the highlighted tree row: the row of a tree node whose cells carry the most
    non-default background (the ListView focus fill), or -1 if none stands out."""
    best_i, best_bg = -1, 0
    idx = 0
    seen = {}
    for y in range(ROWS):
        t = screen.display[y]
        node = next((n for n in TREE_NODES if n in t), None)
        if node is None:
            continue
        seen[node] = idx
        nd = sum(1 for x in range(3, 55) if screen.buffer[y][x].bg != "default")
        if nd > best_bg:
            best_bg, best_i = nd, idx
        idx += 1
    return best_i if best_bg > 20 else -1


CTRL_RIGHT = b"\x1b[1;5C"
UP = b"\x1b[A"
DOWN = b"\x1b[B"
ENTER = b"\r"


def fail(msg):
    sys.stderr.write("FAIL: " + msg + "\n\n" + visible() + "\n")
    try:
        os.killpg(os.getpgid(proc.pid), 9)
    except Exception:
        pass
    sys.exit(1)


try:
    pump(8.0)
    assert "Task 0" in visible(), "list boot failed:\n" + visible()

    # Open t0's detail. The Stream tab is front-most and auto-scrolls to the newest comment at the
    # bottom; at ROWS=18 the description + three comments overflow the pane, so there is room above.
    send(ENTER)
    pump(3.0)
    assert "Release task" in visible(), "detail did not open:\n" + visible()

    at_bottom = pane_body()
    if len(at_bottom) < 3:
        fail("Stream pane body too short to exercise scrolling (need an overflowing pane):")

    # ── 1) Text pane: bare ↑ scrolls up one line, bare ↓ scrolls back ────────────────────────────
    send(UP)
    pump(0.6)
    after_up = pane_body()
    if after_up == at_bottom:
        fail("bare ↑ did not scroll the Stream pane (pre-#452 behaviour: caret moved, no scroll):")

    send(DOWN)
    pump(0.6)
    after_down = pane_body()
    if after_down == after_up:
        fail("bare ↓ after ↑ did not scroll the Stream pane back down:")

    # ── 2) Task Tree tab: bare ↓ moves the selection down one row, bare ↑ moves it back ───────────
    for _ in range(5):               # Stream -> Description -> Comments -> Other -> Checklists -> Task Tree
        send(CTRL_RIGHT)
        pump(0.4)
    pump(3.0)                        # let the lazy tree load land
    assert "ROOT" in visible(), "Task Tree tab did not render:\n" + visible()

    start = tree_selected_index()
    if start < 0:
        fail("could not locate the highlighted tree row:")

    send(DOWN)
    pump(0.6)
    down1 = tree_selected_index()
    if down1 != start + 1:
        fail(f"bare ↓ did not move the tree selection down one row (was {start}, now {down1}):")

    send(UP)
    pump(0.6)
    up1 = tree_selected_index()
    if up1 != start:
        fail(f"bare ↑ did not move the tree selection back up (expected {start}, now {up1}):")

    print("ok — bare ↑/↓ scroll the Stream pane one line (both directions) and move the Task "
          "Tree selection one row")
    os.killpg(os.getpgid(proc.pid), 9)
except AssertionError as e:
    fail(str(e))
except SystemExit:
    raise
except Exception as e:
    fail(repr(e))
