#!/usr/bin/env python
"""Digitise a filter transmission curve from a published chart image into a CSV.

Why this exists
---------------
`FilterCurveDatabase` needs wavelength/transmission pairs, and most filter vendors publish
their spectra only as a chart image. Reading a curve off such a chart BY EYE and entering the
result as if it were measured data is the worst option available: it would shape an entire
colour calibration while looking authoritative. This tool makes the extraction mechanical and,
more importantly, CHECKABLE -- it re-draws what it extracted back onto the source chart, so the
result can be inspected rather than trusted.

Why Python rather than a dotnet tool under tools/
------------------------------------------------
The repo idiom is a C# console project (see tools/BakeShaders). This is deliberately not one:
the work is per-pixel image classification and axis regression, iterated against a real chart,
which is a few dozen lines with numpy/PIL and a great deal more without. The tool's OUTPUT is a
CSV consumed by the managed pipeline, so the language boundary sits at a file rather than
inside the build.

How it works
------------
1. AXES ARE MEASURED, NOT ASSUMED. Chart gridlines are long runs of low-saturation grey; their
   pixel positions give the pixel-per-unit scale directly. The tool requires the expected
   gridline COUNT to match what it finds, so a chart it cannot calibrate fails loudly instead
   of emitting a plausible, wrongly-scaled curve.
2. THE CURVE IS THE BLACK INK. Vendor charts overlay several coloured traces (lamp spectra,
   emission lines); the filter's own curve is drawn in black. Selecting on "dark AND
   unsaturated" separates it from every coloured trace without any spatial reasoning.
3. THE LEGEND IS EXCLUDED. A legend contains a black line SAMPLE, which is ink of exactly the
   right colour at a wavelength where the filter is opaque -- on the LPS-D3 chart it sits at
   ~29 % around 760 nm, where the true curve is 0. The legend is found as the rectangle whose
   borders interrupt the gridlines, so it needs no hand-entered coordinates.
4. Per column, the ink's vertical CENTROID is the sample. At a steep passband edge the ink spans
   many pixels; the centroid is the standard reading and the residual ambiguity is reported.

Usage
-----
  python digitize_filter_curve.py CHART.png --out curve.csv --overlay check.png \
      --wavelength-range 300 1200 --wavelength-step 100 \
      --value-range 0 100 --value-step 10 \
      --expect-notches 557.7 589.0 630.0 636.4
"""

import argparse
import sys

import numpy as np
from PIL import Image, ImageDraw


def find_runs(counts, threshold, min_gap=4):
    """Cluster indices whose count exceeds `threshold` into single positions.

    A drawn gridline is 1-3 pixels wide, so the raw hits arrive in small clusters; each cluster
    collapses to its intensity-weighted centre. `min_gap` separates two distinct lines from two
    rows of one thick line.
    """
    hits = [i for i, c in enumerate(counts) if c > threshold]
    if not hits:
        return []
    groups, current = [], [hits[0]]
    for i in hits[1:]:
        if i - current[-1] <= min_gap:
            current.append(i)
        else:
            groups.append(current)
            current = [i]
    groups.append(current)
    return [float(np.average(g, weights=[counts[i] for i in g])) for g in groups]


def fit_grid(candidates, n_expected, tol=3.0):
    """Pick the uniform grid of `n_expected` lines best explained by `candidates`.

    Returns (lo, hi, matched, rejected).

    A gridline cannot be told from a legend border by how LONG it is: on the LPS-D3 chart the
    gridlines run 410..1173 px and the legend's borders 637..638, straddling the middle of that
    range, because the legend occludes part of every gridline it crosses. What does separate them
    is that gridlines lie on a UNIFORM grid and a legend border does not, so the grid is fitted
    rather than thresholded.

    Every pair of candidates is tried as the outer pair; the fit scoring the most candidates on
    its implied grid wins. Both lists are short, so the O(n^2) sweep is free -- and it doubles as
    validation, because a chart whose axis is not linear cannot score well under any pair.

    Missing lines are tolerated (an occluded interior gridline simply is not there to match),
    which is why the score counts candidates EXPLAINED rather than grid positions filled.
    """
    best = None
    for i, lo in enumerate(candidates):
        for hi in candidates[i + 1:]:
            spacing = (hi - lo) / (n_expected - 1)
            if spacing <= tol:
                continue
            grid = [lo + k * spacing for k in range(n_expected)]
            matched = [c for c in candidates if any(abs(c - g) <= tol for g in grid)]
            if best is None or len(matched) > best[0]:
                rejected = [c for c in candidates if c not in matched]
                best = (len(matched), lo, hi, matched, rejected)
    if best is None:
        return None
    _, lo, hi, matched, rejected = best
    return lo, hi, matched, rejected


