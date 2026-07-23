#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises quick-open (Ctrl+O, #303):
Ctrl+O opens the entry surface → a cached task id opens Task Detail → a task URL
opens Task Detail (URL parsing) → an uncached custom id resolves via the
custom_task_ids API and opens Task Detail → a not-found id flashes an error and
stays on the list → an invalid (non-ClickUp) URL flashes an error and stays on the
list. Asserts each step on the pyte screen. Run with E2E_TASKS=20 so the working set
is t0..t19 and the uncached ids below are genuinely uncached."""
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

    # 1. Ctrl+O opens the entry surface.
    open_surface()
    print("OPEN ok — quick-open entry surface renders")

    # 2. A cached task id (t5 is in the t0..t19 working set) opens its Task Detail.
    type_and_submit("t5")
    v = visible()
    assert "Description" in v, f"cached-id open did not reach Task Detail:\n{v}"
    assert "EA-7221" in v, f"detail task name not shown for cached id:\n{v}"
    print("BY-ID ok — cached task id opens Task Detail")
    send(ESC, 1.2)  # back to the list
    assert "next section" in visible(), f"Esc did not return to the list:\n{visible()}"

    # 3. A task URL opens Task Detail (URL path parsing).
    open_surface()
    type_and_submit("https://app.clickup.com/t/t3")
    assert "Description" in visible(), f"URL open did not reach Task Detail:\n{visible()}"
    print("BY-URL ok — task URL opens Task Detail")
    send(ESC, 1.2)

    # 4. An uncached custom id (hyphenated → CustomId) resolves via the custom_task_ids API and opens.
    open_surface()
    type_and_submit("DEV-42")
    assert "Description" in visible(), f"uncached custom-id open did not reach Task Detail:\n{visible()}"
    print("BY-CUSTOM-ID ok — uncached custom id resolves via the API and opens Task Detail")
    send(ESC, 1.2)

    # 5. A not-found id flashes an error and stays on the list (no navigation).
    open_surface()
    type_and_submit("tmissing")
    v = visible()
    assert "Description" not in v, f"a not-found id must not open Task Detail:\n{v}"
    assert "next section" in v, f"a not-found id must stay on the list:\n{v}"
    assert "could not" in v.lower() or "couldn" in v.lower(), f"no error flashed for not-found id:\n{v}"
    print("NOT-FOUND ok — error flashed, stayed on the list")

    # 6. An invalid (non-ClickUp) URL flashes an error and stays on the list.
    open_surface()
    type_and_submit("https://example.com/t/nope")
    v = visible()
    assert "Description" not in v, f"an invalid input must not open Task Detail:\n{v}"
    assert "next section" in v, f"an invalid input must stay on the list:\n{v}"
    assert "couldn" in v.lower(), f"no error flashed for invalid input:\n{v}"
    print("INVALID ok — error flashed, stayed on the list")

    print("QUICK OPEN E2E: PASS")
finally:
    try: os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception: pass
