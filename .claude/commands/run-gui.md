Build and launch the TianWen GUI application.

Run from the src/ directory with stderr redirected to a log file (captures
font atlas diagnostics and .NET exceptions without cluttering the terminal):

```
cd src && dotnet run --project TianWen.UI.Gui 2>gui-stderr.log
```

Use `run_in_background: true` on the Bash tool so the GUI runs independently.
Do NOT use shell `&` backgrounding - the GUI exits immediately when backgrounded
via `&` (SDL requires the foreground process).

After the GUI closes, check `gui-stderr.log` if there were any issues.
If the process crashes (exit code 127 or 13x), always read the stderr log
for the actual .NET exception before drawing conclusions from the exit code.

$ARGUMENTS