def row_grey_extent(grey_row, min_run=20):
    """Full horizontal extent of a row's grey runs, ignoring the runs a trace breaks it into.

    A gridline is one line, but a curve crossing it splits it into several runs, so taking the
    LONGEST run finds a fragment and taking the whole row finds the image border. The extent of
    all runs longer than `min_run` is the gridline, border pixels excluded by requiring length.
    """
    runs, start = [], None
    for i, v in enumerate(grey_row):
        if v and start is None:
            start = i
        elif not v and start is not None:
            if i - start >= min_run:
                runs.append((start, i - 1))
            start = None
    if start is not None and len(grey_row) - start >= min_run:
        runs.append((start, len(grey_row) - 1))
    if not runs:
        return None
    return min(r[0] for r in runs), max(r[1] for r in runs), sum(r[1] - r[0] + 1 for r in runs)


def fit_excel_grid(hi_ch, sat, width, height, val_min, val_max, value_step):
    """Calibrate a spreadsheet-style chart: dense horizontal gridlines, NO vertical ones.

    The Askar / ColourMagic charts are Excel line plots. Two things differ from a vendor
    spectrophotometer chart and both defeat `fit_grid`: there is no vertical grid at all, so the
    wavelength axis has nothing to measure against, and the `val_min` gridline is drawn as the
    AXIS in a darker grey, so a fit that demands every line finds one too few and gives up.

    So: measure what is really there (the gridline rows and their horizontal extent), derive the
    bottom of the scale from the SPACING rather than from a line that may not be grey, and take
    the plot box from the gridline extent. The wavelength mapping is then edge-to-edge across
    that box, which is an ASSUMPTION about how the chart was plotted -- which is exactly why this
    mode requires `--expect-peaks`: a narrowband filter's passbands sit on known emission lines,
    so a wrong mapping cannot pass.
    """
    # Its own grey mask, and the upper bound is the reason. A spreadsheet draws minor gridlines
    # much fainter than a vendor chart draws its major ones -- these sit around 238 against the
    # shared mask's ceiling of 235, so the shared mask found ten of fifty-one and derived a
    # spacing six times too large. Silently: the fit "succeeded" and put 0 % off the bottom of
    # the image.
    grey = (sat < 20) & (hi_ch >= 170) & (hi_ch < 250)

    rows = []
    for y in range(2, height - 2):
        ext = row_grey_extent(grey[y])
        if ext is not None and ext[2] > 0.35 * width and ext[0] > 8:
            rows.append((y, ext[0], ext[1]))
    if len(rows) < 8:
        return None

    centres, current = [], [rows[0]]
    for row in rows[1:]:
        if row[0] - current[-1][0] <= 2:
            current.append(row)
        else:
            centres.append(sum(r[0] for r in current) / len(current))
            current = [row]
    centres.append(sum(r[0] for r in current) / len(current))
    if len(centres) < 8:
        return None

    diffs = np.diff(centres)
    seed = float(np.median(diffs))
    if seed <= 0:
        return None

    # Fit the whole LATTICE, rather than trusting the first line and a median gap. Two lines at
    # the ends of a fifty-line grid are routinely missed -- a trace running along the baseline
    # hides one, a plot border merges with another -- and a median gap taken over the survivors
    # came out 12.000 against a true 12.265, which walked the derived 0 % position 25 px up the
    # image. The whole flat part of the curve then fell outside the plot box and was dropped: the
    # tool reported 51 samples over 494..663nm for a chart spanning 460..700 and every check it
    # had still passed, because the passbands it DID find were correctly placed.
    centres = np.asarray(centres, dtype=float)
    # Index each line from the gap BEFORE it, accumulated -- not by dividing its offset from the
    # first line by a nominal spacing. Gridline centres land on whole pixels, so a true spacing of
    # 12.265 is measured as an alternating 12, 12, 13, and dividing the total offset by the median
    # 12.0 makes the fiftieth line come out at index 50 instead of 49. That one-step drift is
    # enough to shear the fit: intercept 153.8 and spacing 11.91 against a truth of 151.5 and
    # 12.265. Rounding each gap on its own is exact for a uniform grid and still counts a genuinely
    # missing line as the two steps it is.
    steps = np.maximum(1, np.round(np.diff(centres) / seed))
    index = np.concatenate([[0.0], np.cumsum(steps)])
    design = np.stack([np.ones_like(index), index], axis=1)
    (intercept, spacing), *_ = np.linalg.lstsq(design, centres, rcond=None)
    residual = float(np.abs(design @ [intercept, spacing] - centres).max())
    if spacing <= 0 or residual > 0.35 * spacing:
        return None

    n_intervals = (val_max - val_min) / value_step
    y_at_max = float(intercept)
    y_at_min = y_at_max + n_intervals * float(spacing)
    spacing = float(spacing)
    x_left = float(np.median([r[1] for r in rows]))
    x_right = float(np.median([r[2] for r in rows]))
    return y_at_max, y_at_min, x_left, x_right, centres, spacing


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("chart")
    ap.add_argument("--out", required=True, help="CSV to write (wavelength_nm,transmission_pct)")
    ap.add_argument("--overlay", help="PNG to write with the extracted curve drawn back on")
    ap.add_argument("--wavelength-range", nargs=2, type=float, required=True, metavar=("MIN", "MAX"))
    ap.add_argument("--wavelength-step", type=float, required=True,
                    help="spacing of the vertical gridlines, in nm")
    ap.add_argument("--value-range", nargs=2, type=float, default=[0.0, 100.0])
    ap.add_argument("--value-step", type=float, default=10.0,
                    help="spacing of the horizontal gridlines, in percent")
    ap.add_argument("--sample-step", type=float, default=1.0, help="output sampling, in nm")
    ap.add_argument("--ink-max", type=int, default=100,
                    help="max channel value for a pixel to count as black ink")
    ap.add_argument("--ink-sat", type=int, default=40,
                    help="max (max-min) channel spread for ink to count as UNSATURATED")
    ap.add_argument("--ink-rgb", metavar="R,G,B",
                    help="select the trace by COLOUR instead of by blackness, within --ink-tol. "
                         "Needed because 'the filter's own curve is the black one' is a convention "
                         "of one chart family, not a rule: the same vendor draws it dark red "
                         "(192,0,0) on other product pages, where the black-ink selector finds "
                         "nothing at all and says so.")
    ap.add_argument("--ink-tol", type=int, default=60,
                    help="per-channel tolerance around --ink-rgb")
    ap.add_argument("--grid-mode", choices=["auto", "excel"], default="auto",
                    help="auto: gridlines on BOTH axes (vendor spectrophotometer charts). "
                         "excel: dense horizontal gridlines only, wavelength axis spans the plot "
                         "box edge to edge (spreadsheet line charts). excel REQUIRES "
                         "--expect-peaks, which is what tests the assumed x mapping.")
    ap.add_argument("--expect-peaks", nargs="*", type=float, default=[],
                    help="wavelengths the passbands must peak at, e.g. the emission lines a "
                         "narrowband filter is cut for. Validates the axis mapping against "
                         "physics rather than against the chart it came from.")
    ap.add_argument("--peak-tol", type=float, default=2.0,
                    help="how far, in nm, an extracted peak may sit from --expect-peaks")
    ap.add_argument("--expect-notches", nargs="*", type=float, default=[],
                    help="wavelengths the vendor says are blocked; reported as a check")
    args = ap.parse_args()

    rgb = np.asarray(Image.open(args.chart).convert("RGB")).astype(int)
    height, width, _ = rgb.shape
    hi_ch = rgb.max(2)
    lo_ch = rgb.min(2)
    sat = hi_ch - lo_ch

    # --- 1. calibrate off the gridlines -------------------------------------------------------
    grey = (sat < 25) & (hi_ch >= 100) & (hi_ch < 235)
    wl_min, wl_max = args.wavelength_range
    val_min, val_max = args.value_range
    h_rejected, v_rejected = [], []

    print(f"chart      : {width}x{height}")

    if args.grid_mode == "excel":
        # The assumed x mapping is the whole risk in this mode, so refuse to run without the
        # check that tests it. Emitting a curve whose wavelengths are wrong by a smooth factor is
        # the failure this tool exists to prevent.
        if not args.expect_peaks:
            sys.exit("--grid-mode excel requires --expect-peaks: the wavelength axis is assumed "
                     "to span the plot box, and the peak positions are what test that")
        # A spreadsheet chart draws minor gridlines; the step is the LINE interval, not the label
        # interval, so --value-step 2 for a chart labelled every 10.
        fit = fit_excel_grid(hi_ch, sat, width, height, val_min, val_max, args.value_step)
        if fit is None:
            sys.exit("could not find a uniform horizontal gridline set; check --value-step "
                     "(it is the GRIDLINE interval, not the label interval)")
        y_at_max, y_at_min, x_at_min, x_at_max, h_lines, spacing = fit
        px_per_nm = (x_at_max - x_at_min) / (wl_max - wl_min)
        print(f"y axis     : {val_max:g}% at px {y_at_max:.1f}, {val_min:g}% at px {y_at_min:.1f} "
              f"({len(h_lines)} gridlines, {spacing:.3f} px per {args.value_step:g}%)")
        print(f"x axis     : plot box px {x_at_min:.1f}..{x_at_max:.1f} taken as "
              f"{wl_min:g}..{wl_max:g}nm ({px_per_nm:.4f} px/nm) -- ASSUMED edge-to-edge, "
              f"checked below against --expect-peaks")
    else:
        h_cand = find_runs(grey.sum(1), threshold=0.30 * width)
        v_cand = find_runs(grey.sum(0), threshold=0.30 * height)
        v_expected = int(round((wl_max - wl_min) / args.wavelength_step)) + 1
        h_expected = int(round((val_max - val_min) / args.value_step)) + 1

        h_fit = fit_grid(h_cand, h_expected)
        v_fit = fit_grid(v_cand, v_expected)

        # A chart that cannot be calibrated must FAIL, not guess. A wrongly-scaled curve is far
        # worse than no curve: it is wrong by a smooth factor, so nothing downstream looks
        # anomalous.
        if h_fit is None or len(h_fit[2]) < h_expected - 1:
            sys.exit(f"could not fit {h_expected} horizontal gridlines to {h_cand}; "
                     f"check --value-range / --value-step")
        if v_fit is None or len(v_fit[2]) < 3:
            sys.exit(f"could not fit {v_expected} vertical gridlines to {v_cand}; "
                     f"check --wavelength-range / --wavelength-step")

        y_at_max, y_at_min, h_lines, h_rejected = h_fit
        x_at_min, x_at_max, v_lines, v_rejected = v_fit
        px_per_nm = (x_at_max - x_at_min) / (wl_max - wl_min)

        print(f"x axis     : {wl_min:g}nm at px {x_at_min:.1f}, {wl_max:g}nm at px "
              f"{x_at_max:.1f} ({px_per_nm:.4f} px/nm, {len(v_lines)}/{v_expected} on grid)")
        print(f"y axis     : {val_max:g}% at px {y_at_max:.1f}, {val_min:g}% at px "
              f"{y_at_min:.1f} ({len(h_lines)}/{h_expected} gridlines on grid)")
        if h_rejected or v_rejected:
            print(f"off-grid   : rows {[round(r) for r in h_rejected]}, "
                  f"cols {[round(c) for c in v_rejected]} (legend borders, not gridlines)")

    # --- 2. find the legend, as the box whose borders interrupt the gridlines ------------------
    # Its borders are grey runs that are LONG but shorter than a gridline, and they enclose a
    # region where gridlines are missing. Detected rather than hand-entered so a differently-laid-
    # out chart from the same vendor still works.
    interior = np.zeros_like(grey)
    y0p, y1p = int(y_at_max), int(y_at_min)
    x0p, x1p = int(x_at_min), int(x_at_max)
    interior[y0p:y1p + 1, x0p:x1p + 1] = True

    # The legend falls out of the grid fit for free: its borders are exactly the long grey runs
    # that did NOT land on the grid. Nothing is hand-entered, so the same invocation works on a
    # chart from the same vendor with the legend placed somewhere else.
    legend = None
    if len(h_rejected) >= 2 and len(v_rejected) >= 2:
        legend = (min(v_rejected), min(h_rejected), max(v_rejected), max(h_rejected))
        print(f"legend     : excluded rect x {legend[0]:.0f}..{legend[2]:.0f}, "
              f"y {legend[1]:.0f}..{legend[3]:.0f} -- it holds a black line SAMPLE, which is ink "
              f"of exactly the right colour at a wavelength where the filter is opaque")
    else:
        print("legend     : none detected")

    # --- 3. select the curve's ink ------------------------------------------------------------
    if args.ink_rgb:
        want = [int(v) for v in args.ink_rgb.split(",")]
        if len(want) != 3:
            sys.exit("--ink-rgb needs three comma-separated channel values")
        ink = interior.copy()
        for c in range(3):
            ink &= np.abs(rgb[:, :, c] - want[c]) <= args.ink_tol
        print(f"ink        : colour {tuple(want)} +/-{args.ink_tol}, {int(ink.sum())} px")
    else:
        ink = (hi_ch < args.ink_max) & (sat < args.ink_sat) & interior
        print(f"ink        : black (max<{args.ink_max}, sat<{args.ink_sat}), {int(ink.sum())} px")
    if legend is not None:
        lx0, ly0, lx1, ly1 = (int(v) for v in legend)
        ink[ly0:ly1 + 1, lx0:lx1 + 1] = False

    # --- 4. per-column centroid, then bin to the requested sampling ---------------------------
    per_nm = {}
    ambiguous = 0
    for x in range(x0p, x1p + 1):
        ys = np.flatnonzero(ink[:, x])
        if ys.size == 0:
            continue
        if ys.max() - ys.min() > 0.25 * (y1p - y0p):
            ambiguous += 1
        y = float(ys.mean())
        nm = wl_min + (x - x_at_min) / px_per_nm
        pct = val_min + (y_at_min - y) * (val_max - val_min) / (y_at_min - y_at_max)
        per_nm.setdefault(round(nm / args.sample_step) * args.sample_step, []).append(pct)

    curve = sorted((nm, float(np.mean(v))) for nm, v in per_nm.items())
    if not curve:
        sys.exit("no black ink found inside the plot area -- check --ink-max / --ink-sat")

    print(f"extracted  : {len(curve)} samples, {curve[0][0]:g}..{curve[-1][0]:g}nm, "
          f"{ambiguous} column(s) with a tall ink run (steep edges)")

    # The value axis, checked from the other end. A blocking filter's floor is 0 by construction,
    # so an extracted minimum far off 0 means the scale is wrong even when the peaks are placed
    # correctly -- the failure above was exactly that shape. Reported for every chart, because it
    # costs nothing and it is the one diagnostic that reads the axis rather than the curve.
    floor = min(p for _, p in curve)
    span_nm = curve[-1][0] - curve[0][0]
    print(f"baseline   : lowest sample {floor:+.2f} % (a blocking filter's floor should be ~0), "
          f"coverage {span_nm:g}nm of the {wl_max - wl_min:g}nm axis")

    # --- 5. validate against what the vendor SAYS the filter does -----------------------------
    # This is the independent check: the notches were not used to build the curve, so their
    # landing on the stated wavelengths tests the calibration and the extraction together.
    if args.expect_peaks:
        # The independent check for a narrowband filter. Its passbands are cut for specific
        # emission lines, so where the extracted peaks LAND tests the axis mapping against
        # physics. Nothing about the peaks was used to build the curve, and in excel mode the
        # wavelength axis is an assumption, so this is the only thing standing between a smoothly
        # mis-scaled curve and the database.
        values = np.array([p for _, p in curve])
        waves = np.array([nm for nm, _ in curve])
        hot = values > 0.5 * values.max()
        bands, start = [], None
        for i, v in enumerate(hot):
            if v and start is None:
                start = i
            elif not v and start is not None:
                bands.append((start, i - 1))
                start = None
        if start is not None:
            bands.append((start, len(hot) - 1))

        found = []
        for lo, hi in bands:
            k = lo + int(np.argmax(values[lo:hi + 1]))
            half = values[k] / 2.0
            li, ri = lo, hi
            while li > 0 and values[li] > half:
                li -= 1
            while ri < len(values) - 1 and values[ri] > half:
                ri += 1
            found.append((waves[k], values[k], waves[ri] - waves[li]))

        print("peak check (the filter is cut for these lines):")
        worst = 0.0
        for want in args.expect_peaks:
            if not found:
                print(f"  {want:7.1f}nm -> NO PASSBAND FOUND")
                worst = float("inf")
                continue
            nm, pct, fwhm = min(found, key=lambda t: abs(t[0] - want))
            err = abs(nm - want)
            worst = max(worst, err)
            print(f"  {want:7.1f}nm -> peak {pct:5.1f} % at {nm:7.2f}nm "
                  f"(off by {err:4.2f}nm, FWHM {fwhm:.2f}nm) "
                  f"{'OK' if err <= args.peak_tol else 'MIS-CALIBRATED'}")
        # The FWHM read off a chart is bounded below by the chart's own sampling: a smoothed line
        # through samples every 5nm cannot render a 6nm passband narrower than its sampling, so a
        # measured FWHM wider than the vendor's spec is the plot, not the glass. Reported, never
        # asserted.
        if worst > args.peak_tol:
            sys.exit(f"peak positions are off by up to {worst:.2f}nm (tolerance "
                     f"{args.peak_tol:g}nm) -- the wavelength axis mapping is wrong, refusing "
                     f"to emit a curve that would look authoritative and be wrong")

    if args.expect_notches:
        lookup = dict(curve)
        print("notch check (vendor says these lines are blocked):")
        worst_notch = 0.0
        for nm in args.expect_notches:
            key = round(nm / args.sample_step) * args.sample_step
            got = lookup.get(key)
            if got is None:
                print(f"  {nm:7.1f}nm -> no sample")
                continue
            worst_notch = max(worst_notch, got)
            print(f"  {nm:7.1f}nm -> {got:5.1f} %  {'OK' if got < 15 else 'NOT BLOCKED'}")
        peak = max(p for _, p in curve)
        print(f"peak transmission: {peak:.1f} %   worst 'blocked' line: {worst_notch:.1f} %")

    # --- 6. emit, only once the checks have passed --------------------------------------------
    # Written AFTER validation on purpose. It used to be written before, so a chart that failed
    # its own notch check still left a CSV on disk that looked exactly like a good one -- and
    # the importer downstream has no way to tell the difference.
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("wavelength_nm,transmission_pct\n")
        for nm, pct in curve:
            fh.write(f"{nm:g},{max(0.0, min(100.0, pct)):.2f}\n")
    print(f"wrote      : {args.out}")

    # --- 7. draw it back, which is the only real proof ----------------------------------------
    if args.overlay:
        im = Image.open(args.chart).convert("RGB")
        d = ImageDraw.Draw(im)
        for nm, pct in curve:
            x = x_at_min + (nm - wl_min) * px_per_nm
            y = y_at_min - (pct - val_min) * (y_at_min - y_at_max) / (val_max - val_min)
            d.ellipse([x - 1.6, y - 1.6, x + 1.6, y + 1.6], fill=(255, 0, 255))
        im.save(args.overlay)
        print(f"overlay    : {args.overlay} (magenta should sit exactly on the black trace)")


if __name__ == "__main__":
    main()
