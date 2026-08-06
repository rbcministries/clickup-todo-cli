#!/usr/bin/env python3
"""Threaded comments (#330): drives posting a reply *into* a comment's thread from Task Detail and
asserts it renders nested under its parent and reached the backend keyed to the picked comment.

Flow (one boot): Enter opens the detail view; two Ctrl+→ land on the Comments tab; Ctrl+T opens the
reply-target picker (a transient overlay listing the task's top-level comments, newest-first, so the
pre-selected row is c3 — authored by "Alex Kim"); Enter picks it, opening the composer in *reply mode*
(title "Reply to Alex Kim"); the user types a reply and Tab→Post→Enter posts it. The fake backend
answers POST /comment/c3/reply with a created-comment shape and records the target id + body to
E2E_REPLY_LOG.

Asserts, on the pyte-rendered screen (never raw bytes):
  • the picker opens on Ctrl+T (its "↑/↓ choose · Enter reply" keys are shown);
  • picking opens the composer in reply mode ("Reply to Alex Kim");
  • after posting, the reply body renders on an *indented* line (nested under its parent — a top-level
    comment sits at the pane's left margin), with a ↳ reply marker present;
  • the composer closed.
And, from the recorder file: the POST reached /comment/c3/reply carrying the typed body.

Exits nonzero / prints a traceback on failure. Self-contained (sets its own env)."""
import os, pty, select, struct, sys, termios, fcntl, time, re, tempfile, subprocess
import pyte

ROWS, COLS = 50, 120
DLL = sys.argv[1]

CTRL_RIGHT = b"\x1b[1;5C"       # Ctrl+→ : next detail tab (#315)
CTRL_T = b"\x14"               # Ctrl+T : open the reply-target picker (#330)
DOWN = b"\x1b[B"
REPLY_BODY = "Reply via Ctrl T looks correct"   # distinctive, short enough not to wrap at COLS=120
INDENTED_MARKER = re.compile(r"^\s+↳")


def content(line):
    # Strip the leading run of Terminal.Gui box-drawing borders so indentation is measured from the
    # pane's own left edge, not column 0 (same helper as thread_check.py).
    return line.lstrip("│┃|")


def main():
    reply_log = os.path.join(tempfile.mkdtemp(), "replies.log")
    screen = pyte.Screen(COLS, ROWS)
    stream = pyte.ByteStream(screen)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
    env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
               E2E_TASKS="5", E2E_REPLY_LOG=reply_log)
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
        pump(3.0)
        assert "Description" in "\n".join(lines()), "detail did not open:\n" + "\n".join(lines())

        # Cycle to the Comments tab (Ctrl+→). The exact hop count doesn't matter for Ctrl+T (it works on
        # any tab) — this just puts the comment thread in view for the post-render check below.
        os.write(master, CTRL_RIGHT); pump(0.6)
        os.write(master, CTRL_RIGHT); pump(0.8)

        # ── Ctrl+T opens the reply-target picker ───────────────────────────────────
        # Retry: on a cold/loaded boot the comments-with-replies load can still be in flight when the
        # first Ctrl+T fires (empty comment set ⇒ "No comments to reply to" no-op), so poll until the
        # picker is up rather than assuming one press lands.
        picker = ""
        for _ in range(10):
            os.write(master, CTRL_T)
            pump(1.0)
            picker = "\n".join(lines())
            if "Enter reply" in picker or "choose" in picker:
                break
        assert "Enter reply" in picker or "choose" in picker, \
            "reply picker did not open on Ctrl+T:\n" + picker

        # ── Enter picks the pre-selected newest comment (c3 / Alex Kim) → composer in reply mode ──
        os.write(master, b"\r")
        pump(1.2)
        composer = "\n".join(lines())
        assert "Reply to Alex Kim" in composer, \
            "composer did not open in reply mode for the picked comment:\n" + composer

        # ── Type the reply, Tab→Post→Enter to post ────────────────────────────────
        os.write(master, REPLY_BODY.encode())
        pump(0.8)
        assert REPLY_BODY in "\n".join(lines()), "typed reply not shown in composer:\n" + "\n".join(lines())
        os.write(master, b"\t")            # Tab: editor → Post button
        pump(0.5)
        os.write(master, b"\r")            # Enter on Post → post
        pump(2.0)

        after = "\n".join(lines())
        assert "Reply to Alex Kim" not in after, "composer stayed open after Post:\n" + after

        # Scroll the Comments pane, accumulating every line, so the nested reply is found wherever it sits.
        seen = set(lines())
        for _ in range(40):
            os.write(master, DOWN); pump(0.1)
            seen.update(lines())

        body_indented = [ln for ln in seen if REPLY_BODY in ln and content(ln).startswith(" ")]
        marker_present = any(INDENTED_MARKER.match(content(ln)) for ln in seen)
        dump = "\n".join(sorted(seen))
        assert body_indented, "posted reply did not render indented (nested) under its parent:\n" + dump
        assert marker_present, "no indented ↳ reply marker after posting:\n" + dump

        # ── The write reached the backend, keyed to the picked comment (c3) ────────
        with open(reply_log, encoding="utf-8") as f:
            log = f.read()
        assert log.startswith("c3\t") or "\nc3\t" in ("\n" + log), \
            "POST /comment/c3/reply not recorded (target comment id wrong):\n" + log
        assert REPLY_BODY in log, "recorded reply body did not carry the typed text:\n" + log

        print("ok — Ctrl+T → picker → reply to c3 (Alex Kim); reply renders nested (↳, indented) and "
              "POST /comment/c3/reply carried the body")
    finally:
        try:
            os.killpg(os.getpgid(proc.pid), 15)
        except ProcessLookupError:
            pass
        os.close(master)


main()
