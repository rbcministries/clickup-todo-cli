#!/usr/bin/env python3
"""Asserts keyboard link focus traversal + activation (#319, Task Detail E). Drives Tab/Shift+Tab to move
a focus highlight across the seeded in-pane links and Enter to activate the focused one, checking that
Enter reaches the same destinations a mouse click does (#318):

  - Enter with nothing focused (fresh detail, no Tab yet)  → inert (no nav, no browser);
  - Tab focuses the Description pane's ClickUp **task** link → a visible attribute change on its cells,
      and Enter then opens that task's Task Detail, stacked in-app (proven by the extra Esc it takes to
      reach the list — an inert Enter would take one);
  - Shift+Tab focuses the Comments pane's **web** link and Enter opens the browser.

Browser launches are asserted through the harness's E2E_BROWSER_LOG recorder (one URL per line), so the
"went to the browser" half is a file fact, not a screen guess; navigation is asserted on the pyte screen;
the highlight via pyte cell attributes. COLS=120 so the seeded URLs don't wrap (matches link_check.py).

Keys: Tab = 0x09, Shift+Tab = CSI Z (ESC[Z), Enter = CR, Ctrl+←/→ = ESC[1;5D / ESC[1;5C (tab cycling).

Exits nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

TASK_URL = "https://app.clickup.com/t/86a1b2c3d"
WEB_URL = "https://github.com/rbcministries/ODBM.Secure/pull/64"


class App:
    """The harness app under a PTY, with a pyte screen and its own browser-launch recorder."""

    def __init__(self, **extra_env):
        self.log = os.path.join(tempfile.mkdtemp(prefix="link-tab-"), "browser.log")
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
                   E2E_BROWSER_LOG=self.log, E2E_TASKS="20", **extra_env)
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

    def cell_attr(self, url, offset=4):
        """The (fg, bg, reverse, underscore, bold) of a cell a few chars into `url` — the signature the
        focus highlight must change. Offset avoids the first/last cell so it's squarely inside the link."""
        loc = self.find_url(url)
        assert loc, f"{url} not on screen:\n{self.visible()}"
        y, col = loc
        c = self.screen.buffer[y][col + offset]
        return (c.fg, c.bg, c.reverse, c.underscore, c.bold)

    def on_detail(self):
        return "Description" in self.visible()

    # ── input ─────────────────────────────────────────────────────────────────
    def key(self, data, settle=1.5):
        os.write(self.master, data)
        self.pump(settle)

    def enter(self, settle=3.0):
        self.key(b"\r", settle=settle)

    def esc(self):
        self.key(b"\x1b")

    def tab(self):
        self.key(b"\t")

    def backtab(self):
        self.key(b"\x1b[Z")

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


def checks():
    app = App()
    try:
        app.pump(8.0)
        assert "Task" in app.visible(), "list boot failed:\n" + app.visible()

        app.enter()                                    # open Task Detail (Description front-most)
        assert app.on_detail(), "detail screen did not open:\n" + app.visible()
        assert app.launched() == [], f"nothing should have been launched yet: {app.launched()}"

        # 0) Enter with nothing focused is inert — no link is focused until Tab is pressed, so Enter must
        #    neither navigate nor open a browser. (Depth-checked: an activation would stack a detail,
        #    needing an extra Esc to reach the list.)
        before = app.cell_attr(TASK_URL)
        app.enter()
        assert app.launched() == [], f"an unfocused Enter launched a browser: {app.launched()}"
        assert app.on_detail(), "an unfocused Enter navigated somewhere:\n" + app.visible()

        # 1) Tab focuses the Description pane's task link — a visible attribute change on its cells.
        app.tab()
        after = app.cell_attr(TASK_URL)
        assert after != before, \
            f"Tab did not visibly highlight the focused link (attr unchanged {before}):\n" + app.visible()

        # 2) Enter on the focused task link opens its Task Detail, stacked over this one (not the browser).
        #    Two Escs are then needed to reach the list; an inert Enter would have taken one.
        app.enter()
        app.pump(2.0)
        assert app.launched() == [], f"a focused task-link Enter must not open the browser: {app.launched()}"
        assert app.on_detail(), "the stacked task detail did not open:\n" + app.visible()
        app.esc()
        assert app.on_detail(), "one Esc should return to the first detail (two levels deep):\n" + app.visible()
        app.esc()
        assert not app.on_detail(), "the second Esc should return to the list:\n" + app.visible()
        assert "Task" in app.visible(), "not back at the list:\n" + app.visible()

        # 3) Shift+Tab focuses the Comments pane's web link and Enter opens the browser.
        app.enter()
        assert app.cycle_to(WEB_URL), "web link not found after cycling tabs:\n" + app.visible()
        web_before = app.cell_attr(WEB_URL)
        app.backtab()                                  # from none, Shift+Tab lands on the last link
        assert app.cell_attr(WEB_URL) != web_before, \
            "Shift+Tab did not highlight the web link:\n" + app.visible()
        app.enter()
        assert app.launched() == [WEB_URL], \
            f"Enter on the focused web link did not open the browser: {app.launched()}"
        assert app.on_detail(), "activating a web link should not have navigated:\n" + app.visible()
    finally:
        app.kill()


checks()
print("ok — unfocused Enter inert; Tab highlights + Enter opens the task link's detail (stacked); "
      "Shift+Tab highlights + Enter opens the web link in the browser")
