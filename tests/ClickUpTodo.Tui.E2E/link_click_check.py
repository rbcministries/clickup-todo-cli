#!/usr/bin/env python3
"""Asserts mouse activation of in-pane links (#318, Task Detail D). Drives real SGR-1006 mouse clicks at
the two seeded links and checks where each gesture actually goes.

Dashboard host (`TodoApp`):
  - ordinary clicks (prose, empty space right of a line, below the body)  → nothing;
  - `Ctrl`+click a ClickUp **task** link (Description)                    → the browser, never in-app;
  - plain click an **other web** link (Comments)                          → the browser;
  - plain click the **task** link                                         → that task's Task Detail,
      stacked in-app (verified by the extra Esc it then takes to reach the list — a click that did
      nothing would take one);
  - a click while any overlay owns input — the comment composer (`Ctrl+N`), the Dispatch pane (`Ctrl+A`)
      or the description editor (`Ctrl+E`)                                → nothing.

Single-task host (`SingleTaskApp`, `E2E_SINGLE_TASK=…`), which has no in-app task→task destination
(#374), so both actions degrade to the browser and the tab stays open:
  - plain click the **task** link  → the browser, process still alive;
  - `Ctrl`+click it               → the browser again.

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
SINGLE_TASK_ID = "t5"


class App:
    """The harness app under a PTY, with a pyte screen and its own browser-launch recorder."""

    def __init__(self, **extra_env):
        self.log = os.path.join(tempfile.mkdtemp(prefix="link-click-"), "browser.log")
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
                   E2E_BROWSER_LOG=self.log, **extra_env)
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    # ── plumbing ──────────────────────────────────────────────────────────────
    def _answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")

    def pump(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            r, _, _ = select.select([self.master], [], [], 0.05)
            if r:
                try:
                    chunk = os.read(self.master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                self._answer(chunk)
                self.stream.feed(chunk)

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass

    # ── screen ────────────────────────────────────────────────────────────────
    def row_text(self, y):
        return "".join(self.screen.buffer[y][x].data for x in range(COLS))

    def visible(self):
        return "\n".join(self.row_text(y).rstrip() for y in range(ROWS))

    def find_url(self, url):
        """(row, start_col) of `url` on the screen, or None. Both seeded URLs fit one row at 120 cols."""
        for y in range(ROWS):
            col = self.row_text(y).find(url)
            if col >= 0:
                return y, col
        return None

    def on_detail(self):
        return "Description" in self.visible()

    # ── input ─────────────────────────────────────────────────────────────────
    def key(self, data, settle=1.5):
        os.write(self.master, data)
        self.pump(settle)

    def esc(self):
        self.key(b"\x1b")

    def _sgr(self, col0, row0, press, ctrl):
        # SGR-1006 mouse report, 1-based coords; button 0 = left, +16 = Ctrl held.
        return b"\x1b[<%d;%d;%d%s" % (0 + (16 if ctrl else 0), col0 + 1, row0 + 1,
                                      b"M" if press else b"m")

    def click(self, col0, row0, ctrl=False):
        os.write(self.master, self._sgr(col0, row0, True, ctrl))
        os.write(self.master, self._sgr(col0, row0, False, ctrl))
        self.pump(1.5)

    def click_url(self, url, ctrl=False, offset=4):
        """Click a few chars into `url` — never its first/last cell, so an off-by-one in either
        direction would still land on the link and can't make an assertion accidentally pass."""
        loc = self.find_url(url)
        assert loc, f"{url} not on screen:\n{self.visible()}"
        y, col = loc
        self.click(col + offset, y, ctrl=ctrl)
        return y, col

    def launched(self):
        """The URLs the app has asked a browser to open, in order."""
        if not os.path.exists(self.log):
            return []
        with open(self.log) as f:
            return [line.strip() for line in f if line.strip()]

    def cycle_to(self, url, back=False):
        """Ctrl+→ (or Ctrl+←) through the detail tabs until `url` is on screen."""
        for _ in range(4):
            self.key(b"\x1b[1;5D" if back else b"\x1b[1;5C", settle=1.2)
            if self.find_url(url):
                return True
        return False


