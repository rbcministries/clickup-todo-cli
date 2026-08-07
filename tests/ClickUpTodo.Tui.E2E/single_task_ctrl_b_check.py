#!/usr/bin/env python3
"""Single-task Ctrl+B never exits (#518): boots the harness in single-task launch mode
(E2E_SINGLE_TASK=<id>, the `clickup-todo --task <id>` equivalent) and presses Ctrl+B — the
"open the task in the browser" gesture.

The bug this guards: in the `--task` host the launch-task detail is the stack root, and Ctrl+B
used to call Application.RequestStop() after launching the browser, so opening a task in the
browser *quit the program*. #518 makes Ctrl+B an event the host owns and removes that exit — a
root view never closes on Ctrl+B (the invariant). Because the view now survives, a launch is
reportable, so the host flashes "Opened: …" instead of the old debug-only handling.

Assertions (each fails hard):
  1. The launch-task detail renders (its name is on screen) and the process is alive.
  2. After Ctrl+B the process is STILL alive — it did not exit. This is the core regression.
  3. The browser launcher fired exactly once with a real clickup.com URL (E2E_BROWSER_LOG).
  4. The detail view is still on screen (the task name still shows — the view did not close).
  5. The success flash "Opened:" is on the status line (the now-live footer, #518).

Single-run behavioural check — there is no stock baseline for a keypress-driven launch."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess, tempfile
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None
TASK_ID = "t5"

browser_log = tempfile.NamedTemporaryFile(prefix="e2e-browser-", suffix=".log", delete=False)
browser_log.close()
LOG = browser_log.name

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_SINGLE_TASK=TASK_ID, E2E_BROWSER_LOG=LOG)
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


def screen_text():
    return "\n".join("".join(row).rstrip() for row in
                     ([screen.buffer[y][x].data for x in range(COLS)] for y in range(ROWS)))


try:
    pump(6.0)

    before = screen_text()
    assert "Address display" in before, \
        f"launch-task detail did not render before Ctrl+B; screen was:\n{before}"
    assert proc.poll() is None, "process exited before Ctrl+B was even sent"

    # Ctrl+B (0x02) — open in browser.
    os.write(master, b"\x02")
    pump(1.5)

    # (2) The core regression: the app must survive Ctrl+B in the --task host.
    assert proc.poll() is None, \
        "process EXITED on Ctrl+B in the --task host (#518 invariant violated)"

    # (3) The browser launcher fired exactly once with a real URL.
    with open(LOG) as f:
        launches = [ln.strip() for ln in f if ln.strip()]
    assert len(launches) == 1, f"expected exactly one browser launch, got {launches!r}"
    assert "clickup.com" in launches[0], f"launched URL is not a clickup.com link: {launches[0]!r}"

    # (4) The detail view stayed — the task is still on screen.
    after = screen_text()
    assert "Address display" in after, \
        f"detail view did not survive Ctrl+B (task name gone); screen was:\n{after}"

    # (5) The success flash reports the launch on the now-live footer.
    assert "Opened" in after, \
        f"no 'Opened' flash after Ctrl+B (the live-view report #518 adds); screen was:\n{after}"

    if OUT:
        with open(OUT, "w") as f:
            f.write(f"launches={launches!r}\nalive={proc.poll() is None}\n")
    print(f"SINGLE TASK CTRL+B E2E: PASS (launched={launches[0]!r}, survived)")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
    try:
        os.unlink(LOG)
    except Exception:
        pass
