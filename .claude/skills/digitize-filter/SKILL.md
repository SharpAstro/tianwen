---
name: digitize-filter
description: Digitise a filter transmission curve from a vendor chart image into the FilterCurveDatabase, via tools/digitize-filter-curve. Use when the user supplies a filter spectrum chart (PNG/JPEG) and wants the filter added, or asks why a filter is missing or why a FILTER card does not resolve. Covers the three chart families, the per-family flags, the validation gates, and the matcher collision to check afterwards.
---

Usage: `/digitize-filter <chart-image-path>` (or several). The user supplies charts; everything
below is what the tool has to be TOLD about this chart, because none of it is guessable.

## The rule this whole tool exists for

Reading a curve off a chart by eye and entering it as if it were measured data would shape an
entire colour calibration while looking authoritative. So the extraction is mechanical, and
**the overlay is the proof**. Always pass `--overlay` and always LOOK at it before shipping the
CSV. Every failure listed below produced a plausible curve that the numbers alone did not reveal.

**A chart the tool cannot calibrate must FAIL, not guess.** Never weaken a validation gate to get
a curve out. A gate that refuses means either the flags are wrong for this family or the chart is
unusable, and both are useful answers.

## Step 1: identify the chart family

Probe first, do not guess. A short `python -c` over PIL/numpy: image size, median brightness (dark
vs light background), and the commonest saturated colours (that is the trace).

| family | looks like | flags |
|---|---|---|
| vendor spectrophotometer | light bg, grey gridlines on BOTH axes | default (`--grid-mode auto`) |
| spreadsheet | light bg, dense horizontal gridlines, NO vertical ones | `--grid-mode excel` |
| dark marketing chart | black bg, bright trace, dashed same-colour gridlines | `--dark-chart --ink-longest-run --drop-annotation-columns` |

A coloured trace needs `--ink-rgb R,G,B`, read off the probe rather than by eye.

## Step 2: the wavelength axis, where the silent errors live

Three sources, best first:

1. **Vertical gridlines** (default) -- only for a chart that has them.
2. **`--x-anchors NM:PX,...`** -- pixel positions taken from the TICK LABELS. Least-squares fitted,
   residuals reported; want under about 1 nm.
3. **`--x-from-plot-box`**, or `--grid-mode excel`, which assumes the axis spans the plot box edge
   to edge. Both REQUIRE `--expect-peaks`, precisely because the axis is being assumed.

**`--y-anchors PCT:PX,...` is the same escape hatch for the VALUE axis**, and the case that needs it
is a gridline set mixing 10% majors with partially-visible 2.5% minors: a uniform-grid fit has no way
to tell which is which and refuses. Do NOT respond to that by passing the minor interval as
`--value-step` -- excel mode then finds only the majors, calls them 2.5% apart, and puts 0% a
thousand pixels off the image while reporting success.

**Watch for a value axis that does not end on a gridline.** A chart labelled to 90 with minor lines
every 2.5 draws its box at 91.25, and a curve may peak in that margin: the tool now allows one
`--value-step` of headroom past the outermost gridlines, because clipping there truncated
L-Ultimate's H-alpha band (peak ~91%) and left a 0.7 nm FWHM on a 3 nm filter.

**Never assume edge-to-edge without checking the labels.** On the L-Quad chart the outermost labels
read 300 and 900 while the box actually spans 301.0 to 947.5 nm, so edge-to-edge would have been
47 nm out at the red end. The tool prints the box's true end wavelengths under an anchor fit so
that is visible rather than inferred.

## Step 3: pick the right validation gate

The most important choice here, and getting it wrong looks exactly like a calibration failure.

- **`--expect-peaks`** -- a NARROWBAND filter, where the passband IS the line. Checks peak position.
- **`--expect-passed`** -- when one passband covers SEVERAL lines. A window passing both Hb 486.1
  and OIII 500.7 has ONE peak, so a peak-position test measures the distance from a line to the
  band's centre and calls a correct curve mis-calibrated. It reported "off by 15.70nm" on a curve
  that was right.
