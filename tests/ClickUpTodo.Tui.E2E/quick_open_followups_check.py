#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the quick-open (Ctrl+O) follow-ups (#353):

  1. Ctrl+O *from an open Task Detail* opens the same entry surface, and resolving a
     target opens its Task Detail stacked over the current one — so ONE Esc returns to
     the previous detail (still on Task Detail) and a SECOND Esc reaches the list. This
     pins the "stacks like Ctrl+U" navigation decision.
  2. A bare hyphenless custom id (sentinel "PROJ123") parses as a plain id, 404s on the
     plain GET, and then resolves via the custom_task_ids fallback and opens Task Detail.

Run with E2E_TASKS=20 so t0..t19 are the working set (t3/t5 cached, PROJ123 uncached)."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200
DLL = sys.argv[1]

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
    out = b""
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
            out += chunk
            answer(chunk)
            stream.feed(chunk)
    return out

def visible():
    return "\n".join(line.rstrip() for line in screen.display).rstrip()

def send(seq, wait=1.2):
    os.write(master, seq)
    return pump(wait)

CTRL_O = b"\x0f"
ESC = b"\x1b"

def open_surface():
    send(CTRL_O, 1.5)
    v = visible()
    assert "Open a task" in v, f"quick-open surface did not render:\n{v}"
    assert "custom id" in v, f"quick-open prompt missing:\n{v}"

def type_and_submit(text, wait=2.5):
    send(text.encode(), 0.8)
    return send(b"\r", wait)

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(1.0)

    # ── Item 1: Ctrl+O from an open Task Detail, stacking over it ─────────────────────
    # Open a first detail from the list (cached t5).
    open_surface()
    type_and_submit("t5")
    assert "Description" in visible(), f"could not open the first detail:\n{visible()}"
    print("SETUP ok — first Task Detail open")

    # Ctrl+O from within the detail opens the same entry surface (item 1 binding).
    open_surface()
    print("DETAIL-CTRL-O ok — entry surface renders over the detail")

    # Resolve a different cached task → a new Task Detail opens over the current one.
    type_and_submit("t3")
    assert "Description" in visible(), f"detail-initiated quick-open did not open a detail:\n{visible()}"
    print("DETAIL-OPEN ok — quick-open from detail opened a Task Detail")

    # Stacking: one Esc returns to the PREVIOUS detail (still on Task Detail, not the list)…
    send(ESC, 1.2)
    v = visible()
    assert "Description" in v, f"first Esc should return to the previous detail, not the list:\n{v}"
    assert "next section" not in v, f"first Esc must not jump straight to the list:\n{v}"
    # …and a second Esc reaches the list.
    send(ESC, 1.2)
    assert "next section" in visible(), f"second Esc should return to the list:\n{visible()}"
    print("STACK ok — detail→detail stacks; Esc walks back one screen at a time")

    # ── Item 3: hyphenless bare custom id resolves via the 404 fallback ───────────────
    open_surface()
    type_and_submit("PROJ123", wait=3.0)
    v = visible()
    assert "Description" in v, f"hyphenless custom id did not resolve via the fallback:\n{v}"
    assert "next section" not in v, f"hyphenless custom id should have opened a detail:\n{v}"
    print("FALLBACK ok — bare hyphenless custom id 404s on the plain GET then resolves via custom_task_ids")

    print("QUICK OPEN FOLLOWUPS E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
