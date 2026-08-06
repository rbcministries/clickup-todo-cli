#!/usr/bin/env python3
"""Asserts OSC-8 terminal hyperlinks for markdown [text](url) links (#430, follow-up to #380).

#380 emits OSC-8 for *bare* links (osc8_link_check.py), where a link's on-screen text is its URL.
This covers the case #380 deferred: a **markdown** `[text](url)` link, whose visible text is prose
and whose true target lives in the markup. With `E2E_MD_LINK=1` the fake backend appends

    See [the runbook](https://example.com/runbook-42) for steps

to the Description. This check asserts, on the **raw byte stream** (OSC-8 is a hyperlink escape a
VT emulator consumes rather than renders, so it never reaches the pyte screen — the harness's
documented raw-bytes-for-escape exception), that:

  1. an OSC-8 open for the RESOLVED target (https://example.com/runbook-42) is present and bounded
     by a close — proving the target is the markdown destination, not the visible prose (the visible
     text "the runbook" is not a URL, so this open can only come from markdown resolution); and
  2. the visible text "the runbook" appears between that open and its close — the hyperlink wraps
     exactly the visible text the reader sees.

Fixed wide COLS=120 (as osc8_link_check.py) so the markup doesn't wrap — a markdown link split
across two rendered rows is out of scope (#443). Exits nonzero / prints a traceback on failure."""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

MD_TARGET = "https://example.com/runbook-42"
MD_VISIBLE = "the runbook"

ESC = b"\x1b"
ST = b"\x1b\\"


def osc8_start(url):
    """The exact OSC-8 open sequence that targets `url`."""
    return ESC + b"]8;;" + url.encode() + ST


OSC8_END = ESC + b"]8;;" + ST

screen = pyte.Screen(COLS, ROWS)
stream = pyte.ByteStream(screen)
master, slave = pty.openpty()
fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", E2E_MD_LINK="1")
proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                        env=env, close_fds=True, preexec_fn=os.setsid)
os.close(slave)

raw = bytearray()  # every byte the app emits, for the escape assertions


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
            raw.extend(chunk)
            answer(chunk)
            stream.feed(chunk)


def visible():
    return "\n".join(
        "".join(screen.buffer[y][x].data for x in range(COLS)).rstrip() for y in range(ROWS))


try:
    pump(8.0)
    assert "Task" in visible(), "list boot failed"

    os.write(master, b"\r")          # Enter → open detail (opens on the Description tab)
    pump(3.0)
    assert "Description" in visible(), "detail screen did not open:\n" + visible()

    # ── The markdown link's visible text is wrapped in a bounded OSC-8 hyperlink to its RESOLVED url ──
    start = osc8_start(MD_TARGET)
    open_at = raw.find(start)
    assert open_at >= 0, (
        "no OSC-8 open sequence targeting the markdown link's RESOLVED url "
        f"({MD_TARGET}) — the target was not resolved from the [text](url) markup")

    close_at = raw.find(OSC8_END, open_at + len(start))
    assert close_at >= 0, "OSC-8 open for the markdown link is not followed by a close — the run is unbounded"

    between = raw[open_at + len(start):close_at]
    assert MD_VISIBLE.encode() in between, (
        f"the visible text {MD_VISIBLE!r} is not inside the OSC-8 run — the hyperlink does not wrap "
        "the markdown link's visible text")

    print(f"ok — markdown link visible text {MD_VISIBLE!r} wrapped in a bounded OSC-8 hyperlink to its "
          f"resolved target {MD_TARGET}")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