def dashboard_checks():
    app = App()
    try:
        app.pump(8.0)
        assert "Task" in app.visible(), "list boot failed:\n" + app.visible()

        app.key(b"\r", settle=3.0)          # Enter → open Task Detail
        assert app.on_detail(), "detail screen did not open:\n" + app.visible()
        assert app.launched() == [], f"nothing should have been launched yet: {app.launched()}"

        # 0) Ordinary clicks do nothing at all. (The *guards* against a clamped click reading as a link
        #    hit are pinned by DetailPaneViewTests, which can construct the wrapped-row geometry that
        #    provokes them; here the point is only that everyday clicks in a pane stay inert.)
        task_y, task_col = app.find_url(TASK_URL)
        app.click(max(task_col - 3, 0), task_y)      # the prose just before the URL
        app.click(COLS - 4, task_y)                  # empty space right of the line, inside the pane
        app.click(2, ROWS - 6)                       # low in the pane, past the body's last line
        assert app.launched() == [], f"a non-link click launched a browser: {app.launched()}"
        assert app.on_detail(), "a non-link click left the detail screen:\n" + app.visible()

        # 1) Ctrl+click the task link → the browser, and *not* in-app.
        app.click_url(TASK_URL, ctrl=True)
        assert app.launched() == [TASK_URL], \
            f"Ctrl+click did not open the task link in the browser: {app.launched()}"
        assert app.on_detail(), "Ctrl+click should not have left the detail screen:\n" + app.visible()

        # 2) Plain click a web link (Comments tab) → the browser.
        assert app.cycle_to(WEB_URL), "web link not found after cycling tabs:\n" + app.visible()
        app.click_url(WEB_URL)
        assert app.launched() == [TASK_URL, WEB_URL], \
            f"plain click on a web link did not open the browser: {app.launched()}"
        assert app.on_detail(), "a web-link click should not have navigated:\n" + app.visible()

        # 3) Plain click the task link → its Task Detail, stacked over this one. Proven by depth: two
        #    Escs are then needed to reach the list, where an inert click would have taken one.
        app.cycle_to(TASK_URL, back=True)
        app.click_url(TASK_URL)
        app.pump(2.5)
        assert app.launched() == [TASK_URL, WEB_URL], \
            f"a plain task-link click must not open the browser: {app.launched()}"
        assert app.on_detail(), "the stacked task detail did not open:\n" + app.visible()
        app.esc()
        assert app.on_detail(), \
            "one Esc should return to the *first* detail (two levels deep):\n" + app.visible()
        app.esc()
        assert not app.on_detail(), "the second Esc should return to the list:\n" + app.visible()
        assert "Task" in app.visible(), "not back at the list:\n" + app.visible()

        # 4) A click is inert while ANY overlay owns input: the comment composer (Ctrl+N), the Dispatch
        #    pane (Ctrl+A) and the description editor (Ctrl+E). Each is re-checked for depth: an
        #    activation would have stacked a detail, costing an extra Esc to get back to the list.
        for label, chord in [("comment composer", b"\x0e"),   # Ctrl+N
                             ("dispatch pane", b"\x01"),      # Ctrl+A
                             ("description editor", b"\x05")]:  # Ctrl+E
            app.key(b"\r", settle=3.0)                        # Enter → detail again (one level)
            assert app.on_detail(), f"detail did not reopen before the {label} leg:\n" + app.visible()
            app.key(chord)
            before = app.launched()
            if app.find_url(TASK_URL):
                app.click_url(TASK_URL)
                assert app.launched() == before, \
                    f"a click under the {label} launched a browser: {app.launched()}"
            app.esc()                                         # close the overlay
            app.esc()                                         # …and the detail → back at the list
            assert not app.on_detail(), \
                f"the click under the {label} navigated (an extra Esc was needed):\n" + app.visible()
        return app.launched()
    finally:
        app.kill()


def single_task_checks():
    """Single-task launch mode has no in-app task→task destination (#374), so a *task* link opens the
    browser there — and, unlike Ctrl+B, leaves the tab running."""
    app = App(E2E_SINGLE_TASK=SINGLE_TASK_ID)
    try:
        app.pump(8.0)
        assert app.on_detail(), "single-task mode did not boot into the detail:\n" + app.visible()
        assert app.launched() == [], f"nothing should have been launched yet: {app.launched()}"

        app.click_url(TASK_URL)
        assert app.launched() == [TASK_URL], \
            f"a task-link click in single-task mode did not open the browser: {app.launched()}"
        assert app.proc.poll() is None, "the single-task tab exited on a link click"
        assert app.on_detail(), "the detail disappeared after a link click:\n" + app.visible()

        app.click_url(TASK_URL, ctrl=True)
        assert app.launched() == [TASK_URL, TASK_URL], \
            f"Ctrl+click in single-task mode did not open the browser: {app.launched()}"
        assert app.proc.poll() is None, "the single-task tab exited on a Ctrl+click"
    finally:
        app.kill()


dashboard_checks()
single_task_checks()
print("ok — Ctrl+click → browser, web click → browser, task click → stacked detail, clicks under the "
      "composer/dispatch/editor overlays → inert, single-task mode → browser (tab stays open)")
