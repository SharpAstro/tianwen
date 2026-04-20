---
name: run-tui
description: Build and launch the TianWen.Cli TUI in a new Windows console window with stderr redirected to tui-stderr.log. Use when the user asks to run, launch, start, or open the TianWen TUI.
---

The TUI takes over stdin/stdout for terminal rendering, so it cannot share the
current console with the agent. Launch it in a **new Windows console window**.

Run from the `src/` directory. The `cmd //c start` wrapper asks the Windows
shell to spawn a detached console; `cmd //k` inside the new window keeps it
open after the TUI exits so the user can read any final stderr / stack trace
without it vanishing.

```
cd src && cmd //c start "TianWen TUI" cmd //k "dotnet run --project TianWen.Cli -- tui 2> tui-stderr.log"
```

Notes:

- Do **not** use `run_in_background: true` — `cmd //c start` detaches the new
  window itself and the parent Bash call returns immediately. The agent should
  not try to stream the TUI's output back.
- The double-slashes (`//c`, `//k`) are Git-Bash / MSYS mangling-protection so
  the literal `/c` and `/k` flags reach `cmd.exe` instead of being rewritten to
  Unix-style paths.
- Window title `"TianWen TUI"` is also what `start` needs as its first
  positional argument (Windows quirk when the first arg is quoted).
- After the user quits the TUI, check `src/tui-stderr.log` for .NET exceptions
  or terminal-capability diagnostics.
- Exit codes 127 / 13x from the TUI process mean the .NET process crashed (see
  `CLAUDE.md`), not "command not found" — read `tui-stderr.log` first.
