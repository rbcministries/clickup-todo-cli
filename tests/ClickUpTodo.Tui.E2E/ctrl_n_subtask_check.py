#!/usr/bin/env python3
"""Contextual chords G (#544), main-list task path: Ctrl+N now offers Add task vs Add subtask.

Boots the TUI under a PTY against the fake backend and drives the two legs the slice introduces:

  Leg 1 (native modal ON, CLICKUP_TODO_NATIVE_MODAL=1, create POST captured to a file):
    • Ctrl+N on a highlighted own task opens the native ChoiceDialog — its title carries
      ChoiceDialog.TitleMarker "[native choice]", and it lists "Add task" / "Add subtask" plus a
      message naming the sub-add parent ("subtask of …"). This is what makes the native path
      provable (a silently no-op'd flag would open New Task directly and never render the dialog).
    • Picking "Add subtask" (Tab to it → activate) opens the compose screen titled "New subtask";
      typing a name + Save round-trips through the create facade and returns to the list.
    • The captured POST /list/{id}/task body carries a top-level "parent":"t…" — proving the sub-add
      wired the highlighted task as the subtask parent (the merged #603 facade), end-to-end.

  Leg 2 (native modal OFF, the default):
    • Ctrl+N opens New Task directly with NO "[native choice]" dialog — the flag-off default is
      unchanged from pre-#544, so the choice rides the native-modal flag like the other F/G modals.

Run:
  DLL=tests/ClickUpTodo.Tui.E2E/bin/Release/net10.0/ClickUpTodo.Tui.E2E.dll
  timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/ctrl_n_subtask_check.py $DLL
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile, re
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

CTRL_N = b"\x0e"
TAB = b"\t"
SPACE = b" "
ENTER = b"\r"
ESC = b"\x1b"

NATIVE_CHOICE_MARKER = "[native choice]"  # ChoiceDialog.TitleMarker — only in the native choice dialog


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

    def answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")
        if b"\x1b[0c" in data or b"\x1b[c" in data:
            os.write(self.master, b"\x1b[?62;22c")

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
                self.answer(chunk)
                self.stream.feed(chunk)

    def visible(self):
        return "\n".join(line.rstrip() for line in self.screen.display).rstrip()

    def boot(self, seconds=30):
        t0 = time.monotonic()
        while time.monotonic() - t0 < seconds:
            self.pump(0.5)
            if "Task 1" in self.visible():
                return True
        return False

    def send(self, seq, wait=1.2):
        os.write(self.master, seq)
        self.pump(wait)
        return self.visible()

    def alive(self):
        return self.proc.poll() is None

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def leg_native(capture_path):
    app = App(E2E_TASKS="200", E2E_REFRESH="600",
              CLICKUP_TODO_NATIVE_MODAL="1", E2E_CAPTURE_FILE=capture_path)
    try:
        assert app.boot(), "native leg: app did not boot:\n" + app.visible()[:1500]

        # Ctrl+N on the boot cursor (a highlighted own task) opens the native choice dialog.
        v = app.send(CTRL_N, 2.0)
        assert NATIVE_CHOICE_MARKER in v, \
            f"native leg: Ctrl+N did not open the native choice dialog ({NATIVE_CHOICE_MARKER!r} missing):\n{v}"
        assert "Add task" in v and "Add subtask" in v, \
            f"native leg: the Add task / Add subtask choices are missing:\n{v}"
        assert "subtask of" in v, f"native leg: the dialog message did not name the sub-add parent:\n{v}"
        print("CHOICE ok — Ctrl+N opened the native [native choice] dialog with Add task / Add subtask")

        # Move focus to the second button (Add subtask) and activate it.
        app.send(TAB, 0.5)
        v = app.send(SPACE, 2.0)
        assert NATIVE_CHOICE_MARKER not in v, f"native leg: the choice dialog did not close on Add subtask:\n{v}"
        assert "New subtask" in v, f"native leg: Add subtask did not open the New subtask screen:\n{v}"
        assert "Name" in v, f"native leg: the compose screen fields did not render:\n{v}"
        print("SUBTASK ok — Add subtask opened the 'New subtask' compose screen")

        # Type a name, then Tab to the Save button and activate it with Space (Space activates the
        # focused button; the ► ◄ decoration marks IsDefault, not focus). Tab order is
        # Name → Description → Assignees → List → Priority → Due → Save (mirrors new_task_check).
        app.send(b"Subtask from Ctrl+N", 0.8)
        for _ in range(6):
            app.send(TAB, 0.4)
        v = app.send(SPACE, 3.0)
        assert "New subtask" not in v, f"native leg: Save did not close the New subtask screen:\n{v}"
        assert "Task 1" in v, f"native leg: did not return to the task list after Save:\n{v}"
        print("SAVE ok — the subtask create round-tripped and returned to the list")

        # The captured create POST body must carry a top-level parent id (the sub-add wiring, #603 facade).
        assert app.alive(), "native leg: process died before the capture could be read"
        deadline = time.monotonic() + 3.0
        body = ""
        while time.monotonic() < deadline:
            try:
                with open(capture_path, "r") as fh:
                    body = fh.read()
            except FileNotFoundError:
                body = ""
            if '"parent"' in body:
                break
            time.sleep(0.1)
        m = re.search(r'"parent"\s*:\s*"(t\d+)"', body)
        assert m, f"native leg: the captured create POST did not carry a top-level parent id:\n{body!r}"
        print(f"PARENT ok — the create POST carried \"parent\":\"{m.group(1)}\" (subtask of the highlighted task)")
        return True
    finally:
        app.kill()


def leg_flag_off():
    app = App(E2E_TASKS="200", E2E_REFRESH="600")
    try:
        assert app.boot(), "flag-off leg: app did not boot:\n" + app.visible()[:1500]
        v = app.send(CTRL_N, 2.0)
        assert NATIVE_CHOICE_MARKER not in v, \
            f"flag-off leg: the native choice dialog appeared with the flag off (should be gated):\n{v}"
        assert "New task" in v and "Name" in v, \
            f"flag-off leg: Ctrl+N did not open New Task directly with the flag off:\n{v}"
        print("FLAG-OFF ok — Ctrl+N opens New Task directly, no [native choice] dialog (default unchanged)")
        return True
    finally:
        app.kill()


ok = True
try:
    print("── Leg 1: native choice ON (CLICKUP_TODO_NATIVE_MODAL=1) ──")
    with tempfile.NamedTemporaryFile(suffix=".json", delete=False) as tf:
        capture_path = tf.name
    try:
        leg_native(capture_path)
    finally:
        try: os.unlink(capture_path)
        except Exception: pass

    print("── Leg 2: native choice OFF (default) ──")
    leg_flag_off()

    print("\nCTRL+N SUBTASK E2E: PASS")
except AssertionError as e:
    ok = False
    print("FAIL:", e)

raise SystemExit(0 if ok else 1)
