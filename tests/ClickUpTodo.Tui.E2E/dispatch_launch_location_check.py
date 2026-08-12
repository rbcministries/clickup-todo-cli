#!/usr/bin/env python3
"""Dispatch-pane three-way launch destination (#508, split-pane epic #502 slice F).

Since #508 the per-dispatch launch-location override in the Ctrl+A Dispatch pane is a three-way cycle
(New window → New tab → Split pane), where before #504 it was a two-state check box that could only
express window/tab. A check box can't carry the third value, so the control changed shape to a cycle
*button*: because the pane traps Enter for Submit, the button cycles on **Space** — the same
pass-through key the sibling check boxes toggle on. This is the CI-untestable rendering/keypress path;
the decision logic (DispatchPaneModel.CycleLaunchLocation / LaunchLocationLabel) and the one-off
greying (LaunchLocationApplies) are unit-tested.

One boot: open the first task's detail (Enter), open the Ctrl+A Dispatch pane. The pane opens with
focus on the prompt field, so a single Shift+Tab wraps focus backward onto the last control — the
launch button. Then Space is pressed three times and the rendered "Launch:" label is asserted to walk
New window → New tab → Split pane → back to New window (the wrap), read off the pyte screen each step.
"""
import os, pty, select, struct, sys, termios, fcntl, time
import pyte, subprocess

ROWS, COLS = 32, 100
DLL = sys.argv[1]

ENTER = b"\r"
CTRL_A = b"\x01"
SHIFT_TAB = b"\x1b[Z"
SPACE = b" "

WINDOW = "New window"
TAB = "New tab (where supported)"
SPLIT = "Split pane (where supported)"


class App:
    def __init__(self):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", E2E_TASKS="8")
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")

    def pump(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            r, _, _ = select.select([self.master], [], [], 0.03)
            if r:
                try:
                    chunk = os.read(self.master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                self.answer(chunk)
                self.stream.feed(chunk)

    def send(self, d):
        os.write(self.master, d)

    def visible(self):
        return "\n".join(self.screen.display[y].rstrip() for y in range(ROWS))

    def launch_label(self):
        """The text of the pane's 'Launch:' button, borders/padding stripped. Scanned bottom-up: the
        Dispatch pane is bottom-anchored, so this finds the real button row rather than any 'Launch:'
        substring that might appear in task prose rendered higher up."""
        for y in reversed(range(ROWS)):
            t = self.screen.display[y]
            if "Launch:" in t:
                return t.split("Launch:", 1)[1].strip("│ ][").strip()
        return None

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), 9)
        except Exception:
            pass


def fail(app, msg):
    sys.stderr.write("FAIL: " + msg + "\n\n" + app.visible() + "\n")
    app.kill()
    sys.exit(1)


def open_dispatch_pane():
    app = App()
    app.pump(8.0)
    if "Task 0" not in app.visible():
        fail(app, "list boot failed")
    app.send(ENTER)
    app.pump(3.0)
    if "Dispatch to Claude" in app.visible():
        fail(app, "dispatch pane open before Ctrl+A")
    app.send(CTRL_A)
    app.pump(1.5)
    if "Dispatch to Claude" not in app.visible() or "Launch:" not in app.visible():
        fail(app, "Dispatch pane did not open (no 'Launch:' button)")
    return app


def expect_label(app, want, when):
    got = app.launch_label()
    if got is None:
        fail(app, f"no 'Launch:' button row found {when}")
    # Substring match: the button renders "New window" etc.; tolerate any surrounding button chrome.
    if want not in got:
        fail(app, f"launch label {when}: expected to contain {want!r}, got {got!r}")


def check_cycle():
    app = open_dispatch_pane()
    # Opens on the persisted default (New window). Shift+Tab wraps focus backward from the prompt field
    # onto the last control — the launch button — then Space cycles it.
    expect_label(app, WINDOW, "on open (default)")

    app.send(SHIFT_TAB)
    app.pump(0.8)

    app.send(SPACE)
    app.pump(0.8)
    expect_label(app, TAB, "after 1st Space")

    app.send(SPACE)
    app.pump(0.8)
    expect_label(app, SPLIT, "after 2nd Space")

    app.send(SPACE)
    app.pump(0.8)
    expect_label(app, WINDOW, "after 3rd Space (wrap)")

    app.kill()
    print("ok — Dispatch launch button cycles New window → New tab → Split pane → New window on Space")


if __name__ == "__main__":
    try:
        check_cycle()
    except SystemExit:
        raise
    except Exception as e:  # pragma: no cover - defensive
        sys.stderr.write("FAIL: " + repr(e) + "\n")
        sys.exit(1)