- **`--expect-notches`** -- lines the vendor states are blocked.

**Pair passed with notches whenever the chart annotates both.** Interleaved pass and block
wavelengths are a far stronger axis test than either alone: a shifted axis moves a passband onto a
line marked suppressed, so the two halves cannot both hold unless the calibration is right. The
L-Quad chart supplies nine such wavelengths.

Read the `baseline` line the tool always prints, too. A blocking filter's floor is about 0 by
construction, so it tests the VALUE axis rather than the curve, and it is what catches a value
scale that is wrong while the peaks are placed correctly.

## Step 4: things on a chart that are not data

Every one of these was found by looking at an overlay, never from a summary:

- **A legend holds a line SAMPLE** in the trace's exact colour, at a wavelength where the filter is
  opaque. Auto-detected, but the hypothesis is REJECTED when the box spans over 30% of the plot: a
  dotted minor grid feeds it false borders, and it masked the curve itself. `--legend-rect` for a
  legend genuinely inside the plot, `--no-legend` to skip.

  **`--no-legend` is not the safe default -- it is the dangerous one.** On the L-Ultimate chart the
  legend sits in the top right INSIDE the plot, which is 700-800 nm where the filter is opaque, and
  its blue line sample plus rotated navy text are the trace's exact colour. Skipping detection made
  780 nm read 90.1%: a confident, plausible passband that does not exist, and every other check
  passed. Its box borders are drawn dark rather than grey so the detector cannot see them, hence
  `--legend-rect`. If you reach for `--no-legend`, check the far end of the axis afterwards.
- **Gridlines the same colour as the trace.** `--ink-longest-run` separates them by thickness, and
  chrome rows are blanked -- except the axis-minimum line, which a blocking filter's curve LIES ON
  through every blocked region. Blanking it deleted the curve wherever the filter does its job.
- **The plot border** is a bright row the value-grid fit rejects, so it is not a "gridline" and drew
  a solid 100% line across the whole output until the rejected rows were blanked as well.
- **A watermark inside the plot area** can be neutral and bright enough to pass a white-ink test.
  Tighten `--ink-max`.
- **Emission-line markers drawn OVER the curve** occlude it at exactly the wavelengths worth
  validating. `--drop-annotation-columns` skips them and the gaps interpolate, which is what the
  database does at query time anyway. Check that no gap over about 2 nm falls on a transition.
- **`--ink-rgb` also requires saturation**, because a per-channel tolerance alone cannot separate a
  coloured trace from grey chrome: at +/-70 around (213,90,92) a mid-grey (150,150,150) satisfies
  all three bounds.

## Step 5: two scales of the same curve

Where a vendor publishes both, **take amplitude from the zoomed chart and coverage from the wide
one**, then splice. At 1.46 px/nm a 7 nm passband is ten pixels of near-vertical ink and the column
centroid averages its own peak down: measured, the wide charts read 79% and 88% where the zoomed
ones read 90% and 96%.

Corollary: **a chart whose scale cannot resolve the passband cannot give a curve at all.** A 3 nm
filter on a 350-800 nm chart is about 5 px wide and no flag fixes that. Ask for a zoomed chart
instead of shipping a guess.

## Step 6: land it

1. Write the CSV to `tools/import-sasp-data/local-filters/<Vendor>-<Product>.csv` in **CHART
   UNITS** (nanometres and percent) so a human can check a row against the chart. The importer
   converts to the database's Angstrom and fraction. Feeding chart units straight in makes the
   filter 100x over-transmissive.
2. The header comment carries: source image, the axis calibration and how it was anchored, the
   validation numbers, and anything excluded and why.
3. `dotnet run --project tools/import-sasp-data -- --merge-only`
4. Update the count in `FilterCurveDatabaseTests.LoadAsync_LoadsAllNCurves`.
5. Add a validation test mirroring the gate that was used (pass/block, or notch).

