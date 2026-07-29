#!/usr/bin/env python3
"""Threaded comments (#329): asserts that a comment's reply thread renders *nested* in the
Task Detail Comments tab, not as a flat run.

Two legs, each a fresh boot:
  • threaded (E2E_THREADS=1): the fake backend marks comment c2 with reply_count=2 and serves
    two replies from GET /comment/c2/reply, so the real CommentThreadLoader fetches them and the
    formatter indents them under c2. Asserts the indented reply marker (a line matching ^\\s+↳)
    and both reply bodies are visible, while the parent comment sits at the left margin — i.e. the
    thread reads as a thread.
  • control (no E2E_THREADS): the same comment reports no thread, so no reply is fetched. Asserts
    the reply marker and reply bodies are absent — proving the nesting is driven by the loaded
    thread data, not always on.

Drives the real app under a PTY, asserts on the pyte-rendered screen (never raw bytes). Exits
nonzero / prints a traceback on failure. Self-contained (sets its own env)."""
import os, pty, select, struct, sys, termios, fcntl, time, re, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

CTRL_RIGHT = b"\x1b[1;5C"   # Ctrl+→ : next detail tab (#315)
REPLY_ONE = "Reply one"
REPLY_TWO = "Reply two"
PARENT = "Follow-up: verified"   # c2's body — the thread parent
INDENTED_MARKER = re.compile(r"^\s+↳")   # pane content that begins with whitespace then "↳"


def content(line):
    # Strip the leading run of Terminal.Gui box-drawing borders (nested window/frame/pane edges,
    # e.g. "│││") so indentation is measured from the pane's own left edge, not column 0.
    return line.lstrip("│┃|")


def run(threaded):
    screen = pyte.Screen(COLS, ROWS)
    stream = pyte.ByteStream(screen)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
    env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet", E2E_TASKS="5")
    if threaded:
        env["E2E_THREADS"] = "1"
    else:
        env.pop("E2E_THREADS", None)
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

    def lines():
        from wcwidth import wcwidth
        out_lines = []
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
            out_lines.append("".join(out).rstrip())
        return out_lines

    try:
        pump(8.0)
        assert "Task" in "\n".join(lines()), "list boot failed"

        os.write(master, b"\r")            # Enter → open detail (Stream tab default)
        pump(3.0)                          # reply fan-out is async — give the loader time
        assert "Description" in "\n".join(lines()), "detail screen did not open:\n" + "\n".join(lines())

        # Stream(0) → Description(1) → Comments(2): two Ctrl+→ lands on the Comments tab, whose body
        # is just the comments (no Description block), so c2's thread is easy to reach.
        os.write(master, CTRL_RIGHT)
        pump(0.6)
        os.write(master, CTRL_RIGHT)
        pump(0.8)

        # Scroll the (now focused) Comments pane, accumulating every line seen — c1 is long, so the
        # thread may start below the first screen.
        seen = set(lines())
        for _ in range(40):
            os.write(master, b"\x1b[B")    # Down
            pump(0.12)
            seen.update(lines())

        indented = [ln for ln in seen if INDENTED_MARKER.match(content(ln))]
        parent_flush = [ln for ln in seen if PARENT in ln and not content(ln).startswith(" ")]
        joined = "\n".join(sorted(seen))
        return {
            "indented": indented,
            "parent_at_margin": parent_flush,
            "has_reply_one": any(REPLY_ONE in ln for ln in seen),
            "has_reply_two": any(REPLY_TWO in ln for ln in seen),
            "has_marker_anywhere": "↳" in joined,
            "dump": joined,
        }
    finally:
        try:
            os.killpg(os.getpgid(proc.pid), 15)
        except ProcessLookupError:
            pass
        os.close(master)


t = run(threaded=True)
assert t["indented"], "no indented reply line (^\\s+↳) on the Comments tab:\n" + t["dump"]
assert t["has_reply_one"] and t["has_reply_two"], "reply bodies not both visible:\n" + t["dump"]
assert t["parent_at_margin"], "the thread parent (c2) should sit at the left margin, not indented:\n" + t["dump"]

c = run(threaded=False)
assert not c["has_marker_anywhere"], "reply marker (↳) leaked into the flat (no-thread) render:\n" + c["dump"]
assert not (c["has_reply_one"] or c["has_reply_two"]), "reply bodies appeared without a loaded thread:\n" + c["dump"]

print("ok — threaded leg nests %d indented reply line(s) with both reply bodies under c2 (at the margin); "
      "control leg has no marker and no replies" % len(t["indented"]))
