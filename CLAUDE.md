# clickup-todo-cli

A keyboard-driven Terminal.Gui (v2) TUI for ClickUp tasks. Main source in
`src/ClickUpTodo/` (TUI in `Tui/`, domain in `Services/`, API facade in `ClickUp/` over a
Kiota-generated client in `ClickUp/Generated/` — never hand-edit generated files).

## Build & test

```bash
dotnet build clickup-todo.slnx
dotnet test clickup-todo.slnx    # integration tests self-skip without CLICKUP_TOKEN
```

## Validating TUI changes

For changes that touch rendering, keypress handling, the list source, or driver/output
code, validate end-to-end with the `tui-validate` skill
(`.claude/skills/tui-validate/SKILL.md`): it runs the real app under a PTY against a fake
ClickUp backend and asserts on a pyte-emulated screen (text, colors, latency, and output
volume) — a TUI absolutely can be visually validated through stdin/stdout; do not skip
validation on that assumption.

**Run it only after `dotnet test` is fully green.** The PTY harness is slower and
noisier than unit tests, and a logic bug surfaces there as a confusing rendering symptom
— chase down unit-level failures first so terminal validation only ever has to explain
terminal-level problems.
