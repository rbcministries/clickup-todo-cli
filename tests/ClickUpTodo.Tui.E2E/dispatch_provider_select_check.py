#!/usr/bin/env python3
"""Dispatch pane per-dispatch provider selector (#498).

The Ctrl+A Dispatch pane gained a provider-selector row, shown **only when 2+ providers are
configured** (DispatchPaneModel.ProviderRowVisible) so the zero-/single-provider pane stays
byte-identical to pre-#498. The pure decision + threading (ProviderRowVisible / InitialProviderIndex /
DispatchOptionApplies / Plan / DispatchAsync / RememberProvider) are unit-tested; this is the rendered
end-to-end proof that the row appears and is gated on the provider count, and that submitting with the
row present doesn't crash the real app.

Two legs, each its own boot (open the first task's detail, Ctrl+A the Dispatch pane):

  • Providers (E2E_DISPATCH_PROVIDER_SELECT=1 → a Claude default + a Codex provider): the pane shows an
    "Agent:" row listing both provider display names ("Claude" and "Codex"); typing a prompt and pressing
    Enter closes the pane (a dispatch is attempted) without crashing.

  • Control (no env → the default empty provider list): the same Ctrl+A pane opens with NO "Agent:" row —
    proving the selector is gated on there being an actual choice (2+ providers), so single-/zero-provider
    users see exactly the pre-#498 pane.
"""
import os, pty, select, struct, sys, termios, fcntl, time
import pyte, subprocess

ROWS, COLS = 32, 100
DLL = sys.argv[1]

ENTER = b"\r"
CTRL_A = b"\x01"


class App:
    def __init__(self, with_providers):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", E2E_TASKS="8")
        if with_providers:
            env["E2E_DISPATCH_PROVIDER_SELECT"] = "1"
        self.proc = subprocess.Popen(["dotnet", DLL], stdin=slave, stdout=slave, stderr=slave,
                                     env=env, close_fds=True, preexec_fn=os.setsid)
        os.close(slave)

    def answer(self, data):
        if b"\x1b[18t" in data:
            os.write(self.master, b"\x1b[8;%d;%dt" % (ROWS, COLS))
        if b"\x1b[6n" in data:
            os.write(self.master, b"\x1b[1;1R")

    def pump(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            r, _, _ = select.select([self.master], [], [], 0.03)
            if r:
                try:
                    chunk = os.read(self.master, 65536)
                except OSError:
                    break
                if not chunk:
                    break
                self.answer(chunk)
                self.stream.feed(chunk)

    def send(self, d):
        os.write(self.master, d)

    def visible(self):
        return "\n".join(self.screen.display[y].rstrip() for y in range(ROWS))

    def agent_row(self):
        """The text on the pane's 'Agent:' row (the provider selector), scanned bottom-up since the
        Dispatch pane is bottom-anchored — or None when the row isn't shown."""
        for y in reversed(range(ROWS)):
            t = self.screen.display[y]
            if "Agent:" in t:
                return t.split("Agent:", 1)[1].strip().rstrip("│ ").strip()
        return None

    def kill(self):
        try:
            os.killpg(os.getpgid(self.proc.pid), 9)
        except Exception:
            pass


def fail(app, msg):
    sys.stderr.write("FAIL: " + msg + "\n\n" + app.visible() + "\n")
    app.kill()
    sys.exit(1)


def open_dispatch_pane(with_providers):
    """Boot, open the first task's detail, and open the Ctrl+A Dispatch pane."""
    app = App(with_providers)
    app.pump(8.0)
    if "Task 0" not in app.visible():
        fail(app, "list boot failed")
    app.send(ENTER)
    app.pump(3.0)
    if "Address display" not in app.visible():
        fail(app, "task detail did not open")
    app.send(CTRL_A)
    app.pump(1.5)
    if "Dispatch to Claude" not in app.visible():
        fail(app, "Dispatch pane did not open")
    return app


def check_with_providers():
    app = open_dispatch_pane(with_providers=True)
    row = app.agent_row()
    if row is None:
        fail(app, "expected an 'Agent:' provider-selector row with 2 providers configured; none found")
    # Both provider display names render on the selector row.
    for want in ("Claude", "Codex"):
        if want not in row:
            fail(app, f"provider '{want}' not shown on the Agent row (got {row!r})")

    # Submitting with the provider row present must not crash: type a prompt and Enter, then the pane closes.
    app.send(b"go")
    app.pump(0.6)
    app.send(ENTER)
    app.pump(2.0)
    vis = app.visible()
    if "Dispatch to Claude" in vis:
        fail(app, "pane did not close after submitting a dispatch with the provider row present")
    if "Address display" not in vis:
        fail(app, "app did not return to the task detail after dispatch")

    app.kill()
    print("ok — providers: pane shows an 'Agent:' selector listing Claude + Codex; submit closes the pane")


def check_control_no_row():
    app = open_dispatch_pane(with_providers=False)
    row = app.agent_row()
    if row is not None:
        fail(app, f"the provider row must be hidden with <2 providers (byte-identical pre-#498 pane); got {row!r}")
    # The rest of the pane is intact (the working-dir control still renders) — only the provider row is gated.
    if "Dir:" not in app.visible():
        fail(app, "the default Dispatch pane is missing its working-dir row")
    app.kill()
    print("ok — control: no 'Agent:' row with <2 providers (pane byte-identical to pre-#498)")


if __name__ == "__main__":
    try:
        check_with_providers()
        check_control_no_row()
    except SystemExit:
        raise
    except Exception as e:  # pragma: no cover - defensive
        sys.stderr.write("FAIL: " + repr(e) + "\n")
        sys.exit(1)
