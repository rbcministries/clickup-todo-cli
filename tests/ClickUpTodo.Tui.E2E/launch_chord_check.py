#!/usr/bin/env python3
"""Configurable launch chords (#506): boots the real TodoApp with a launch-chord override in config
(the LaunchChordScenario reads E2E_LAUNCH_NEWTAB / E2E_LAUNCH_SPLIT) and asserts, on the pyte screen,
that the MAIN-LIST FOOTER advertises the rebound Ctrl+Enter/Ctrl+Alt+Enter gestures — the render-side
proof that HelpItemSets.WithConfiguredLaunchChords picks the override up via the same seam the list
dispatcher resolves through. A control leg (no override) asserts the shipped default glyphs, so the
change is provably driven by the config, not always-on.

Two legs, each its own boot:
  A — override: new tab → Alt+Enter, split pane → Ctrl+Shift+Enter; footer shows those, not the defaults.
  B — control:  no override; footer shows the shipped Ctrl+↩ / Ctrl+Alt+↩ and never the override tokens.
"""
import os, pty, select, struct, sys, termios, fcntl, time, signal, subprocess
import pyte

ROWS, COLS = 50, 200  # wide so the full main-list footer fits without truncation
DLL = sys.argv[1]


def run_leg(extra_env):
    screen = pyte.Screen(COLS, ROWS)
    stream = pyte.ByteStream(screen)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
    env = dict(os.environ, TERM="xterm-256color", DOTNET_ROOT="/usr/local/dotnet",
               E2E_TASKS="20", **extra_env)
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
        return "\n".join(line.rstrip() for line in screen.display).rstrip()

    try:
        pump(8.0)
        assert "Task" in visible(), visible()[-1000:]
        pump(0.6)
        return visible()
    finally:
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except Exception:
            pass


def footer_line(vis):
    # The launch gestures live on the one help-footer row that carries both hints.
    for line in vis.splitlines():
        if "new tab" in line and "split pane" in line:
            return line
    raise AssertionError("no footer row with the launch gestures found:\n" + vis[-1200:])


# ── Leg A: override ───────────────────────────────────────────────────────────────────────────────
foot_a = footer_line(run_leg({"E2E_LAUNCH_NEWTAB": "Alt+Enter", "E2E_LAUNCH_SPLIT": "Ctrl+Shift+Enter"}))
assert "Alt+Enter new tab" in foot_a, f"override new-tab chord missing from footer:\n{foot_a}"
assert "Ctrl+Shift+Enter split pane" in foot_a, f"override split-pane chord missing from footer:\n{foot_a}"
assert "Ctrl+↩ new tab" not in foot_a, f"default new-tab glyph still shown under override:\n{foot_a}"
assert "Ctrl+Alt+↩ split pane" not in foot_a, f"default split glyph still shown under override:\n{foot_a}"

# ── Leg B: control (no override) ──────────────────────────────────────────────────────────────────
foot_b = footer_line(run_leg({}))
assert "Ctrl+↩ new tab" in foot_b, f"default new-tab glyph missing without override:\n{foot_b}"
assert "Ctrl+Alt+↩ split pane" in foot_b, f"default split glyph missing without override:\n{foot_b}"
assert "Alt+Enter" not in foot_b, f"override token leaked into the control footer:\n{foot_b}"

print("ok — override leg: footer advertises 'Alt+Enter new tab' + 'Ctrl+Shift+Enter split pane' "
      "(defaults gone); control leg: shipped 'Ctrl+↩ new tab' + 'Ctrl+Alt+↩ split pane', no override token")
print("LAUNCH CHORD FOOTER E2E: PASS")
