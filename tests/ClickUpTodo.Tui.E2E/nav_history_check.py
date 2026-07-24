#!/usr/bin/env python3
"""Browser-style navigation history in single-task mode (#298): boots the harness with
E2E_SINGLE_TASK=<id> (the `clickup-todo --task <id>` equivalent), then exercises the
NavigationHistory wiring in SingleTaskApp:

  1. F1 opens the Help overlay over the launch-task detail (a forward navigation).
  2. Alt+Left goes *back* to the detail (overlay torn down, detail front-most again).
  3. Alt+Right goes *forward*, re-opening Help (the model retained the forward entry).
  4. Esc inside Help is a back navigation too (its own Close routes through GoBack), so it
     returns to the detail rather than quitting.
  5. Esc at the launch-task root hands off to the exit seam (#299) — today it quits the tab,
     since single-task mode has no list beneath.

Single-run behavioural check (no stock baseline for this wiring); drives the real
SingleTaskApp under a PTY against the canned backend."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None
TASK_ID = os.environ.get("E2E_SINGLE_TASK", "t5")

# xterm modifier-encoded arrows: CSI 1 ; 3 <dir> is Alt+<arrow> (modifier 3 = Alt), which the
# Terminal.Gui ansi driver decodes to Alt+CursorLeft / Alt+CursorRight.
ALT_LEFT = b"\x1b[1;3D"
ALT_RIGHT = b"\x1b[1;3C"
F1 = b"\x1bOP"
ESC = b"\x1b"

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

HELP_MARK = "Keyboard shortcuts"

stages = []
try:
    pump(6.0)
    boot = visible()
    stages.append(("boot", boot))
    assert "Description" in boot, "detail did not render on --task launch:\n" + boot
    assert HELP_MARK not in boot, "Help overlay showing before F1:\n" + boot

    # 1. F1 → Help overlay over the detail.
    os.write(master, F1); pump(1.5)
    help_open = visible(); stages.append(("f1_help", help_open))
    assert HELP_MARK in help_open, "F1 did not open Help:\n" + help_open

    # 2. Alt+Left → back to the detail (Help torn down).
    os.write(master, ALT_LEFT); pump(1.5)
    back = visible(); stages.append(("alt_left_back", back))
    assert "Description" in back, "Alt+Left did not restore the detail:\n" + back
    assert HELP_MARK not in back, "Help overlay still showing after Alt+Left back:\n" + back

    # 3. Alt+Right → forward, re-opening Help.
    os.write(master, ALT_RIGHT); pump(1.5)
    fwd = visible(); stages.append(("alt_right_forward", fwd))
    assert HELP_MARK in fwd, "Alt+Right did not re-open Help (forward):\n" + fwd

    # 4. Esc inside Help is a back navigation (not a quit): returns to the detail.
    os.write(master, ESC); pump(1.5)
    esc_back = visible(); stages.append(("esc_back", esc_back))
    assert proc.poll() is None, "Esc inside Help quit the app instead of going back"
    assert "Description" in esc_back, "Esc in Help did not return to the detail:\n" + esc_back
    assert HELP_MARK not in esc_back, "Help still showing after Esc-back:\n" + esc_back

    # 5. Esc at the launch-task root hands off to the exit seam → quits the tab.
    os.write(master, ESC)
    end = time.monotonic() + 5.0
    while time.monotonic() < end and proc.poll() is None:
        pump(0.3)
    assert proc.poll() is not None, "Esc at the root did not quit the single-task tab (still alive)"

    if OUT:
        with open(OUT, "w") as f:
            for name, text in stages:
                f.write(f"===== {name} =====\n{text}\n")
    print("ok — F1→Help, Alt+Left back, Alt+Right forward, Esc back, Esc-at-root quits")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
