#!/usr/bin/env python3
"""Dispatch pane working-dir browser seeding (#559, PTY coverage #564).

Since #559, opening the Ctrl+A Dispatch pane pre-fills the working-dir field with the task-derived
directory and — when that directory *exists* — seeds the file-tree browser to the target's parent with
the target highlighted, so ↑/↓ start "in the right place". A blank / not-yet-existent target degrades to
the base root (today's behaviour), field preserved. The decision logic (DirectoryBrowserModel.SeedTo,
DispatchWorkingDirectoryPreFill) is unit-tested; this is the missing end-to-end rendered proof.

The dispatch-seed scenario (E2E_DISPATCH_SEED=1) stands up a real base working dir on disk:

    {base}/AAAROOTKID
    {base}/WTPROJECTS/{SEEDTARGET, SIBLINGONE, SIBLINGTWO}
    {base}/ZZZROOTKID

Two legs, each its own boot (open the first task's detail, Ctrl+A the Dispatch pane):

  • Seeded (E2E_DISPATCH_SEED=1): the #96 cache resolves the pre-fill to the existing nested
    {base}/WTPROJECTS/SEEDTARGET, so the browser seeds to WTPROJECTS — asserts the field carries
    …/WTPROJECTS/SEEDTARGET, the browser shows the *parent-of-target* listing (SEEDTARGET + its
    siblings; the base-root-only children AAAROOTKID/ZZZROOTKID absent), and SEEDTARGET is highlighted.

  • Degrade (…_DEGRADE=1): the cache is not seeded, so the pre-fill is the non-existent {base}/{taskId};
    asserts the browser opened at the base root (AAAROOTKID/WTPROJECTS/ZZZROOTKID) with ".." highlighted
    and the field still carrying the (non-existent) pre-fill — never clobbered.
"""
import os, pty, select, struct, sys, termios, fcntl, time
import pyte, subprocess

ROWS, COLS = 32, 100
DLL = sys.argv[1]

ENTER = b"\r"
CTRL_A = b"\x01"

# Distinctive directory tokens the scenario seeds; used to locate the browser's ListView rows.
BASE_ROOT_KIDS = ("AAAROOTKID", "WTPROJECTS", "ZZZROOTKID")
TARGET_SIBS = ("SEEDTARGET", "SIBLINGONE", "SIBLINGTWO")
PARENT_ENTRY = ".."
KNOWN = set(BASE_ROOT_KIDS) | set(TARGET_SIBS)


class App:
    def __init__(self, degrade):
        self.screen = pyte.Screen(COLS, ROWS)
        self.stream = pyte.ByteStream(self.screen)
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", ROWS, COLS, 0, 0))
        env = dict(os.environ, TERM="xterm-256color", E2E_TASKS="8", E2E_DISPATCH_SEED="1")
        if degrade:
            env["E2E_DISPATCH_SEED_DEGRADE"] = "1"
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

    def browser_rows(self):
        """(y, token) for each rendered Dispatch-pane directory-browser row, in screen order — the rows
        whose inner text (borders/padding stripped) is the ".." parent entry or one of the seeded dir
        names. Task/prose rows never strip to those, so this isolates the ListView entries."""
        out = []
        for y in range(ROWS):
            token = self.screen.display[y].strip("│ ").rstrip()
            if token == PARENT_ENTRY or token in KNOWN:
                out.append((y, token))
        return out

    def selected_token(self):
        """The highlighted browser row's token: the browser row whose cells carry the ListView focus-fill
        background (like detail_arrow_check.py's tree_selected_index), or None if none stands out."""
        best_tok, best_bg = None, 0
        for y, token in self.browser_rows():
            nd = sum(1 for x in range(1, COLS - 1) if self.screen.buffer[y][x].bg != "default")
            if nd > best_bg:
                best_bg, best_tok = nd, token
        return best_tok if best_bg > 20 else None

    def dir_field(self):
        """The text on the pane's 'Dir:' row (the pre-filled working directory)."""
        for y in range(ROWS):
            t = self.screen.display[y]
            if "Dir:" in t:
                return t.split("Dir:", 1)[1].strip().rstrip("│ ").strip()
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


def open_dispatch_pane(degrade):
    """Boot, open the first task's detail, and open the Ctrl+A Dispatch pane."""
    app = App(degrade)
    app.pump(8.0)
    if "Task 0" not in app.visible():
        fail(app, "list boot failed")
    app.send(ENTER)
    app.pump(3.0)
    if "Address display" not in app.visible():
        fail(app, "task detail did not open")
    app.send(CTRL_A)
    app.pump(1.5)
    if "Dispatch to Claude" not in app.visible() or "Dir:" not in app.visible():
        fail(app, "Dispatch pane did not open")
    return app


def check_seeded():
    app = open_dispatch_pane(degrade=False)
    field = app.dir_field()
    if not field or "/WTPROJECTS/SEEDTARGET" not in field:
        fail(app, f"working-dir field did not carry the nested pre-fill (got {field!r})")

    tokens = [tok for _, tok in app.browser_rows()]
    # Seeded to the target's parent (WTPROJECTS): its children are listed, the base-root-only siblings
    # are not — proving the browser moved off the base root to the parent-of-target.
    for want in TARGET_SIBS:
        if want not in tokens:
            fail(app, f"expected parent-of-target listing to include {want}; got {tokens}")
    for unwanted in ("AAAROOTKID", "ZZZROOTKID"):
        if unwanted in tokens:
            fail(app, f"base-root-only child {unwanted} should not show when seeded to WTPROJECTS; got {tokens}")

    sel = app.selected_token()
    if sel != "SEEDTARGET":
        fail(app, f"target row not highlighted (highlighted={sel!r}, expected SEEDTARGET)")

    app.kill()
    print("ok — seeded: field carries …/WTPROJECTS/SEEDTARGET, browser shows the parent-of-target "
          "listing with SEEDTARGET highlighted")


def check_degrade():
    app = open_dispatch_pane(degrade=True)
    field = app.dir_field()
    # Field carries the (non-existent) task-derived pre-fill and is never clobbered by the degrade Reset().
    if not field or "SEEDTARGET" in field or "/base/" not in field:
        fail(app, f"degrade field should carry the non-existent base pre-fill, not the seed (got {field!r})")

    tokens = [tok for _, tok in app.browser_rows()]
    # Degraded to the base root: its children are listed, the target's siblings are not.
    for want in BASE_ROOT_KIDS:
        if want not in tokens:
            fail(app, f"expected base-root listing to include {want}; got {tokens}")
    for unwanted in ("SEEDTARGET", "SIBLINGONE", "SIBLINGTWO"):
        if unwanted in tokens:
            fail(app, f"nested target child {unwanted} should not show at the base root; got {tokens}")

    sel = app.selected_token()
    if sel != PARENT_ENTRY:
        fail(app, f"base root's '..' row should be highlighted on the degrade path (highlighted={sel!r})")

    app.kill()
    print("ok — degrade: non-existent pre-fill preserved, browser opened at the base root with '..' "
          "highlighted (today's behaviour)")


if __name__ == "__main__":
    try:
        check_seeded()
        check_degrade()
    except SystemExit:
        raise
    except Exception as e:  # pragma: no cover - defensive
        sys.stderr.write("FAIL: " + repr(e) + "\n")
        sys.exit(1)
