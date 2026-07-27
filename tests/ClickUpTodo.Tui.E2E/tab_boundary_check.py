#!/usr/bin/env python3
"""Regression guard for the Task Detail tab-boundary crash.

Terminal.Gui 2.4.10's stock Tabs control binds the bare arrow keys to a tab-navigation
handler that, on cycling past the first or last tab, calls SetFocus() on the wrapped-to
tab header — which throws `InvalidOperationException: FocusChanging was not cancelled and
the HasFocus value did not change` and kills the process. The app owns tab switching via
Ctrl+←/→ and disables the native arrow navigation (NavSafeTabs).

A bare arrow only reaches the tab control after the focused view declines it, so this
drives the boundary primarily from the Task Tree tab's ListView — which declines ↑ the
instant the selection is on the top row (a read-only TextView pane instead walks its caret
first, a weaker trigger). It cycles to that tab (the LAST tab), pushes bare → past it and
bare ↑ past its top row, then also drives bare ←/→ on a text pane for good measure, and
asserts the app stays alive and rendering throughout. Reproduces the reported crash on the
stock control and passes on NavSafeTabs.

Requires E2E_TREE=1 so the Task Tree tab (a ListView) is present as the last tab.
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte
from wcwidth import wcwidth

ROWS, COLS = 50, 120
DLL = sys.argv[1]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", E2E_TREE="1")
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
    lines = []
    for y in range(ROWS):
        row = screen.buffer[y]
        out = []
        prev_wide = False
        for x in range(COLS):
            data = row[x].data
            if data == "":
                if not prev_wide:
                    out.append("▯")
                prev_wide = False
            else:
                out.append(data)
                prev_wide = len(data) > 0 and wcwidth(data[0]) == 2
        lines.append("".join(out).rstrip())
    return "\n".join(lines)

def alive():
    return proc.poll() is None

try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed:\n" + visible()

    os.write(master, b"\r")          # Enter → open detail (async fetch + screen swap)
    pump(3.0)
    assert "Description" in visible(), "detail screen did not open:\n" + visible()
    assert alive(), "process died opening detail"

    # The reliable reproduction path is the Task Tree tab: a bare arrow reaches the tab control
    # only after the focused view declines it, and its ListView declines ↑ the instant the
    # selection sits on the top row (no caret to walk, unlike the read-only TextView panes). So
    # cycle to the Task Tree tab (last of 5: Stream/Description/Comments/Other/Task Tree) with the
    # app's own Ctrl+→ and let its lazy load land — position-independent so it doesn't depend on
    # the tab we happen to be on.
    tree = ""
    for _ in range(8):
        os.write(master, b"\x1b[1;5C")   # Ctrl+→
        pump(1.2)
        tree = visible()
        if "ANCESTOR" in tree and "CHILDONE" in tree:
            break
    assert "ANCESTOR" in tree and "CHILDONE" in tree, \
        "Task Tree tab did not render its rows:\n" + tree

    # → from the Task Tree tab (the LAST tab) is the primary reported crash: the ListView declines
    # it, it bubbles to the tab control, and the stock control's SelectNextTab wraps to the first
    # tab and SetFocus()es its header — the throw site. Push it well past the boundary.
    for _ in range(8):
        os.write(master, b"\x1b[C")      # bare Right arrow — past the last tab
        pump(0.4)
    assert alive(), "process CRASHED driving bare → past the last tab:\n" + visible()

    # ↓ walks the selection down through the subtasks (a ListView gesture) — the tree tokens stay
    # on screen. ↑ from the top row previously bubbled to the stock Tabs and switched to the
    # previous tab (the reported asymmetry) — and past the first tab it wraps + SetFocus()es,
    # the second reported crash. With NavSafeTabs both are inert, so the tree stays put. Drive ↓
    # then ↑ well past the top and assert we never left the Task Tree tab.
    for _ in range(3):
        os.write(master, b"\x1b[B")      # bare Down
        pump(0.3)
    for _ in range(8):
        os.write(master, b"\x1b[A")      # bare Up — past the top row / first tab
        pump(0.3)
    assert alive(), "process CRASHED driving bare ↑ past the first tab / top row:\n" + visible()
    after = visible()
    assert "ANCESTOR" in after and "CHILDONE" in after, \
        "↑ on the Task Tree tab switched away from it (native tab-nav not disabled):\n" + after

    # Secondary coverage from a text pane: land back on a scrollable pane and drive bare ←/→ at
    # its scroll/caret edge too, so the guard isn't tied to ListView focus alone.
    os.write(master, b"\x1b[1;5C")       # Ctrl+→ → wrap to Stream
    pump(1.2)
    for _ in range(6):
        os.write(master, b"\x1b[D")      # bare Left arrow
        pump(0.3)
    for _ in range(6):
        os.write(master, b"\x1b[C")      # bare Right arrow
        pump(0.3)
    assert alive(), "process CRASHED driving bare ←/→ on a text pane:\n" + visible()

    # The screen must still be a live, rendering detail view (not a frozen last frame): the app's
    # own Ctrl+→ tab cycle should still work and the tab bar keep rendering.
    os.write(master, b"\x1b[1;5C")       # Ctrl+→ → next tab
    pump(1.2)
    assert alive(), "process died after boundary presses"
    v = visible()
    assert "Stream" in v, "detail view unresponsive after boundary presses:\n" + v

    print("ok — survived bare arrow navigation past both tab boundaries; "
          "↑ on the Task Tree tab stays on the tab")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
