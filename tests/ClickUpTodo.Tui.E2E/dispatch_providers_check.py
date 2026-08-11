#!/usr/bin/env python3
"""Boots the TUI under a PTY and exercises the multi-provider dispatch editor (#547): F10 opens
Settings, whose Dispatch section shows a read-only providers summary + an "Edit dispatch providers…"
button (replacing the old single exe/args fields). Clicking it opens the DispatchProvidersScreen; this
adds a provider, edits its name, makes it the default, saves back to Settings, saves Settings, and
proves the change PERSISTED by reopening F10 (the summary still names the new default) and the editor
(both rows present, the ● marker on the new default). Then it deletes a provider behind the inline Y/N
confirm and cancels the editor to prove the delete is discarded. Every step is asserted on the pyte
screen. No new backend scenario/env gate — the editor is pure UI over config, so the default boot
suffices (only a new .py file is added).

Mouse is injected as SGR-1006 clicks (ESC[<0;x;yM/m); buttons/rows are located by scanning the pyte
screen for their label text, so the check is robust to exact layout coordinates."""
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
    return [line.rstrip() for line in screen.display]

def visible():
    return "\n".join(lines()).rstrip()

def send(seq, wait=1.0):
    os.write(master, seq)
    pump(wait)

def find(text, min_col=0):
    """(col0, row0) of the first cell of `text` on the screen at/after `min_col`, else None."""
    for r, line in enumerate(lines()):
        c = line.find(text, min_col)
        if c >= 0:
            return c, r
    return None

def click_text(text, dx=1, min_col=0, wait=1.2):
    loc = find(text, min_col)
    assert loc is not None, f"could not find {text!r} to click:\n{visible()}"
    col0, row0 = loc
    col = col0 + dx
    row = row0
    os.write(master, b"\x1b[<0;%d;%dM" % (col + 1, row + 1))
    os.write(master, b"\x1b[<0;%d;%dm" % (col + 1, row + 1))
    pump(wait)

F10 = b"\x1b[21~"
ESC = b"\x1b"

