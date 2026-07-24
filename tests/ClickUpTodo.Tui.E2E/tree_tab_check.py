#!/usr/bin/env python3
"""Drives the Task Detail "Task Tree" tab (#291) end-to-end against the in-process fake backend
(E2E_TREE=1 serves a fixed ancestry/child tree hung off the opened task t0):

    tanc (Ancestor epic ANCESTOR)
      └─ t0   (Release task ROOT)          ← the opened task
           ├─ t0c1  (Subtask one CHILDONE)
           │    └─ t0c1a (Nested subtask GRANDKID)
           └─ t0c2  (Subtask two CHILDTWO)

Asserts:
  1. Cycling to the Task Tree tab renders ancestry + the task + its descendants (all five nodes),
     indented — proving the shared TaskRowRenderer drove real rows. Its badge display mirrors the
     main list's state on open, and F6 cycles it in place (#415): text -> hidden -> icons -> text.
  2. Enter on a highlighted child row navigates the detail to that task (its header changes; the
     other tree rows are gone because the new detail opens on its Stream tab).
  3. A single Esc after navigating returns to the MAIN LIST (not the task we came from) — the
     replace-in-place semantics the issue requires.
  4. Double-clicking a tree row navigates the same way (the mouse equivalent of Enter, via the
     shared RowHitTester).

Mouse is injected as SGR-1006 sequences, exactly like double_click_check.py."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
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


def visible():
    return "\n".join(screen.display[y].rstrip() for y in range(ROWS))


def send(data):
    os.write(master, data)


def _sgr(col0, row0, press):
    return b"\x1b[<0;%d;%d%s" % (col0 + 1, row0 + 1, b"M" if press else b"m")


def double_click(col0, row0, gap=0.08):
    send(_sgr(col0, row0, True)); send(_sgr(col0, row0, False))
    time.sleep(gap)
    send(_sgr(col0, row0, True)); send(_sgr(col0, row0, False))


def row_of(substr):
    """The screen row index whose text contains substr, or -1."""
    for y in range(ROWS):
        if substr in screen.display[y]:
            return y
    return -1


def rows_with(substr):
    """How many screen rows contain substr. The detail HEADER always shows the current task's
    "○ in progress" status once, independent of the tree's badge mode; the tree ROWS add one each
    only in text mode — so this count separates the two (1 = header only, >1 = header + text badges)."""
    return sum(1 for y in range(ROWS) if substr in screen.display[y])


def pos_of(substr):
    """(col, row) of the first occurrence of substr on the screen, or (-1, -1)."""
    for y in range(ROWS):
        i = screen.display[y].find(substr)
        if i >= 0:
            return (i, y)
    return (-1, -1)


def click(col0, row0):
    send(_sgr(col0, row0, True)); send(_sgr(col0, row0, False))


CTRL_RIGHT = b"\x1b[1;5C"
DOWN = b"\x1b[B"
ENTER = b"\r"
ESC = b"\x1b"
F6 = b"\x1b[17~"


def open_tree_tab():
    """From the main list: open t0's detail and cycle to the Task Tree tab (5th)."""
    send(ENTER)          # open the focused task (t0) in Task Detail
    pump(3.0)
    for _ in range(4):   # Stream -> Description -> Comments -> Other -> Task Tree
        send(CTRL_RIGHT)
        pump(0.4)
    pump(3.0)            # let the lazy tree load land


