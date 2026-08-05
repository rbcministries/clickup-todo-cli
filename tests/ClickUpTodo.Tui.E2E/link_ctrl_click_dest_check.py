#!/usr/bin/env python3
"""Asserts the configurable Ctrl+Click destination for task links (#320, Task Detail F). Drives real
SGR-1006 mouse clicks at the seeded ClickUp **task** link and checks that the gesture goes where the
persisted setting says — with Ctrl+Shift inverting it — while a **web** link always opens the browser.

Two legs, one per setting:

  NewTerminalTab default (E2E_LINK_CTRL_DEST=tab):
    - Ctrl+click the task link        → a new terminal tab (`clickup-todo --task <id>`), not the browser;
    - Ctrl+Shift+click the task link  → the browser (inverted);

  Browser default (unset — matches #318):
    - Ctrl+click the task link        → the browser;
    - Ctrl+Shift+click the task link  → a new terminal tab (inverted);
    - Ctrl+Shift+click a web link     → the browser (the setting governs task links only).

Browser launches are asserted through E2E_BROWSER_LOG (one URL per line); new-tab launches through
E2E_TAB_LOG (one `clickup-todo --task <id>` command per line) — both file facts, not screen guesses.
Every leg also asserts the app never navigated (still on the detail screen).

Mouse is injected as SGR-1006 (ESC[<b;x;yM/m); Ctrl is the +16 modifier bit on the button code and
Shift the +4 bit, so Ctrl+Shift is +20. Exits nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

TASK_URL = "https://app.clickup.com/t/86a1b2c3d"
TASK_ID = "86a1b2c3d"
WEB_URL = "https://github.com/rbcministries/ODBM.Secure/pull/64"


class App:
    """The harness app under a PTY, with a pyte screen plus browser- and tab-launch recorders."""

    def __init__(self, **extra_env):
        d = tempfile.mkdtemp(prefix="link-dest-")
        self.browser_log = os.path.join(d, "browser.log")
        self.tab_log = os.path.join(d, "tab.log")
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
                   E2E_BROWSER_LOG=self.browser_log, E2E_TAB_LOG=self.tab_log, **extra_env)
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

    def _sgr(self, col0, row0, press, mod):
        return b"\x1b[<%d;%d;%d%s" % (0 + mod, col0 + 1, row0 + 1, b"M" if press else b"m")

    def click(self, col0, row0, ctrl=False, shift=False):
        mod = (16 if ctrl else 0) + (4 if shift else 0)
        os.write(self.master, self._sgr(col0, row0, True, mod))
        os.write(self.master, self._sgr(col0, row0, False, mod))
        self.pump(1.8)

    def click_url(self, url, ctrl=False, shift=False, offset=4):
        loc = self.find_url(url)
        assert loc, f"{url} not on screen:\n{self.visible()}"
        y, col = loc
        self.click(col + offset, y, ctrl=ctrl, shift=shift)

    def _read(self, path):
        if not os.path.exists(path):
            return []
        with open(path) as f:
            return [line.strip() for line in f if line.strip()]

    def browsed(self):
        return self._read(self.browser_log)

    def tabbed(self):
        return self._read(self.tab_log)


def leg(label, expect_ctrl_is_tab, extra_env):
    """Drive Ctrl and Ctrl+Shift clicks on the task link under one setting and assert the destinations.
    `expect_ctrl_is_tab` is what a *plain* Ctrl+click should do (True → new tab, False → browser); the
    Ctrl+Shift click must always do the other."""
    app = App(**extra_env)
    try:
        app.pump(8.0)
        assert "Task" in app.visible(), f"[{label}] list boot failed:\n" + app.visible()
        app.key(b"\r", settle=3.0)                       # Enter → Task Detail (opens on the Stream tab,
        assert app.on_detail(), f"[{label}] detail did not open:\n" + app.visible()  # which shows the desc)
        assert app.browsed() == [] and app.tabbed() == [], \
            f"[{label}] nothing should have launched yet: browser={app.browsed()} tab={app.tabbed()}"

        # Ctrl+click the task link → the configured destination.
        app.click_url(TASK_URL, ctrl=True)
        if expect_ctrl_is_tab:
            assert len(app.tabbed()) == 1 and TASK_ID in app.tabbed()[0] and "--task" in app.tabbed()[0], \
                f"[{label}] Ctrl+click did not open a new terminal tab: {app.tabbed()}"
            assert app.browsed() == [], f"[{label}] Ctrl+click also opened the browser: {app.browsed()}"
        else:
            assert app.browsed() == [TASK_URL], \
                f"[{label}] Ctrl+click did not open the browser: {app.browsed()}"
            assert app.tabbed() == [], f"[{label}] Ctrl+click also opened a tab: {app.tabbed()}"
        assert app.on_detail(), f"[{label}] Ctrl+click should not have navigated:\n" + app.visible()

        # Ctrl+Shift+click the task link → the OTHER destination (the inversion).
        app.click_url(TASK_URL, ctrl=True, shift=True)
        if expect_ctrl_is_tab:
            # default was tab → shift inverts to browser
            assert app.browsed() == [TASK_URL], \
                f"[{label}] Ctrl+Shift+click did not invert to the browser: {app.browsed()}"
            assert len(app.tabbed()) == 1, f"[{label}] Ctrl+Shift+click opened another tab: {app.tabbed()}"
        else:
            # default was browser → shift inverts to a tab
            assert len(app.tabbed()) == 1 and TASK_ID in app.tabbed()[0], \
                f"[{label}] Ctrl+Shift+click did not invert to a new tab: {app.tabbed()}"
            assert app.browsed() == [TASK_URL], \
                f"[{label}] Ctrl+Shift+click changed the browser log: {app.browsed()}"
        assert app.on_detail(), f"[{label}] Ctrl+Shift+click should not have navigated:\n" + app.visible()
        return app
    finally:
        app.kill()


def web_link_ignores_the_setting():
    """A web link always opens the browser — the task-link Ctrl+Click setting must not touch it. Uses the
    tab-default leg (where a task link's Ctrl+click goes to a tab) to prove the web link still doesn't."""
    app = App(E2E_LINK_CTRL_DEST="tab")
    try:
        app.pump(8.0)
        app.key(b"\r", settle=3.0)
        assert app.on_detail(), "detail did not open:\n" + app.visible()
        # The web link lives on the Comments tab; cycle to it (Ctrl+→).
        for _ in range(4):
            if app.find_url(WEB_URL):
                break
            app.key(b"\x1b[1;5C", settle=1.2)
        assert app.find_url(WEB_URL), "web link not found after cycling tabs:\n" + app.visible()

        app.click_url(WEB_URL, ctrl=True, shift=True)
        assert app.browsed() == [WEB_URL], \
            f"a Ctrl+Shift+click on a web link must open the browser: {app.browsed()}"
        assert app.tabbed() == [], f"a web link must never open a terminal tab: {app.tabbed()}"
    finally:
        app.kill()


leg("NewTerminalTab default", expect_ctrl_is_tab=True, extra_env={"E2E_LINK_CTRL_DEST": "tab"})
leg("Browser default", expect_ctrl_is_tab=False, extra_env={})
web_link_ignores_the_setting()
print("ok — Ctrl+click follows the task-link destination (browser ↔ new tab) and Ctrl+Shift inverts it; "
      "a web link always opens the browser")
