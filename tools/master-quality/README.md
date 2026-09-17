# master-quality

Measurements on a bake's masters (`<bake>/session-masters/*.fits`, `<bake>/masters/*.fits`), kept so a
finding can be re-measured on the next bake instead of re-argued. Each one is the harness behind a
documented number; the number and what it decided live in the doc named beside it. Needs `astropy` and
`numpy`; the renders want `Pillow`. All read-only.

| script | what it answers | where the finding is |
|---|---|---|
| `measure.py <dir or fits>... [drizzle]` | per channel: field level, background sigma, and the 2x2 Bayer-phase pattern as column, row and checkerboard terms in per-pixel sigma, each with its sign agreement (50 percent is noise) | `docs/architecture/stacking-render-pipeline.md`, "An UNNORMALISED drizzle has the same phase bias"; #292 |
| `compare_masters.py <old> <new> <name> <outdir>` | the same session from two bakes as three PNGs: whole frame, a star-poor sky patch at 2x, and 48 px at 10x in the OLD master's sigma per channel, so noise and pattern compare directly | same |
| `classify_divergence.py` | every session master two bakes share: did it change, in what units, with what NaN (bake roots are the `A` / `B` constants) | the 8.2 flat-floor re-bake, `CHANGELOG.md` 8.2 |
| `flat_floor.py` | where a master flat's no-light population ends and real vignette begins; the empty band that `Calibrator.FlatEpsilon` = 0.02 sits in (bake root is the `root` constant) | `CHANGELOG.md` 8.2, "A dead flat pixel yields absence" |
| `blowup_map.py <master> <out.png>` | the pixels orders of magnitude above the sky, painted and dilated so one survives the downsample (the 2.46e8 Eta Car SII master) | same |
| `gate_check.py <master> <label>` | the exporter's `NeedsStretch` statistic (channel 0, `median(v - min)` against 0.125) on the whole frame and on a crop | `docs/todo/imaging.md`, the stretch gate's anchor on the canvas ring (#250's second half) |
| `gate_sweep.py <bake>/session-masters` | that statistic over every master of a bake, whole and cropped, naming what the gate refuses | same |
| `linearity.py <fits> <label> [...]` | linear or already stretched: dynamic range, ceiling pile-up, faint-end asymmetry | same |

The edge and hole renders (`edge_band.py`, `edge_level.py`, `hole_map.py`) sit with the auto-crop
harness in `tools/coverage-edge-walk/`, since that is the question they answer.
