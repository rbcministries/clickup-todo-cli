#!/usr/bin/env python3
"""Regression guard for #413 — the wrapped-line variant of the #317 link styling that
`link_check.py` deliberately does not cover (it fixes COLS=120 "so the seeded URLs don't wrap").

Runs at a **narrow** COLS so the seeded Description line

    Parent ticket: https://app.clickup.com/t/86a1b2c3d for the full thread

word-wraps and the task URL lands on a **continuation row**, then asserts on the pyte screen that:

  - the URL is found contiguously on a row *below* the "Parent ticket:" prose (i.e. it wrapped);
  - every cell of the URL is UNDERLINED;
  - the prose that trails the URL on that row (" for the") is NOT underlined — the exact failure in
    #413, where Terminal.Gui 2.4.10's word wrap rebuilt the wrapped row's attributes from source index 0
    so the underline was painted `len("Parent ticket: ")` columns too far right, running off the URL;
  - the URL keeps the normal body foreground (task styling = default fg + underline), matching a normal
    body reference cell.

Before the #413 fix the underline is shifted right into the trailing prose, so the "URL underlined" /
"trailing prose not underlined" assertions fail. Asserts on the pyte screen, never raw bytes; exits
nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

# COLS is hand-tuned to the detail pane's current inner width (after borders): wide enough that the
# 35-char task URL fits whole on one continuation row, narrow enough that "Parent ticket: " + URL
# overflows and the URL wraps. A layout/padding change to the pane may need this retuned — every failure
# mode below raises a specific assertion rather than passing spuriously, so a retune need is obvious.
ROWS, COLS = 40, 50
DLL = sys.argv[1]

TASK_URL = "https://app.clickup.com/t/86a1b2c3d"
PROSE = "Parent ticket:"

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


def row_text(y):
    return "".join(screen.buffer[y][x].data for x in range(COLS))


def visible():
    return "\n".join(row_text(y) for y in range(ROWS))


def find_text(needle):
    """(row, start_col) of `needle` on the screen, or None."""
    for y in range(ROWS):
        col = row_text(y).find(needle)
        if col >= 0:
            return y, col
    return None


try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed"

    os.write(master, b"\r")          # Enter → open detail
    pump(3.0)
    assert "Description" in visible(), "detail screen did not open:\n" + visible()

    # Land on the Description tab (Ctrl+← cycles tabs); stop as soon as the seeded prose is visible.
    for _ in range(6):
        if PROSE in visible():
            break
        os.write(master, b"\x1b[1;5D")   # Ctrl+←
        pump(1.0)
    assert PROSE in visible(), "Description tab (with the seeded 'Parent ticket:' line) not reached:\n" + visible()

    prose = find_text(PROSE)
    loc = find_text(TASK_URL)
    assert loc, "task URL did not render contiguously (did it hard-wrap mid-URL? widen COLS):\n" + visible()
    py, _ = prose
    y, col = loc
    assert y > py, f"task URL did not wrap onto a continuation row (URL row {y} <= prose row {py}) — " \
                   f"COLS={COLS} is too wide to exercise #413:\n" + visible()

    # Every cell of the URL is underlined.
    url_cells = [screen.buffer[y][col + i] for i in range(len(TASK_URL))]
    assert all(c.underscore for c in url_cells), \
        f"wrapped task URL not fully underlined: {[(c.data, c.underscore) for c in url_cells]}"

    # The prose trailing the URL on the wrapped row (" for the") must NOT be underlined — the #413 bug
    # painted the underline shifted right into exactly these cells. Take the first alphabetic cell after
    # the URL as the reference.
    trailing = None
    for x in range(col + len(TASK_URL), COLS):
        if screen.buffer[y][x].data.isalpha():
            trailing = screen.buffer[y][x]
            break
    assert trailing is not None, f"no trailing prose after the URL to check on row {y}: {row_text(y)!r}"
    assert not trailing.underscore, \
        f"prose trailing the URL is underlined (ref {trailing.data!r}) — underline ran off the URL (#413)"

    # Task styling keeps the normal body foreground (default fg + underline), so the URL cells share the
    # foreground of a normal (non-underlined) body letter.
    assert all(c.fg == trailing.fg for c in url_cells), \
        f"wrapped task URL fg {sorted({c.fg for c in url_cells})} != normal body fg {trailing.fg!r}"

    print("ok — wrapped task URL underlined exactly (no shift into trailing prose), COLS=%d" % COLS)
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
