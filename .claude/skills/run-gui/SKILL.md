---
name: run-gui
description: Build and launch the TianWen.UI.Gui application with stderr redirected to gui-stderr.log. Use when the user asks to run, launch, start, or open the TianWen GUI.
---

Run from the src/ directory with stderr redirected to a log file (captures
font atlas diagnostics and .NET exceptions without cluttering the terminal):

```
cd src && dotnet run --project TianWen.UI.Gui 2>gui-stderr.log
```

Use `run_in_background: true` on the Bash tool so the GUI runs independently.
Do NOT use shell `&` backgrounding - the GUI exits immediately when backgrounded
via `&` (SDL requires the foreground process).

**Vulkan validation is opt-in (SdlVulkan.Renderer 6.26+).** A plain Debug run is
fast by default - the Khronos validation layer only loads when `SDLVK_VALIDATION=1`
is set (AND the build is Debug; a Release/AOT build can never enable it). Launch
with validation ONLY when actually chasing a GPU/Vulkan bug (hazards, wedges,
barrier issues), never as the default - it CPU-validates every vkCmd call and
makes the whole app several times slower:

```
cd src && SDLVK_VALIDATION=1 dotnet run --project TianWen.UI.Gui 2>gui-stderr.log
```

Add `SDLVK_SYNC_VALIDATION=1` on top for synchronization-hazard analysis (the
SYNC-HAZARD-* class; even slower). Validation output lands in gui-stderr.log and
the inspector's `validation_report`.

After the GUI closes, check `gui-stderr.log` if there were any issues.
If the process crashes (exit code 127 or 13x), always read the stderr log
for the actual .NET exception before drawing conclusions from the exit code.
