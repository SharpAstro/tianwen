---
name: stack
description: Run `tianwen stack` against a folder of FITS lights + calibration. Use when the user asks to stack frames, build a master, integrate a session, or wants a tianwen stack run kicked off (e.g. "stack C:\temp\stack", "integrate the SoL frames", "build a master from the latest session").
---

Usage: `/stack <data-root> [options]`

Examples:

```
/stack C:\temp\stack
/stack C:\session1 -o C:\masters --strategy TilePipelined
/stack C:\skull --group-filter skull --no-png
/stack C:\temp\stack --no-plate-solve --no-png   # fastest -- FITS only
```

The skill runs from `src/`:

```
cd src && dotnet run --project TianWen.Cli -c Release -- stack <args>
```

Use `-c Release` (Release build) -- the integration loop is CPU-bound and
the Debug build is ~3x slower per frame.

## What gets written

Under `<data-root>/output/` (or `--output` if set):

- `masters/master_<group>.fits`     — cached bias/dark/flat masters (reused across runs)
- `master_<group>.fits`             — integrated light master with WCS embedded
- `master_<group>_autocrop.fits`    — same, cropped to the no-NaN intersection
- `master_<group>.png`              — display-encoded preview with SPCC + bg-neut (unless `--no-png`)
- `master_<group>.rejection.fits`   — per-pixel rejection count map (when rejections > 0)

## Knobs worth knowing

| Flag | When to use |
|---|---|
| `--strategy <kind>` | Force a specific integrator. Default lets the selector pick. `TilePipelined` for tight RAM, `InRamAllFrames` for max fidelity when N x canvas fits, `ChunkedTwoPass` for huge N. |
| `--group-filter <pat>` | Substring on the group slug; only matching groups run. Useful when one session has multiple targets and you only want one. |
| `--group-exclude <pat>` | Inverse of `--group-filter`. |
| `--no-png` | Skip the PNG preview render. Use when iterating on the FITS pipeline and don't need visual QA. |
| `--no-plate-solve` | Skip plate-solving. Use for synthetic / non-celestial data, or when the catalog DB initialisation is the bottleneck. |
| `--stack-debayer AHD` (default) | Best colour fidelity, slower per-frame. Swap to `VNG` for ~2-3x speedup at small fidelity cost. |

## Long-running runs

Real-dataset runs can take 5-30 minutes depending on N, canvas size, and
chosen strategy. The CLI streams `[stack] <group>: <status>` lines after each
group completes; the underlying StackingPipeline emits per-stage
`[Information]` chatter via `ILogger`. **Do not background** -- the user wants
to see progress. Stream stdout to the conversation.

## Failure modes

- **Exit code 127 / 13x**: .NET process crashed. Look for a stack trace
  before the crash line; the AOT-published binary would land it in stderr.
- **"Data root does not exist"**: typo or wrong drive letter; the CLI checks
  before doing anything.
- **"fewer than 2 matched frames" for a group**: registration failed for most
  frames. Usually means `--min-stars` is too high for the field's star density
  (try `--min-stars 500` for sparse fields) or the reference frame is bad
  (rare; the pipeline picks the highest-star-count frame as reference).
- **"strategy <X> not implemented" / NotImplementedException**: someone forced
  a strategy that isn't shipped yet (e.g. `LiveAccumulator`). Drop the
  `--strategy` flag to let the selector pick a working one.

## Verifying the output

After the run, check `<output-dir>/master_<group>.png` for a quick visual.
For deeper inspection, open `master_<group>_autocrop.fits` in PixInsight /
ASTAP / FITS Viewer (`/run-fits`).