try:
    pump(8.0)
    assert "Task 0" in visible(), "list boot failed:\n" + visible()

    # ── 1) Render + badge state mirrors the main list; F6 cycles it in the tab (#415) ─────────
    # Every tree node's status is "in progress": in text mode each row carries its "○ in progress"
    # name badge; in icons mode the abbreviation chip "(IP)"; in hidden mode neither. The detail
    # header always shows "○ in progress" once, so rows_with("in progress") is 1 (header only) when
    # the tree badges are hidden/icon and >1 when they're text.
    #
    # Drive the MAIN LIST to HIDDEN first (icons -> text -> hidden). Opening the tree in hidden — not
    # the icons default and not the old hardcoded text — proves the tab inherits the list's live state.
    send(F6); pump(0.6)            # icons -> text
    send(F6); pump(0.6)            # text  -> hidden
    open_tree_tab()
    v = visible()
    for token in ("ANCESTOR", "ROOT", "CHILDONE", "GRANDKID", "CHILDTWO"):
        assert token in v, f"tree tab missing {token}:\n{v}"
    assert rows_with("in progress") == 1, "tree tab did not open in the list's hidden-badge state:\n" + v
    assert "(IP)" not in v, "tree tab opened with icon badges, not the list's hidden state:\n" + v

    # F6 on the tab cycles like the main list — a pure in-place re-render, rows/tokens stay put:
    #   hidden -> icons (the "(IP)" abbreviation chips appear; still no status name in the rows),
    send(F6); pump(1.0)
    v = visible()
    assert "(IP)" in v, "F6 did not switch the tree to icon badges:\n" + v
    assert rows_with("in progress") == 1, "icon mode should not show the status name in rows:\n" + v
    assert "ROOT" in v, "cycling badges dropped the tree rows:\n" + v
    #   icons -> text (the "○ in progress" name badges appear on the rows),
    send(F6); pump(1.0)
    v = visible()
    assert rows_with("in progress") > 1, "F6 did not switch the tree to text badges:\n" + v
    assert "(IP)" not in v, "text mode should not show the icon abbreviation chip:\n" + v
    #   text -> hidden (both vanish, header aside).
    send(F6); pump(1.0)
    v = visible()
    assert rows_with("in progress") == 1, "F6 did not cycle the tree back to hidden badges:\n" + v
    assert "(IP)" not in v, "hidden mode should show no badges:\n" + v

    # ── 2) Enter-navigate into a child; 3) Esc WALKS BACK one task at a time ───────────────────
    # Move to the bottom row (the ListView clamps there) so the selection is deterministically a
    # non-current task — CHILDTWO, the last arranged row — regardless of the initial cursor.
    for _ in range(4):
        send(DOWN)
        pump(0.2)
    send(ENTER)          # open CHILDTWO's detail stacked over t0's (no replace-in-place)
    pump(3.0)
    v = visible()
    assert "CHILDTWO" in v, "Enter did not navigate to the child task:\n" + v
    assert "GRANDKID" not in v, "expected the new detail on its Stream tab, not the tree:\n" + v
    assert "Release task" not in v, "still showing the task we navigated from:\n" + v

    # Shared badge state across the visited-task stack (#415): the mode is currently hidden (end of §1).
    # Cycle to CHILDTWO's own Task Tree tab and F6 there (hidden -> icons). Because the host reflects the
    # cycle into every stacked detail — not just the front-most — t0's tree beneath must adopt it too, so
    # Esc-ing back below shows t0's tree in icons, not the stale hidden it was last rendered in.
    for _ in range(4):   # CHILDTWO detail: Stream -> Description -> Comments -> Other -> Task Tree
        send(CTRL_RIGHT)
        pump(0.4)
    pump(2.0)            # let CHILDTWO's (ancestry-only) tree load
    assert rows_with("in progress") == 1, "child tree did not open in the shared hidden state:\n" + visible()
    send(F6); pump(1.0)  # hidden -> icons on the child's tree
    assert "(IP)" in visible(), "F6 on the child tree did not switch to icon badges:\n" + visible()

    # A single Esc = Back: return to the PREVIOUS task (t0's detail, still on its Task Tree tab),
    # NOT straight to the list — the walkable-back model (#401/#298), uniform with Ctrl+O (#387).
    send(ESC)
    pump(2.0)
    v = visible()
    assert "Release task" in v, "Esc did not walk back to the previous task's detail:\n" + v
    assert "Task 0 —" not in v, "Esc jumped past the previous task straight to the list:\n" + v
    assert "(IP)" in v, "t0's stacked tree did not adopt the badge mode cycled on the child (#415):\n" + v
    # A second Esc from the root detail returns to the main list.
    send(ESC)
    pump(2.0)
    assert "Task 0" in visible(), "second Esc did not return to the main list:\n" + visible()

    # ── 4) Double-click a tree row navigates the same way (stacked) ────────────────────────────
    open_tree_tab()
    y = row_of("CHILDONE")
    assert y >= 0, "CHILDONE row not found for double-click:\n" + visible()
    double_click(12, y)
    pump(3.0)
    v = visible()
    assert "CHILDONE" in v, "double-click did not navigate to the child task:\n" + v
    assert "GRANDKID" not in v, "double-click did not land on the new detail's Stream tab:\n" + v
    assert "CHILDTWO" not in v, "double-click did not land on the new detail's Stream tab:\n" + v

    send(ESC)            # back to t0's detail (walkable back)
    pump(2.0)
    assert "Release task" in visible(), "Esc did not walk back to the previous detail:\n" + visible()
    send(ESC)            # back to the list
    pump(2.0)
    assert "Task 0" in visible(), "second Esc after double-click did not return to the list:\n" + visible()

    # ── 5) Clicking the "Task Tree" tab header (mouse) lazy-loads the tree ─────────────────────
    # Selecting the tab by clicking its header must trigger the same lazy load as Ctrl+←/→, not leave
    # it stuck on "Loading task tree…".
    send(ENTER)          # reopen t0's detail (opens on the Stream tab)
    pump(3.0)
    cx, cy = pos_of("Task Tree")
    assert cx >= 0, "Task Tree tab header not found:\n" + visible()
    click(cx + 2, cy)    # single-click the tab header
    pump(3.0)
    v = visible()
    assert "ANCESTOR" in v and "GRANDKID" in v, "tab-header click did not load the tree:\n" + v
    assert "Loading task tree" not in v, "tab-header click left the tree stuck loading:\n" + v

    print("ok")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
