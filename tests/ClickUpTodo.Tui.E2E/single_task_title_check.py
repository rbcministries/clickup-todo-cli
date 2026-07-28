#!/usr/bin/env python3
"""Single-task terminal title (#418): boots the harness in single-task launch mode
(E2E_SINGLE_TASK=<id>, the `clickup-todo --task <id>` equivalent) and asserts the host
terminal's window/tab title is `{id}: {name}`, truncated to 40 chars — captured by pyte's
screen.title from the OSC title escape Terminal.Gui emits from the top-level Window.Title.
SingleTaskApp titles its window with the task (not the product branding the dashboard
uses), so several `--task` tabs stay distinguishable on the tab strip.

This is the end-to-end proof that the wire-in reaches the terminal; TerminalTitleTests
already pins the formatting/sanitization/truncation logic in CI. Single-run behavioural
check (there is no stock baseline for a title write)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None
TASK_ID = os.environ.get("E2E_SINGLE_TASK", "t5")

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
           E2E_SINGLE_TASK=TASK_ID)
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

try:
    pump(6.0)
    title = screen.title
    # The launch-task detail for the seeded id is "My Account - Address display  (EA-7221)".
    expected_prefix = "t5: My Account - Address display"
    assert title, "no terminal title was set on --task launch (screen.title is empty)"
    assert title.startswith(expected_prefix), \
        f"title did not lead with the id + task name; got: {title!r}"
    # Truncated to 40 chars: the long name is cut, so the trailing "(EA-7221)" must not survive.
    assert len(title) <= 40, f"title exceeds 40 chars: {title!r} (len {len(title)})"
    assert "7221" not in title, f"title was not truncated to 40 chars: {title!r}"

    if OUT:
        with open(OUT, "w") as f:
            f.write(f"title={title!r}\nlen={len(title)}\n")
    print(f"ok (title={title!r})")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
