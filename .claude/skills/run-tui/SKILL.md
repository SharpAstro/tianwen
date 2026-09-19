---
name: run-tui
description: Build and launch the TianWen.Cli TUI DETACHED in a new Windows console window (tools/start-app.ps1), with stderr redirected to src/tui-stderr.log, printing the TUI's pid and the log path. Use when the user asks to run, launch, start, or open the TianWen TUI.
---

Launch through the shared detached launcher, as an ordinary FOREGROUND Bash call from the repo root
(it builds, opens the console window, prints, and returns):

```
pwsh -NoProfile -File tools/start-app.ps1 tui
```

It prints the TUI's own pid, the pid of the console window around it, and the stderr path:

```
tianwen tui started detached (Debug), in its own console window
pid:     12345 (console window: cmd pid 12340)
stdout:  the console window
stderr:  ...\src\tui-stderr.log
```

Anything after `tui` passes through (`tools/start-app.ps1 tui -- --some-flag`; the `--` keeps a dashed
argument away from the launcher's own parameters).

Notes:

- **The TUI takes over stdin/stdout for terminal rendering**, so it cannot share the agent's console and
  its stdout cannot go to a file: it gets a new console window (`cmd /k`, titled "TianWen TUI", which stays
  open after the TUI exits so a final stack trace is readable) and only stderr is redirected. Read the
  screen through the console inspector (`mcp__console-inspector__*`), not through a log.
- **Do not use `run_in_background`** and do not start it with a bare `cmd //c start` any more: the
  launcher is what reports the pid and refuses a second TUI.
- **Only a `tianwen.exe` whose command line says `tui` counts as a running TUI.** A detached CLI job (a
  dataset bake) is `tianwen.exe` too; it does not block a launch, but it DOES lock the binaries the build
  writes, and the launcher says so when the build fails.
- After the user quits the TUI, check `src/tui-stderr.log` for .NET exceptions or terminal-capability
  diagnostics. Exit codes 127 / 13x mean the .NET process crashed (see `CLAUDE.md`), not "command not
  found"; the console window prints "TUI exited with code N" when it ends, and the log carries the
  exception.
