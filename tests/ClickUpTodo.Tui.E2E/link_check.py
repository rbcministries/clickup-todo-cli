#!/usr/bin/env python3
"""Asserts the in-text link styling added in #317 (Task Detail C). Opens Task Detail and
inspects the pyte-rendered cells of two seeded links:

  - a ClickUp **task** link in the Description  (https://app.clickup.com/t/86a1b2c3d)
  - an **other web** link in the Comments        (https://github.com/rbcministries/ODBM.Secure/pull/64)

Relational assertions (so the check is robust to pyte's exact colour encoding — the unit tests
pin the concrete attributes):
  - both links' cells are UNDERLINED (pyte `underscore`);
  - the task link keeps the SAME foreground as normal body text (task styling = default fg + underline);
  - the web link is RECOLOURED (foreground differs from normal body text) and uniform across the URL;
  - a normal body cell is NOT underlined (underline is specific to links).

Asserts on the pyte screen, never raw bytes. Exits nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

TASK_URL = "https://app.clickup.com/t/86a1b2c3d"
WEB_URL = "https://github.com/rbcministries/ODBM.Secure/pull/64"

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


def find_url(url):
    """(row, start_col) of `url` on the screen, or None. URLs are short enough not to wrap."""
    for y in range(ROWS):
        col = row_text(y).find(url)
        if col >= 0:
            return y, col
    return None


def visible():
    return "\n".join(row_text(y).rstrip() for y in range(ROWS))


def url_cells(y, col, url):
    return [screen.buffer[y][col + i] for i in range(len(url))]


def normal_ref_cell(y, url_col, url_len):
    """A normal body cell on the same row, OUTSIDE the URL span: an alphabetic character. Prefers a
    letter before the URL (e.g. "Parent ticket:" / "PR:"), but falls back to one after it, so the check
    isn't coupled to wrap width — if word-wrap ever pushed a URL to column 0 there is still a reference."""
    for x in list(range(url_col)) + list(range(url_col + url_len, COLS)):
        if screen.buffer[y][x].data.isalpha():
            return screen.buffer[y][x]
    raise AssertionError(f"no normal reference letter outside the URL on row {y}: {row_text(y)!r}")


try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed"

    os.write(master, b"\r")          # Enter → open detail (opens on the Description tab)
    pump(3.0)
    assert "Description" in visible(), "detail screen did not open:\n" + visible()

    # ── Task link on the Description tab: underlined, same fg as normal text ─────────────────────────
    loc = find_url(TASK_URL)
    assert loc, "task link not found on the Description tab:\n" + visible()
    y, col = loc
    ref = normal_ref_cell(y, col, len(TASK_URL))
    cells = url_cells(y, col, TASK_URL)
    assert all(c.underscore for c in cells), \
        f"task link not underlined: {[(c.data, c.underscore) for c in cells]}"
    assert not ref.underscore, f"normal body text is underlined (ref {ref.data!r})"
    # Task styling = the read-only foreground + underline, so the URL cells share the fg the driver
    # emits for base read-only text (both go through the same Color→SGR path). A regression here would
    # surface as a spurious fg mismatch rather than a wrong colour.
    assert all(c.fg == ref.fg for c in cells), \
        f"task link fg {sorted({c.fg for c in cells})} != normal body fg {ref.fg!r} (task = default fg + underline)"

    # ── Web link on the Comments tab: underlined, recoloured (fg differs from normal), uniform ───────
    web = None
    for _ in range(4):
        os.write(master, b"\x1b[1;5C")   # Ctrl+→ → next tab
        pump(1.2)
        web = find_url(WEB_URL)
        if web:
            break
    assert web, "web link not found after cycling tabs:\n" + visible()
    y, col = web
    ref = normal_ref_cell(y, col, len(WEB_URL))
    cells = url_cells(y, col, WEB_URL)
    assert all(c.underscore for c in cells), \
        f"web link not underlined: {[(c.data, c.underscore) for c in cells]}"
    web_fgs = {c.fg for c in cells}
    assert len(web_fgs) == 1, f"web link fg not uniform across the URL: {sorted(web_fgs)}"
    assert web_fgs != {ref.fg}, \
        f"web link fg {sorted(web_fgs)} not recoloured vs normal body fg {ref.fg!r}"

    print("ok — task link underlined+default-fg (Description), web link underlined+recoloured (Comments)")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
