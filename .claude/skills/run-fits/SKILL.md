---
name: run-fits
description: Build and launch the TianWen.UI.FitsViewer application DETACHED (tools/start-app.ps1), with stdout and stderr redirected to src/fitsviewer-stdout.log and src/fitsviewer-stderr.log, printing the pid and both paths. Use when the user asks to run, launch, start, or open the FITS viewer.
---

Launch through the shared detached launcher, as an ordinary FOREGROUND Bash call from the repo root
(it builds, starts the app, prints, and returns):

```
pwsh -NoProfile -File tools/start-app.ps1 fits
```

Optionally pass a file or folder to open on startup (a relative path resolves against the directory you
call from; put `--` first if an argument starts with a dash):

```
pwsh -NoProfile -File tools/start-app.ps1 fits D:\Astro\M42\master.fits
pwsh -NoProfile -File tools/start-app.ps1 fits -- --new-window D:\Astro\M42
```

It prints the pid and both redirects (`src/fitsviewer-stdout.log`, `src/fitsviewer-stderr.log`).

**Why detached, and why NOT `run_in_background`.** An app started as a Claude Code background shell dies
with that shell, and Claude Code reaps background shells when the machine runs low on memory while the
session is idle. The launcher starts the built `tianwen-fits.exe` with `Start-Process`, so the app belongs
to no shell. Do not wrap the launcher in `run_in_background`, and never background the app with shell `&`.

**What the launcher does, so you do not repeat it:** builds in the foreground and prints every unique error
or warning (a failure starts nothing); **refuses when a viewer is already running** and prints that pid,
never closing it (`-AllowSecondInstance` starts another with timestamped logs); reports a launch that dies
inside three seconds, with its exit code and stderr. Note the viewer's own single-instance hand-off: a
second launch on the same folder passes its file to the window already open (`--new-window` opts out), so
`-AllowSecondInstance` alone may not give you a second window.

**Readiness and liveness.** Wait on the inspector (`mcp__sdl-ui-inspector__list_instances`), never on a
fixed sleep; `Get-Process -Id <pid>` says whether it still runs. `-Validation` / `-SyncValidation` load the
Vulkan validation layers exactly as for the GUI (Debug only, slow; for GPU bugs only).

**After it closes or crashes, read `src/fitsviewer-stderr.log`** (font atlas diagnostics, shader errors, .NET
exceptions). A detached app hands back no exit code; a native crash that writes nothing there shows in the
Windows Application log (`Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime', 'Application Error' } -MaxEvents 5`).