try:
    pump(8.0)
    assert "Task" in visible(), "app never rendered the list:\n" + visible()[-1500:]
    pump(0.5)

    # ── F10 → Settings: the providers summary + editor button (replaces the exe/args fields) ──
    send(F10, 2.0)
    v = visible()
    assert "Dispatch providers:" in v, f"providers label missing in Settings:\n{v}"
    assert "Edit dispatch providers" in v, f"editor button missing in Settings:\n{v}"
    assert "1 provider" in v and "Claude" in v, f"seeded provider summary missing:\n{v}"
    assert "Claude executable" not in v, f"old single exe field should be gone (replaced):\n{v}"
    print("SETTINGS ok — providers summary + 'Edit dispatch providers…' button (1 provider · Claude)")

    # ── open the editor ──
    click_text("Edit dispatch providers", wait=2.0)
    v = visible()
    assert "Providers (● = default)" in v, f"editor list header missing (editor didn't open?):\n{v}"
    assert "Set as default" in v, f"editor detail/buttons missing:\n{v}"
    assert "● Claude — claude" in v, f"seeded default row missing its ● marker:\n{v}"
    print("OPEN ok — editor shows the seeded '● Claude — claude' row")

    # ── Add a provider ──
    click_text("Add", wait=1.5)
    v = visible()
    assert "New provider — claude" in v, f"Add did not append a 'New provider' row:\n{v}"
    assert "● Claude — claude" in v, f"Add should not move the default off Claude:\n{v}"
    print("ADD ok — a second 'New provider — claude' row, default still on Claude")

    # ── Edit its name (click the Name field, type a distinctive token) ──
    nl = find("Name:")
    assert nl is not None, f"Name field label missing:\n{visible()}"
    # The field renders on the row below the "Name:" label; click into it, then type.
    os.write(master, b"\x1b[<0;%d;%dM" % (nl[0] + 2, nl[1] + 2))
    os.write(master, b"\x1b[<0;%d;%dm" % (nl[0] + 2, nl[1] + 2))
    pump(0.8)
    send(b"Codex", 0.8)

    # ── Make the new provider the default (commits the field edits first) ──
    click_text("Set as default", wait=1.5)
    v = visible()
    assert "is now the default" in v, f"set-default status not shown:\n{v}"
    assert "Codex" in v, f"the edited name 'Codex' did not commit into the row:\n{v}"
    # The ● marker moved off Claude onto the (renamed) new provider.
    assert "● Claude — claude" not in v, f"default marker still on Claude after set-default:\n{v}"
    assert "  Claude — claude" in v, f"Claude row should remain (unmarked) after set-default:\n{v}"
    default_row = next((ln for ln in lines() if "●" in ln and "—" in ln), "")
    assert "Codex" in default_row, f"the ● default row should be the edited 'Codex' provider:\n{v}"
    print("EDIT+DEFAULT ok — renamed the new provider to include 'Codex' and made it the default")

    # ── Save the editor → back to Settings; the summary reflects the new default ──
    click_text("Save", wait=1.5)
    v = visible()
    assert "Set as default" not in v, f"editor did not close on Save:\n{v}"
    assert "2 providers" in v, f"Settings summary should now count 2 providers:\n{v}"
    assert "Codex" in v, f"Settings summary should name the new default 'Codex':\n{v}"
    print("SAVE-EDITOR ok — back in Settings, summary reads 2 providers · default …Codex…")

    # ── Save Settings → back to the list ──
    click_text("Save", wait=2.0)
    v = visible()
    assert "Dispatch providers:" not in v, f"Settings did not close on Save:\n{v}"
    assert "Task" in v, f"did not return to the list after Settings Save:\n{v}"
    print("SAVE-SETTINGS ok — persisted and returned to the list")

    # ── Reopen F10 → the summary persisted (proves BuildDispatchSettings wrote it into config) ──
    send(F10, 2.0)
    v = visible()
    assert "2 providers" in v and "Codex" in v, f"provider change did not persist across a Settings reopen:\n{v}"
    print("PERSIST ok — reopened Settings still reads 2 providers · default …Codex…")

    # ── Reopen the editor → both rows persisted, ● on the new default ──
    click_text("Edit dispatch providers", wait=2.0)
    v = visible()
    assert "  Claude — claude" in v, f"Claude row missing after reopen:\n{v}"
    default_row = next((ln for ln in lines() if "●" in ln and "—" in ln), "")
    assert "Codex" in default_row, f"persisted default row should be the 'Codex' provider:\n{v}"
    print("REOPEN ok — editor shows both providers with ● on the new default")

    # ── Delete the Claude row behind the inline Y/N confirm, then cancel to discard it ──
    click_text("Claude — claude", wait=1.0)       # select the (unmarked) Claude row
    click_text("Delete", wait=1.2)
    v = visible()
    assert "Delete provider 'Claude'?" in v, f"delete did not arm the inline Y/N confirm:\n{v}"
    send(b"Y", 1.2)
    v = visible()
    assert "Provider deleted." in v, f"Y did not confirm the delete:\n{v}"
    assert "Claude — claude" not in v, f"Claude row not removed after confirm:\n{v}"
    print("DELETE ok — inline Y/N confirm removed the Claude provider")

    # Cancel the editor (Esc) → the delete is discarded (Result stays null, Settings unchanged).
    send(ESC, 1.2)
    v = visible()
    assert "2 providers" in v, f"cancelling the editor should discard the delete (still 2 providers):\n{v}"
    print("CANCEL ok — Esc discarded the delete; Settings still shows 2 providers")

    send(ESC, 1.0)  # close Settings
    assert "Task" in visible(), f"Settings did not close:\n{visible()}"
    print("DISPATCH PROVIDERS E2E: PASS")
finally:
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