**To BACK a curve out, delete its CSV and re-merge.** The merge retracts it, because the names it
injected are recorded in `local-filters/.merged-names.txt` (checked in, written only after a
successful merge). That manifest exists because the `.gs.gz` is the merge's own input, so without it
a merge could only ever add -- and because ORIGIN cannot say who put a curve there: the upstream
SASP data was itself built from CSVs, so `IDAS_LPS_P3_LIGHT_POLLUTION` carries
`IDAS-LPS-P3-Light-Pollution.csv` and pruning on "origin ends in .csv" would delete upstream curves.
Never hand-edit the manifest; delete the CSV and let the merge do it.

## Step 7: ALWAYS re-check the matcher afterwards

**Adding a curve can capture other products' names.** `TryMatchFilter` is token overlap, so a new
name containing an existing product's name as a token SUBSET will answer for it. Adding
`OPTOLONG_L_QUAD_ENHANCE` made L-eNhance, L-eXtreme and L-Ultimate all resolve to the quad-band,
because "optolong" plus the single letter "l" already clears the half-coverage gate on a four-token
key.

Run `ReportKnownLightPollutionFilters`, which prints what a written card resolves to, and read
every line including the ones you did not touch. Two gates stand behind it:

- **document frequency** -- an unmatched key token appearing in exactly one curve name is what makes
  that curve specific, so the match is refused. Catches `L-eNhance` landing on L-Quad Enhance
  (`quad` names one curve).
- **two-sided token difference** -- the written name has a token the curve lacks AND the curve has
  one the written name lacks, so they diverge and neither matches. Catches what frequency cannot:
  `L-eNhance` landing on L-Ultimate, where `ultimate` appears in seven names and is not rare at all.

A one-sided difference still resolves in both directions, which is what keeps real cards working: a
slot named `Baader R CCD 31mm` matches `BAADER_R`, and `LPS-D3` matches `IDAS_LPS_D3`.

Then the suite:
`dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~FilterCurveDatabase"`

## Worked example: the dark family, hardest of the three

```bash
python tools/digitize-filter-curve/digitize_filter_curve.py "Lqef chart.jpeg" \
  --dark-chart --no-legend --ink-longest-run --drop-annotation-columns --ink-max 60 \
  --out tools/import-sasp-data/local-filters/Optolong-L-Quad-Enhance.csv \
  --overlay verify.png \
  --wavelength-range 295 947 --wavelength-step 100 --value-range 0 100 --value-step 10 \
  --x-anchors "300:115.5,400:345.5,500:579.5,600:810.5,900:1507.0" \
  --sample-step 1 \
  --expect-passed 486.1 500.7 656.3 671.6 \
  --expect-notches 435.8 546.1 589.0 589.6 615.4
```

`--value-step` is the GRIDLINE interval, not the label interval. They differ on spreadsheet charts,
where minor gridlines are drawn between the labelled ones, and a mismatch derives a spacing several
times too large while still reporting success.

## Check the chart is the filter it is named after

A file called `L-Ultimate_HA.png` turned out to be an **L-eNhance** chart: its legend read
"Optolong L-Enhance" and the annotation said FWHM 10 nm, where L-Ultimate is 3 nm. Read the legend
and any stated FWHM, and compare against the product's specification.

`--expect-peaks 656.3` would have PASSED on that chart, because 656.3 sits inside the 10 nm band.
The peak check validates the wavelength AXIS, not the filter's identity, so nothing downstream
would have caught a 10 nm curve stored for a 3 nm filter.

## SPCC will still decline on a narrowband curve, and that is not a bug

The white balance integrates a broadband stellar SED against QE x CFA, which is meaningless over a
few nanometres. Narrowband SPCC is blocked on per-star Gaia DR3 `xp_sampled` spectra, not on filter
curves -- see ADR-3 in `docs/plans/narrowband-colour.md`. A digitised duo or quad-band curve is
still worth having: sensor-matched luma weights, and the pre-convolved response that an OSC frame
shot through that filter has to be modelled with.
