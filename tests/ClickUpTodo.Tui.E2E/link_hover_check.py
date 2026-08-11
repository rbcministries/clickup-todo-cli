#!/usr/bin/env python3
"""Asserts the #408 status-line hover hint for in-pane links. Drives real SGR-1006 *motion* reports
(ESC[<35;x;yM, button code 35 = move with no button) over a seeded link in a Task Detail pane and checks
that the shared status line names the link's resolved target, that a move within the same link repaints
nothing (the zero-redraw claim the design rests on), that moving off clears the hint, and that a hint is
suppressed while an overlay owns input.

The dashboard opens the detail on whatever tab is default and a seeded link can sit below the fold, so the
check *reveals* a known link by cycling tabs (Ctrl+→) and paging down until it is on screen — it does not
assume a landing tab. Mouse motion is injected the same way double_click_check / link_click_check inject
clicks: the ansi driver enables ?1003h + ?1006h at boot, so only the reports themselves are written.

Exits nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

# Seeded links (see the harness Program.cs): a task link in the Description, a web link in a comment.
TASK_URL = "https://app.clickup.com/t/86a1b2c3d"
WEB_URL = "https://github.com/rbcministries/ODBM.Secure/pull/64"
STATUS_ROW = ROWS - 3  # ContextualFooter status line (Y = AnchorEnd(2)); help line is AnchorEnd(1) =
#                        ROWS-2, and the window's bottom border is ROWS-1.


class App:
    def __init__(self, **extra_env):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", **extra_env)
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def _answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")

    def pump(self, seconds):
        """Feed output through pyte for `seconds`, returning the raw bytes drained (for redraw sizing)."""
        end = time.monotonic() + seconds
        drained = bytearray()
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
                drained += chunk
        return bytes(drained)

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass

    # ── screen ──────────────────────────────────────────────────────────────
    def row_text(self, y):
        return "".join(self.screen.buffer[y][x].data for x in range(COLS))

    def visible(self):
        return "\n".join(self.row_text(y).rstrip() for y in range(ROWS))

    def status(self):
        return self.row_text(STATUS_ROW).strip()

    def find_url(self, url):
        for y in range(ROWS):
            col = self.row_text(y).find(url)
            if col >= 0:
                return y, col
        return None

    def on_detail(self):
        return "Description" in self.visible()

    # ── input ───────────────────────────────────────────────────────────────
    def key(self, data, settle=1.2):
        os.write(self.master, data)
        return self.pump(settle)

    def esc(self):
        return self.key(b"\x1b")

    def move(self, col0, row0, settle=1.0):
        """A bare SGR-1006 motion report (no button): button code 35, terminator M."""
        os.write(self.master, b"\x1b[<35;%d;%dM" % (col0 + 1, row0 + 1))
        return self.pump(settle)

    def reveal(self, url, max_tabs=6):
        """Bring `url` onto the screen by cycling detail tabs (Ctrl+→) and paging down within each."""
        for _ in range(max_tabs):
            for _ in range(8):
                if self.find_url(url):
                    return self.find_url(url)
                self.key(b"\x1b[6~", settle=0.5)   # PageDown
            if self.find_url(url):
                return self.find_url(url)
            self.key(b"\x1b[1;5C", settle=0.7)      # Ctrl+Right → next tab (opens at its top)
        return self.find_url(url)


def checks():
    app = App()
    try:
        app.pump(8.0)
        assert "Task" in app.visible(), "list boot failed:\n" + app.visible()

        app.key(b"\r", settle=3.0)                 # Enter → open Task Detail
        assert app.on_detail(), "detail screen did not open:\n" + app.visible()
        assert "Link:" not in app.status(), f"a hint showed before any hover: {app.status()!r}"

        # Reveal the web link (a comment), hover a few chars into it, and assert the status line names it.
        loc = app.reveal(WEB_URL)
        assert loc, "could not bring the web link on screen:\n" + app.visible()
        y, col = loc
        app.move(col + 5, y)
        assert f"Link: {WEB_URL}" in app.status(), \
            f"hovering the web link did not name it on the status line: {app.status()!r}\n" + app.visible()

        # A move that stays within the same link changes NOTHING on screen — the hint only updates on a
        # link-boundary crossing (the dedup that keeps hover off the redraw path). Asserted on the emulated
        # screen: a move within the link leaves every visible cell identical (any bytes a motion report
        # emits are non-visible cursor control, not a repaint).
        before = app.visible()
        app.move(col + 8, y)                        # still inside the same link
        assert app.visible() == before, "a move within the same link changed the screen:\n" + app.visible()
        assert f"Link: {WEB_URL}" in app.status(), f"the hint changed on an in-link move: {app.status()!r}"

        # Moving off the link (onto the prose to its left, before the URL) clears the hint.
        app.move(max(col - 4, 0), y)
        assert "Link:" not in app.status(), f"the hint did not clear when leaving the link: {app.status()!r}"

        # The hint is suppressed while an overlay owns input: Ctrl+N opens the comment composer over the
        # panes. Hovering the link's coordinates must show no hint — whether the composer covers them (the
        # pane gets no motion report) or leaves them exposed (the screen's overlay gate suppresses it). We
        # first hover the link so a hint IS up, then open the composer, then hover the same coordinates:
        # a non-suppressed hover would re-show `Link: …`, so the assertion can't silently no-op.
        app.move(col + 5, y)
        assert f"Link: {WEB_URL}" in app.status(), f"precondition: hint should be up before the overlay: {app.status()!r}"
        app.key(b"\x0e", settle=1.5)               # Ctrl+N → comment composer
        app.move(col + 6, y)                        # hover the link's coordinates under the composer
        assert "Link:" not in app.status(), \
            f"a hover under the comment composer showed a hint: {app.status()!r}\n" + app.visible()
        app.esc()                                   # close the composer

        # A keypress after hovering still drives the app (MousePositionTracking didn't wedge input): the
        # hint is re-cleared and the detail is still up.
        app.move(col + 5, y) if app.find_url(WEB_URL) else None
        app.key(b"\x1b[1;5C", settle=1.0)          # Ctrl+Right → next tab, a normal keypress
        assert app.on_detail(), "the detail screen was lost after hovering:\n" + app.visible()
    finally:
        app.kill()


checks()
print("ok — hover over a link names its target on the status line, an in-link move repaints nothing, "
      "leaving it clears the hint, a hover under the composer is suppressed, keys still work after hover")
