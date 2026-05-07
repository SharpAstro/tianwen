---
name: run-fits
description: Build and launch the TianWen.UI.FitsViewer application with stderr redirected to fitsviewer-stderr.log. Use when the user asks to run, launch, start, or open the FITS viewer.
---

Run from the src/ directory with stderr redirected to a log file (captures
font atlas diagnostics, GLSL compilation errors, and .NET exceptions):

```
cd src && dotnet run --project TianWen.UI.FitsViewer 2>fitsviewer-stderr.log
```

Optionally pass a file or folder path to open on startup:

```
cd src && dotnet run --project TianWen.UI.FitsViewer -- /path/to/image.fits 2>fitsviewer-stderr.log
```

Use `run_in_background: true` on the Bash tool so the FITS viewer runs independently.
Do NOT use shell `&` backgrounding - the FITS viewer exits immediately when backgrounded
via `&` (SDL requires the foreground process).

After the FITS viewer closes, check `fitsviewer-stderr.log` if there were any issues.
If the process crashes (exit code 127 or 13x), always read the stderr log
for the actual .NET exception before drawing conclusions from the exit code.
