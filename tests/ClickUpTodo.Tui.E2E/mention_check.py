#!/usr/bin/env python3
"""Asserts @-mention authoring in the Task Detail comment composer (#325, sub-issue K of #313).

Boots the dashboard `TodoApp`, opens Task Detail, and drives the Ctrl+N composer twice:

  A) a PLAIN comment ("plain hello") posted with no @ — must go through the unchanged plain-text
     path (`comment_text` in the request body, no structured `comment` blocks / tag);
  B) a MENTION comment: type "hi ", press @ to open the mention picker, type "Ada", Enter to insert
     the "@Ada Lovelace" token, then Ctrl+Enter to post — must go through the structured path
     (a `{"type":"tag","user":{"id":101}}` block, Ada Lovelace being member 101).

The structured payload is asserted from the harness's E2E_COMMENT_LOG recorder (one request body per
line) — a file fact, not a screen guess. The on-screen half (the "@Ada Lovelace" token rendered in the
composer after the pick, and the posted comment in the pane) is asserted on the pyte screen.

Self-contained (sets its own E2E_COMMENT_LOG). Exits nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

MEMBER_NAME = "Ada Lovelace"   # Members[0] in Program.cs
MEMBER_ID = 101


class App:
    def __init__(self, **extra_env):
        self.log = os.path.join(tempfile.mkdtemp(prefix="mention-"), "comment.log")
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
                   E2E_COMMENT_LOG=self.log, **extra_env)
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

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

    def row_text(self, y):
        return "".join(self.screen.buffer[y][x].data for x in range(COLS))

    def visible(self):
        return "\n".join(self.row_text(y).rstrip() for y in range(ROWS))

    def on_detail(self):
        return "Description" in self.visible()

    def key(self, data, settle=1.5):
        os.write(self.master, data)
        self.pump(settle)

    def type_text(self, text, settle=1.5):
        self.key(text.encode(), settle=settle)

    def esc(self):
        self.key(b"\x1b")

    def posted_bodies(self):
        """The request bodies the app POSTed to /task/{id}/comment, in order."""
        if not os.path.exists(self.log):
            return []
        with open(self.log) as f:
            return [line.strip() for line in f if line.strip()]


def drive_legs(app, host):
    """Drives the two composer legs against an `app` already showing a Task Detail. `host` names the
    host (dashboard / single-task) for assertion messages. Shared so the #325 dashboard wiring and the
    #473 single-task wiring are proven by the *same* gestures against the *same* structured-write path."""
    # ── Leg A: a plain comment posts via the unchanged plain-text path ───────────────────────────
    # Post via the driver-robust Tab→Post→Enter (Ctrl+Enter folds into a bare newline on some drivers,
    # per the composer's own note), so this exercises the real Post-button submit path.
    app.key(b"\x0e", settle=1.5)             # Ctrl+N → composer
    app.type_text("plain hello")
    app.key(b"\t", settle=1.0)               # Tab → Post button
    app.key(b"\r", settle=2.5)               # Enter → post
    bodies = app.posted_bodies()
    assert len(bodies) >= 1, f"[{host}] plain comment was not posted (no body recorded): {bodies}"
    plain = bodies[0]
    assert "comment_text" in plain and "plain hello" in plain, \
        f"[{host}] plain comment did not use the plain-text path: {plain}"
    assert '"type":"tag"' not in plain, f"[{host}] a plain comment must carry no mention tag: {plain}"
    assert app.on_detail(), f"[{host}] posting a plain comment should stay on the detail:\n" + app.visible()

    # ── Leg B: an @-mention comment posts via the structured path ────────────────────────────────
    app.key(b"\x0e", settle=1.5)             # Ctrl+N → composer again
    app.type_text("hi ")
    app.key(b"@", settle=1.5)                # @ opens the mention picker (consumes the literal @)
    app.type_text("Ada", settle=2.5)         # debounced search → the "Ada Lovelace" row
    assert MEMBER_NAME in app.visible(), \
        f"[{host}] mention picker did not surface the member row for 'Ada':\n" + app.visible()
    app.key(b"\r", settle=2.0)               # Enter → pick highlighted row → insert token, close picker
    assert "@" + MEMBER_NAME in app.visible(), \
        f"[{host}] the @Ada Lovelace token was not inserted into the composer:\n" + app.visible()

    app.key(b"\t", settle=1.0)               # Tab → Post button
    app.key(b"\r", settle=2.5)               # Enter → post
    bodies = app.posted_bodies()
    assert len(bodies) >= 2, f"[{host}] mention comment was not posted (only {len(bodies)} bodies): {bodies}"
    mention = bodies[1]
    assert '"type":"tag"' in mention, f"[{host}] mention comment carried no structured tag block: {mention}"
    assert f'"id":{MEMBER_ID}' in mention, \
        f"[{host}] mention tag did not reference member {MEMBER_ID}: {mention}"
    assert '"text":"hi ' in mention, f"[{host}] mention body lost its leading text run: {mention}"
    assert "comment_text" not in mention, \
        f"[{host}] a structured mention post must not also send comment_text: {mention}"

    # The posted mention renders in the pane (its visible @Name literal), stacked over the composer
    # which has now closed.
    assert "@" + MEMBER_NAME in app.visible(), \
        f"[{host}] the posted mention comment did not render in the detail pane:\n" + app.visible()


def run_dashboard():
    """#325: the dashboard host (`TodoApp`) — boot the list, open a task, drive both legs."""
    app = App()
    try:
        # Boot + settle long enough for the assignee pool top-up (GET /team) to land, so the picker has
        # Ada Lovelace to match — the pool is what the #325 wiring projects into WorkspaceMembers.
        app.pump(9.0)
        assert "Task" in app.visible(), "list boot failed:\n" + app.visible()

        app.key(b"\r", settle=3.0)  # Enter → Task Detail
        assert app.on_detail(), "detail screen did not open:\n" + app.visible()

        drive_legs(app, "dashboard")
    finally:
        app.kill()


def run_single_task():
    """#473: the single-task host (`SingleTaskApp`, E2E_SINGLE_TASK) — boots straight into the launch
    task's detail (no list), so the same composer + mention path must light up there too. The pool warms
    from the same GET /team top-up (single-task mode tallies no working set), so the same 9s settle
    applies before the picker can match 'Ada'."""
    app = App(E2E_SINGLE_TASK="t5")
    try:
        app.pump(9.0)
        assert app.on_detail(), "single-task detail did not boot:\n" + app.visible()
        # The dashboard list was never built — this is genuinely the SingleTaskApp host.
        assert "follow up on the" not in app.visible(), \
            "dashboard list rendered in single-task mode:\n" + app.visible()

        drive_legs(app, "single-task")
    finally:
        app.kill()


run_dashboard()
run_single_task()
print(f"ok — dashboard (#325) AND single-task (#473): plain comment → plain-text path (comment_text, "
      f"no tag); @-mention → structured tag block for member {MEMBER_ID} (Ada Lovelace), token rendered "
      f"in composer and pane")
