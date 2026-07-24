#!/usr/bin/env python3
"""Exit confirmation on quit from a root view (#299): drives the real app under a PTY and
asserts the guard on both roots — the dashboard's main list (TodoApp) and single-task mode's
launch task (SingleTaskApp) — since "the same behaviour in both launch modes" is the
acceptance criterion that no unit test can reach.

Per root: Esc at the root shows the confirmation and the process stays alive → declining
(N on the dashboard, Esc in single-task mode, so both answer keys are covered) tears the
modal down and restores the root view → Esc again re-asks → Y exits the process.

Self-contained (drives both legs, sets its own env). Like single_task_launch_check.py this is
a single-run behavioural check, not an A/B: there is no stock baseline for a new screen."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None

ESC = b"\x1b"
PROMPT = "Are you sure you want to exit?"
FOOTER = "yes, exit"


class App:
    """One app run under a PTY, with the screen the user would see."""

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
        # Terminal.Gui's ANSI driver asks for size/cursor position; an unanswered query = blank app.
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
                self.answer(chunk)
                self.stream.feed(chunk)

    def visible(self):
        return "\n".join(line.rstrip() for line in self.screen.display).rstrip()

    def send(self, seq, wait=1.5):
        os.write(self.master, seq)
        self.pump(wait)
        return self.visible()

    def alive(self):
        return self.proc.poll() is None

    def wait_for_exit(self, seconds=6.0):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.pump(0.3)
        return not self.alive()

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        except Exception:
            pass


def check_root(app, label, root_marker, decline_key, decline_name, stages):
    """Esc → prompt → decline → root back → Esc → Y → the process exits."""
    boot = app.visible()
    stages.append((f"{label}-boot", boot))
    assert root_marker in boot, f"{label}: root view did not render:\n{boot}"
    assert PROMPT not in boot, f"{label}: the exit prompt is showing before any Esc:\n{boot}"

    # 1. Esc at the root asks instead of quitting.
    v = app.send(ESC, 2.0)
    stages.append((f"{label}-asked", v))
    assert PROMPT in v, f"{label}: Esc at the root did not show the confirmation:\n{v}"
    assert FOOTER in v, f"{label}: the confirmation's footer hints are missing:\n{v}"
    assert app.alive(), f"{label}: Esc at the root quit without confirming"

    # 2. Declining restores the root view untouched.
    v = app.send(decline_key, 2.0)
    stages.append((f"{label}-declined", v))
    assert PROMPT not in v, f"{label}: {decline_name} did not dismiss the confirmation:\n{v}"
    assert root_marker in v, f"{label}: {decline_name} did not restore the root view:\n{v}"
    assert app.alive(), f"{label}: {decline_name} quit the app instead of cancelling"

    # 3. It asks again on the next Esc (the guard isn't one-shot)…
    v = app.send(ESC, 2.0)
    stages.append((f"{label}-asked-again", v))
    assert PROMPT in v, f"{label}: a second Esc at the root did not re-ask:\n{v}"
    assert app.alive(), f"{label}: the second Esc quit without confirming"

    # 4. …and Y exits.
    os.write(app.master, b"Y")
    assert app.wait_for_exit(), f"{label}: Y at the confirmation did not exit:\n{app.visible()}"
    print(f"{label} ok — Esc asks, {decline_name} cancels, Y exits")


stages = []
dash = None
single = None
try:
    # ── The dashboard root: the main list. Decline with N. ────────────────────────────────
    dash = App(E2E_TASKS="20", E2E_REFRESH="600")
    dash.pump(6.0)
    check_root(dash, "dashboard", "next section", b"N", "N", stages)

    # ── The single-task root: the launch task's detail (#296). Decline with Esc. ───────────
    single = App(E2E_SINGLE_TASK="t5", E2E_TASKS="20", E2E_REFRESH="600")
    single.pump(6.0)
    check_root(single, "single-task", "Description", ESC, "Esc", stages)

    if OUT:
        with open(OUT, "w") as f:
            for name, text in stages:
                f.write(f"===== {name} =====\n{text}\n")
    print("ok — exit confirmation guards both roots (#299)")
finally:
    for app in (dash, single):
        if app is not None:
            app.kill()
