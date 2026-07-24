#!/usr/bin/env python3
"""Asserts mouse activation of in-pane links (#318, Task Detail D). Opens Task Detail and drives real
SGR-1006 mouse clicks at the two seeded links, checking where each gesture actually goes:

  - Ctrl+click a ClickUp **task** link (Description) → the browser (never in-app);
  - plain click an **other web** link (Comments)     → the browser;
  - plain click the **task** link                    → that task's Task Detail, stacked in-app
                                                       (verified by the extra Esc it then takes to reach
                                                       the list — a click that did nothing would take one);
  - a click while the comment composer is open       → nothing (an overlay owns input).

Browser launches are asserted through the harness's E2E_BROWSER_LOG recorder (one URL per line), so the
"went to the browser" half is a file fact, not a screen guess. Navigation is asserted on the pyte screen.

Mouse is injected as SGR-1006 sequences (ESC[<b;x;yM/m); the Terminal.Gui ansi driver enables mouse
reporting (?1003h + ?1006h) on boot, so only the reports themselves are written (as double_click_check.py
does). Ctrl is the +16 modifier bit on the button code.

Exits nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

TASK_URL = "https://app.clickup.com/t/86a1b2c3d"
WEB_URL = "https://github.com/rbcministries/ODBM.Secure/pull/64"

browser_log = os.path.join(tempfile.mkdtemp(prefix="link-click-"), "browser.log")

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_BROWSER_LOG=browser_log)
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
    return "\n".join(row_text(y).rstrip() for y in range(ROWS))


def find_url(url):
    """(row, start_col) of `url` on the screen, or None. Both seeded URLs fit on one row at 120 cols."""
    for y in range(ROWS):
        col = row_text(y).find(url)
        if col >= 0:
            return y, col
    return None


def _sgr(col0, row0, press, ctrl=False):
    # SGR-1006 mouse report, 1-based coords; button 0 = left, +16 = Ctrl held.
    button = 0 + (16 if ctrl else 0)
    return b"\x1b[<%d;%d;%d%s" % (button, col0 + 1, row0 + 1, b"M" if press else b"m")


def click(col0, row0, ctrl=False):
    os.write(master, _sgr(col0, row0, True, ctrl))
    os.write(master, _sgr(col0, row0, False, ctrl))
    pump(1.5)


def click_url(url, ctrl=False, offset=4):
    """Click a few chars into `url` (never its first/last cell, so an off-by-one in either direction
    would still land on the link and can't make the assertion accidentally pass on adjacent text)."""
    loc = find_url(url)
    assert loc, f"{url} not on screen:\n{visible()}"
    y, col = loc
    click(col + offset, y, ctrl=ctrl)
    return y, col


def launched():
    """The URLs the app has asked a browser to open, in order."""
    if not os.path.exists(browser_log):
        return []
    with open(browser_log) as f:
        return [line.strip() for line in f if line.strip()]


def on_detail():
    return "Description" in visible()


def esc():
    os.write(master, b"\x1b")
    pump(1.5)


try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed:\n" + visible()

    os.write(master, b"\r")          # Enter → open Task Detail
    pump(3.0)
    assert on_detail(), "detail screen did not open:\n" + visible()
    assert launched() == [], f"nothing should have been launched yet: {launched()}"

    # 0) Ordinary clicks — on prose, on empty space right of a line, low in the pane past the body —
    #    do nothing at all. (The *guards* against a clamped click reading as a link hit are pinned by
    #    DetailPaneViewTests, which can construct the wrapped-row geometry that provokes them; here the
    #    point is only that everyday clicks in a pane stay inert.)
    task_y, task_col = find_url(TASK_URL)
    click(max(task_col - 3, 0), task_y)          # the prose just before the URL
    click(COLS - 4, task_y)                      # empty space right of the line, inside the pane
    click(2, ROWS - 6)                           # low in the pane, past the body's last line
    assert launched() == [], f"a non-link click launched a browser: {launched()}"
    assert on_detail(), "a non-link click left the detail screen:\n" + visible()

    # 1) Ctrl+click the task link → the browser, and *not* in-app.
    click_url(TASK_URL, ctrl=True)
    assert launched() == [TASK_URL], f"Ctrl+click did not open the task link in the browser: {launched()}"
    assert on_detail(), "Ctrl+click should not have left the detail screen:\n" + visible()

    # 2) Plain click a web link (Comments tab) → the browser.
    web = None
    for _ in range(4):
        os.write(master, b"\x1b[1;5C")   # Ctrl+→ → next tab
        pump(1.2)
        web = find_url(WEB_URL)
        if web:
            break
    assert web, "web link not found after cycling tabs:\n" + visible()
    click_url(WEB_URL)
    assert launched() == [TASK_URL, WEB_URL], f"plain click on a web link did not open the browser: {launched()}"
    assert on_detail(), "a web-link click should not have navigated:\n" + visible()

    # 3) Plain click the task link → its Task Detail, stacked over this one. Proven by depth: two Escs
    #    are then needed to reach the list, where an inert click would have taken one.
    for _ in range(4):
        os.write(master, b"\x1b[1;5D")   # Ctrl+← → previous tab, back to the Description
        pump(1.0)
        if find_url(TASK_URL):
            break
    click_url(TASK_URL)
    pump(2.5)
    assert launched() == [TASK_URL, WEB_URL], f"a plain task-link click must not open the browser: {launched()}"
    assert on_detail(), "the stacked task detail did not open:\n" + visible()
    esc()
    assert on_detail(), "one Esc should return to the *first* detail (two levels deep):\n" + visible()
    esc()
    assert not on_detail(), "the second Esc should return to the list:\n" + visible()
    assert "Task" in visible(), "not back at the list:\n" + visible()

    # 4) A click while the comment composer is open does nothing (the overlay owns input).
    os.write(master, b"\r")              # Enter → detail again (one level)
    pump(3.0)
    assert on_detail(), "detail did not reopen:\n" + visible()
    os.write(master, b"\x0e")            # Ctrl+N → comment composer
    pump(1.5)
    before = launched()
    click_url(TASK_URL)
    assert launched() == before, f"a click under an open composer must not launch a browser: {launched()}"
    esc()                                 # discard the (empty) draft → back to the detail
    pump(1.0)
    esc()
    assert not on_detail(), "the click under the composer navigated (an extra Esc was needed):\n" + visible()

    print("ok — Ctrl+click → browser, web click → browser, task click → stacked detail, "
          "click under an open composer → inert")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
