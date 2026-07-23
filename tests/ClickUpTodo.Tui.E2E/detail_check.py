#!/usr/bin/env python3
"""Opens the task detail screen (Enter), cycles tabs (Ctrl+→, #315) the way a user
browsing Description/Comments would, and dumps the pyte-rendered screen text after each stage.
Run once normally and once with CLICKUP_TODO_NO_DIFF=1, then diff the dumps: the
detail screen mixes emoji/em-dash/curly-quote graphemes with auto-hyperlinked URLs,
which is exactly where sparse (diffed) flushing can drift the cursor."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]
OUT = sys.argv[2]

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet")
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
    # Like screen.display, but tolerant of orphaned wide-char stub cells (data == ""
    # not preceded by a wide glyph) — pyte's own renderer crashes on those. An orphan
    # means one half of a wide glyph was overwritten: render it as ▯ so it shows in diffs.
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
    pump(8.0)
    assert "Task" in visible(), "list boot failed"

    os.write(master, b"\r")          # Enter → open detail (async fetch + screen swap)
    pump(3.0)
    assert "Description" in visible(), "detail screen did not open:\n" + visible()

    stages = []
    stages.append(("detail:description", visible()))
    for i, name in enumerate(["comments", "other", "description2", "comments2"]):
        os.write(master, b"\x1b[1;5C")  # Ctrl+→ → next tab (#315; was Tab)
        pump(1.2)
        stages.append((f"detail:{name}", visible()))
    os.write(master, b"\x1b[1;5D")      # Ctrl+← → previous tab (#315; exercises the backward chord)
    pump(1.2)
    stages.append(("detail:back", visible()))

    with open(OUT, "w") as f:
        for name, text in stages:
            f.write(f"===== {name} =====\n{text}\n")
    print("ok")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
