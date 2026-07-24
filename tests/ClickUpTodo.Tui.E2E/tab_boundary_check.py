#!/usr/bin/env python3
"""Regression guard for the Task Detail tab-boundary crash.

Terminal.Gui 2.4.10's stock Tabs control binds the bare arrow keys to a tab-navigation
handler that, on cycling past the first or last tab, calls SetFocus() on the wrapped-to
tab header — which throws `InvalidOperationException: FocusChanging was not cancelled and
the HasFocus value did not change` and kills the process. The app owns tab switching via
Ctrl+←/→ and disables the native arrow navigation (NavSafeTabs), so this drives bare
→ well past the last tab and bare ← well past the first and asserts the app is still
alive and rendering afterwards.

Run with E2E_TREE=1 so the crashing Task Tree tab (a ListView) is present as the last tab.
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

    # Cycle tab focus onto the header bar the way the reporter did, then push bare → far past
    # the last tab: 4 named tabs + Task Tree = 5, so 12 presses wraps the end several times.
    for _ in range(12):
        os.write(master, b"\x1b[C")   # bare Right arrow
        pump(0.4)
    assert alive(), "process CRASHED driving bare → past the last tab:\n" + visible()

    # ...and bare ← far past the first tab (the second reported crash).
    for _ in range(12):
        os.write(master, b"\x1b[D")   # bare Left arrow
        pump(0.4)
    assert alive(), "process CRASHED driving bare ← past the first tab:\n" + visible()

    # The screen must still be a live, rendering detail view (not a frozen last frame): the
    # app's own Ctrl+→ tab cycle should still work and the tab bar keep rendering.
    os.write(master, b"\x1b[1;5C")    # Ctrl+→ → next tab
    pump(1.2)
    assert alive(), "process died after boundary presses"
    v = visible()
    assert "Stream" in v, "detail view unresponsive after boundary presses:\n" + v

    # ── Task Tree tab: ↑ must not switch tabs (was the reported asymmetry) ──────────
    # Cycle with the app's own Ctrl+→ until the Task Tree tab (last of 5) is front-most and
    # its lazy load has landed — position-independent so it doesn't depend on the current tab.
    tree = ""
    for _ in range(8):
        os.write(master, b"\x1b[1;5C")   # Ctrl+→
        pump(1.2)
        tree = visible()
        if "ANCESTOR" in tree and "CHILDONE" in tree:
            break
    assert "ANCESTOR" in tree and "CHILDONE" in tree, \
        "Task Tree tab did not render its rows:\n" + tree

    # ↓ moves the selection down through the subtasks (a ListView gesture) — the tree tokens
    # stay on screen. ↑ from the top row previously bubbled to the stock Tabs and switched to
    # the previous tab; with NavSafeTabs it's inert, so the tree stays put. Press ↑ well past
    # the top and assert we're still on the Task Tree tab (its distinctive rows still shown).
    for _ in range(3):
        os.write(master, b"\x1b[B")      # bare Down
        pump(0.3)
    for _ in range(6):
        os.write(master, b"\x1b[A")      # bare Up (past the top row)
        pump(0.3)
    assert alive(), "process died driving ↑/↓ on the Task Tree tab"
    after = visible()
    assert "ANCESTOR" in after and "CHILDONE" in after, \
        "↑ on the Task Tree tab switched away from it (native tab-nav not disabled):\n" + after

    print("ok — survived bare arrow navigation past both tab boundaries; "
          "↑ on the Task Tree tab stays on the tab")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
