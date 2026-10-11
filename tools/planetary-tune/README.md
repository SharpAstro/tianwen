# planetary-tune

The AUTO mode's per-capture tune (`docs/plans/planetary-stacking.md`, A2 and A3 of #817), kept as a measuring tool. It was the verb
`planetary tune` on an unmerged branch: A2 (#1389) found that no tune it picks carries one calibration step, and A3 (#1390) found no
regime whose best setting sits away from the defaults past the seed spread, so AUTO runs the defaults and this does not ship.

Every candidate keep (0.1, 0.25, 0.5, 0.75), strength stop and target (through the pupil, or the telescope undone too) is stacked and
sharpened on a capture's calibrated twin (`tianwen planetary twin`) and scored against the twin's truth: bands 1 to 4 inside 0.9 radii.
The twin is stacked uncropped and without de-rotation, onto its truth's grid and instant. The whole grid is printed; with `--apply`,
the real capture is stacked with the tuned settings and with the defaults, for `tianwen planetary judge` to set against its post.

```
dotnet run --project tools/planetary-tune -c Release -- <twin.ser> --truth <twin.truth.fits> --output <dir> \
    [--planet jupiter|saturn|mars] [--aperture-mm 254 --obstruction 0.23] [--apply <capture.ser>]
```

A colour twin's truths are read beside `--truth` as `.r`, `.g` and `.b`. Without `--aperture-mm` the twin's telescope is the 254 mm
Newtonian the twins were made through. Grades are cached under `<dir>/grades`.
