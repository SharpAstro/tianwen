---
name: run-gui
description: Build and launch the TianWen.UI.Gui application DETACHED (tools/start-app.ps1), with stdout and stderr redirected to src/gui-stdout.log and src/gui-stderr.log, printing the pid and both paths. Use when the user asks to run, launch, start, or open the TianWen GUI.
---

Launch through the shared detached launcher, as an ordinary FOREGROUND Bash call from the repo root
(it builds, starts the app, prints, and returns):

```
pwsh -NoProfile -File tools/start-app.ps1 gui
```

It prints the pid and both redirects:

```
tianwen-gui started detached (Debug)
pid:     12345
stdout:  ...\src\gui-stdout.log
stderr:  ...\src\gui-stderr.log
```

**Why detached, and why NOT `run_in_background`.** An app started as a Claude Code background shell dies
with that shell, and Claude Code reaps background shells when the machine runs low on memory while the
session is idle; the GUI was lost that way on 2026-09-19. The launcher starts the built `tianwen-gui.exe`
with `Start-Process`, so the app belongs to no shell. Do not wrap the launcher in `run_in_background`, and
never background the app with shell `&` (the sandbox drops it).

**What the launcher does, so you do not repeat it:**

- Builds in the foreground and prints every unique error or warning; a build failure exits non-zero and
  starts nothing.
- **Refuses when a GUI is already running** and prints that pid (the build would fail on its locked DLLs
  anyway). It never closes a running GUI, and neither should you: ask the user to close it.
  `-AllowSecondInstance` starts a second one with timestamped logs.
- Reports a launch that dies inside three seconds, with its exit code and stderr.

**Readiness and liveness.** The launcher returns once the process is up, which is before the window is.
Wait on the inspector (`mcp__sdl-ui-inspector__list_instances` shows the pid) or on a signal the app writes,
never on a fixed sleep. `Get-Process -Id <pid>` says whether it still runs.

**Environment reaches the app**, since it inherits the launcher's: anchor the clock with
`TIANWEN_NOW=2026-06-21T22:00:00+10:00 pwsh -NoProfile -File tools/start-app.ps1 gui`.

**Vulkan validation is opt-in (SdlVulkan.Renderer 6.26+).** A plain Debug run is fast by default; the
Khronos layer only loads with `SDLVK_VALIDATION=1` AND a Debug build (a Release/AOT build can never enable
it). Use it ONLY when chasing a GPU/Vulkan bug (hazards, wedges, barrier issues); it CPU-validates every
vkCmd call and makes the whole app several times slower:

```
pwsh -NoProfile -File tools/start-app.ps1 gui -Validation
pwsh -NoProfile -File tools/start-app.ps1 gui -SyncValidation   # SYNC-HAZARD-* analysis too; slower still
```

Validation output lands in `gui-stderr.log` and the inspector's `validation_report`.

**After it closes or crashes, read `src/gui-stderr.log`.** A detached app hands back no exit code, so the
stderr log (where .NET writes an unhandled exception) is the evidence. A native crash that writes nothing
there shows in the Windows Application log:
`Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime', 'Application Error' } -MaxEvents 5`.
Exit codes 127 / 13x, where one is known, mean the .NET process crashed (see `CLAUDE.md`).
