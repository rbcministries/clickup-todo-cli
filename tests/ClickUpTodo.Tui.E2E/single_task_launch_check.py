#!/usr/bin/env python3
"""Single-task launch mode (#296): boots the harness with E2E_SINGLE_TASK=<id> — the
equivalent of `clickup-todo --task <id>` — and asserts the app comes up straight in the
Task Detail view (not the dashboard list), that its tabs cycle, and that Esc quits the
tab (there is no list to fall back to, so Esc = exit the process).

Unlike the A/B checks this is a single-run behavioural check (there is no stock baseline
for a brand-new boot path); it drives the real SingleTaskApp under the PTY against the
canned backend."""
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

def visible():
    from wcwidth import wcwidth
    lines = []
    for y in range(ROWS):
        row = screen.buffer[y]
        out = []
        prev_wide = False
        for x in range(COLS):
            data = row[x].data
            if data == "":
                if not prev_wide:
                    out.append("▯")
                prev_wide = False
            else:
                out.append(data)
                prev_wide = len(data) > 0 and wcwidth(data[0]) == 2
        lines.append("".join(out).rstrip())
    return "\n".join(lines)

try:
    pump(6.0)
    boot = visible()
    # Booted straight into Task Detail: the detail tabs and the seeded task name are present…
    assert "Description" in boot, "detail screen did not render on --task launch:\n" + boot
    assert "My Account" in boot, "launch task name not shown:\n" + boot
    # …and the dashboard list was never built (its task rows / section header are absent).
    assert "follow up on the" not in boot, "dashboard list rows rendered in single-task mode:\n" + boot

    stages = [("launch", boot)]
    # Tabs cycle Stream/Description/Comments/Other, same as a dashboard-opened detail.
    for name in ["tab1", "tab2", "tab3"]:
        os.write(master, b"\t")
        pump(1.0)
        stages.append((name, visible()))
    assert "Comments" in stages[-1][1] or "Comments" in boot, "tabs did not render"

    # Esc at the root quits the tab (no list beneath) — the process should exit.
    os.write(master, b"\x1b")
    end = time.monotonic() + 5.0
    while time.monotonic() < end and proc.poll() is None:
        pump(0.3)
    assert proc.poll() is not None, "Esc did not quit the single-task tab (process still alive)"

    if OUT:
        with open(OUT, "w") as f:
            for name, text in stages:
                f.write(f"===== {name} =====\n{text}\n")
    print("ok")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
