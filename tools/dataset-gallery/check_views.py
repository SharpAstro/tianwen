"""Measure every rendered gallery view for the ways a card has actually gone wrong.

    python check_views.py <img dir> [--json out.json]

Every failure this looks for cost a human noticing that a picture looked off, which is the slowest
feedback loop this pipeline has. None of them is visible in a thumbnail and all of them are obvious
in a statistic, so they are checked by measurement and never by eye -- the same rule the skill states
for judging a card at all.

It reports and does not fix. A flagged card is a question ("why is this one teal?"), and answering it
belongs upstream in a verb, never here: nothing in this file touches a pixel of the product.

THE CHECKS, each with the incident behind it:

* **flat**       A channel with almost no variance. The gallery once published solid cyan, blue,
                 green and red rectangles: `Float16Staged` masters are written in ADU and `image
                 render` divided by the wrong scale, so a whole frame clipped to one corner of the
                 colour cube (#50). A real astronomical frame has structure everywhere.
* **blown**      Saturated pixels over a whole-frame fraction. A stretch that clips the top end
                 destroys exactly the stars a viewer checks focus on, and the mean stays plausible.
* **cast**       One channel far from the other two. Two different causes, both real: no WCS means
                 no SPCC, so the render falls back to sky-background white balance and leaves the
                 signal on the raw OSC balance (Rho Ophiuchi came out uniformly green); and a
                 dual-narrowband master is rank-deficient, because G and B both carry OIII and SPCC
                 has no honest curve for it (#51). The check cannot tell these apart on ONE view --
                 it says WHICH channel and by how much. **The row's filter does NOT settle it**, and
                 believing it did is the mistake documented under `crushed` below.
* **crushed**    A channel black in the ENHANCED half that the RAW half still carries. The one check
                 here that is a hard failure rather than a matter of degree, and the last one added,
                 because for two passes a filter name talked this script out of looking: flagged
                 cards were annotated "narrowband, #51, not a render fault" on the strength of the
                 header alone while the raw half of every one of them was neutral. Rank-deficiency
                 is a property of the DATA and appears in BOTH halves of a card. Five of 139 cards
                 were losing red entirely to a Linked stretch over a narrowband SPCC triple.
* **edge**      A border band darker or brighter than the interior. The partial-coverage ramp that
                 the crop is supposed to remove: the edge walk REFUSES an edge whose band never
                 settles, and a refusal keeps the ramp, which a background model then fits.

The thresholds are deliberately loose. This is a net for gross failures that should never ship, not
a judgement of whether a picture is good -- a card that trips nothing can still be dull, and a card
that trips `cast` can still be correct.
"""
import json
import os
import sys

import numpy as np
from PIL import Image

# A channel flatter than this, in 0-255 display units, is not a photograph of the sky.
FLAT_STD = 3.0
# Saturated pixels as a fraction of the frame. A real star field saturates a handful of cores.
BLOWN_FRACTION = 0.02
# How far one channel's mean may sit from the mean of the other two, relative to the overall mean.
CAST_RATIO = 0.45
# Border band width as a fraction of the short edge, and how far its median may sit from the
# interior's, in units of the interior's own standard deviation.
EDGE_BAND = 0.03
EDGE_SIGMA = 1.5
# Interior-step (mosaic seam) detection. The window is a fraction of the axis; a step is flagged when
# it stands this many times above the typical step of the same size AND clears an absolute floor in
# display levels, so a quiet frame cannot make one out of its own noise.
#
# BOTH NUMBERS ARE MEASURED OVER THE 139-CARD STORE, not chosen. The one real mosaic in it (the
# ASI585 SMC, whose panels differ enough that the union canvas is a bright strip beside a dim one)
# sits at 130x and 87 levels. The next fourteen cards land between 7x and 16x at 7 to 17 levels, and
# every one of them is a BRIGHT CORE rather than a seam -- Orion, the Lagoon, eta Car -- where the
# column median climbs steeply over a few columns. So the gap is 130x against 16x and 87 levels
# against 17, and the thresholds sit in it with a factor of four to spare either way.
#
# Calibrated against ONE positive, which is weak, and that is worth knowing: if a second mosaic ever
# lands in a store, re-measure rather than assume these still separate.
BAND_WINDOW = 0.02
BAND_STEP_RATIO = 30.0
BAND_MIN_LEVELS = 30.0

# Judged on a downscaled copy: the failures are all whole-frame properties, and a 4000 px card costs
# a second a channel to no purpose.
WORK_EDGE = 900


def measure(path):
    im = Image.open(path).convert("RGB")
    im.thumbnail((WORK_EDGE, WORK_EDGE), Image.LANCZOS)
    a = np.asarray(im).astype(np.float32)
    h, w, _ = a.shape
    ch = {n: a[..., i] for i, n in enumerate("RGB")}

    flags = []
    means = {n: float(c.mean()) for n, c in ch.items()}
    stds = {n: float(c.std()) for n, c in ch.items()}

    for n, s in stds.items():
        if s < FLAT_STD:
            flags.append(f"flat:{n}(sd {s:.1f})")

    blown = float((a >= 254).all(-1).mean())
    if blown > BLOWN_FRACTION:
        flags.append(f"blown({100 * blown:.1f}%)")

    overall = max(1e-6, sum(means.values()) / 3)
    for n in "RGB":
        others = [means[o] for o in "RGB" if o != n]
        delta = (means[n] - sum(others) / 2) / overall
        if abs(delta) > CAST_RATIO:
            flags.append(f"cast:{n}{delta:+.2f}")

    # The band is compared against the INTERIOR's own spread, not against a fixed level: these
    # frames differ by orders of magnitude in sky brightness and a fixed threshold would flag the
    # dark ones and miss the bright ones.
    b = max(2, int(round(min(h, w) * EDGE_BAND)))
    luma = a.mean(-1)
    interior = luma[b * 2:h - b * 2, b * 2:w - b * 2]
    if interior.size:
        # MAD, not standard deviation. The stars are 2 percent of the pixels at eight times the sky
        # and they triple a plain sigma (33 against 18 on the probe), which buries exactly the band
        # this is looking for: a real 36-level dark edge measured 1.08 sigma and passed.
        centre = float(np.median(interior))
        spread = max(1e-6, 1.4826 * float(np.median(np.abs(interior - centre))))

        # Compared against the level JUST INSIDE the band, never the frame interior -- the product's
        # own rule for the same question (CoverageEdgeWalk's reference is the edge's own level just
        # inside it). Against the interior this flags every unflattened sky GRADIENT, which the raw
        # half shows by definition: Omega Cen reads -1.7 sd at row 0 and climbs smoothly through
        # -1.35 at row 80 to the interior, which is light pollution, not a crop that failed. A
        # coverage ramp is a STEP and shows up against the neighbouring strip; a gradient does not.
        for name, band, inside in (
                ("left", luma[:, :b], luma[:, b:b * 3]),
                ("right", luma[:, -b:], luma[:, -b * 3:-b]),
                ("top", luma[:b, :], luma[b:b * 3, :]),
                ("bottom", luma[-b:, :], luma[-b * 3:-b, :])):
            if band.size and inside.size:
                off = (float(np.median(band)) - float(np.median(inside))) / spread
                if abs(off) > EDGE_SIGMA:
                    flags.append(f"edge:{name}{off:+.1f}sd")

    # A STEP across the frame's interior, which is what a mosaic panel seam looks like and what the
    # edge check cannot see (it only inspects the four borders). Added after a 54-sub SMC card came
    # out with three obvious vertical zones -- a mosaic whose panels have very different depth, so
    # the union canvas is a bright strip beside a dim one -- and every existing check passed it: the
    # colour cast sat at 0.33 against a 0.45 threshold and the seams are nowhere near a border.
    #
    # A seam is a STEP and a sky gradient is a RAMP, which is the whole discrimination: the column
    # medians are differenced over a window, so a smooth gradient spreads its change across every
    # window and a panel edge puts it all in one.
    for axis, profile in (("cols", np.median(luma, axis=0)), ("rows", np.median(luma, axis=1))):
        if profile.size < 8 * BAND_WINDOW:
            continue
        w = max(2, int(round(profile.size * BAND_WINDOW)))
        # Ignore the outermost window: that is the border, which `edge` already owns.
        steps = np.abs(profile[w:] - profile[:-w])[w:-w] if profile.size > 3 * w else np.array([])
        if steps.size:
            typical = max(1e-6, float(np.median(steps)))
            worst = float(steps.max())
            if worst > BAND_STEP_RATIO * typical and worst > BAND_MIN_LEVELS:
                flags.append(f"band:{axis}({worst:.0f} levels, {worst / typical:.0f}x typical)")

    return {
        "file": os.path.basename(path),
        "mean": {k: round(v, 1) for k, v in means.items()},
        "std": {k: round(v, 1) for k, v in stds.items()},
        "blown": round(blown, 5),
        "flags": flags,
    }


def channel_ratios(path):
    """(R/G, B/G) over the SIGNAL, as a pair of numbers with no threshold attached.

    Taken at the 99th percentile rather than the median because the median is background, and the
    background is deliberately neutralised per document (`BackgroundNeutralization` is re-solved on
    each image by design, so the two halves of a card are SUPPOSED to differ there). What must agree
    is the colour of the signal, because that is what a shared white balance fixes.
    """
    im = Image.open(path).convert("RGB")
    im.thumbnail((WORK_EDGE, WORK_EDGE), Image.LANCZOS)
    a = np.asarray(im).astype(np.float32)
    hi = [float(np.percentile(a[..., i], 99)) for i in range(3)]
    g = max(hi[1], 1e-6)
    return hi[0] / g, hi[2] / g


def crushed_channels(raw_path, enh_path):
    """Channels the ENHANCE drove to black that the raw view still carries.

    THE CHECK THAT SHOULD HAVE EXISTED FIRST, and the one a filter name talked this script out of
    for two passes. A rank-deficient palette is a property of the DATA, so it is present in both
    halves of a card; a channel that is fine in the raw view and gone in the enhanced one is a
    RENDER fault whatever the filter says. That is exactly what the five teal cards were: the
    headless renderer asserted a narrowband SPCC triple as colour through a Linked stretch, one
    shared shadow point came off the mean of three unequal medians, and red clipped to zero.

    Near-black rather than exactly zero because these views are JPEG by the time anyone looks.
    Returns [(channel, raw%, enhanced%)] for each channel that crossed.
    """
    out = []
    a = np.asarray(Image.open(raw_path).convert("RGB")).astype(np.float32).reshape(-1, 3)
    b = np.asarray(Image.open(enh_path).convert("RGB")).astype(np.float32).reshape(-1, 3)
    for c in range(3):
        zr = float((a[:, c] <= 1).mean() * 100)
        ze = float((b[:, c] <= 1).mean() * 100)
        # Measured over the 139-card store: the five broken cards ran 15.1 to 76.5 percent black in
        # red against 0.0 in their raw halves, and no healthy card came near. The bound is a
        # multiple AND a floor so a card that is legitimately dark in one channel cannot trip it.
        if ze > 5.0 and ze > zr * 3 + 2:
            out.append(("RGB"[c], round(zr, 2), round(ze, 2)))
    return out


def pair_drift(img_dir, ids):
    """How far each card's enhanced view has drifted from its raw view in colour.

    This is the measurement for #59. The two halves are the same pixels and now share one white
    balance, so their signal ratios should agree; when each half solved its own SPCC they did not,
    and that is what the olive wash was. It reports the distribution rather than a verdict -- any
    threshold worth having comes off these numbers over a whole store, not out of the air.
    """
    out = []
    for i in ids:
        raw = os.path.join(img_dir, "%d_raw.png" % i)
        enh = os.path.join(img_dir, "%d_enhanced.png" % i)
        if not (os.path.exists(raw) and os.path.exists(enh)):
            continue
        (rr, rb), (er, eb) = channel_ratios(raw), channel_ratios(enh)
        out.append({"id": i, "dRG": round(er - rr, 4), "dBG": round(eb - rb, 4),
                    "raw": [round(rr, 3), round(rb, 3)], "enhanced": [round(er, 3), round(eb, 3)]})
    return out


def main():
    img_dir = sys.argv[1]
    out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    rows_path = sys.argv[sys.argv.index("--rows") + 1] if "--rows" in sys.argv else None
    meta = {}
    if rows_path:
        for r in json.load(open(rows_path, encoding="utf-8")):
            meta[r["id"]] = r

    rows = [measure(os.path.join(img_dir, f))
            for f in sorted(os.listdir(img_dir)) if f.lower().endswith(".png")]

    bad = [r for r in rows if r["flags"]]
    for r in bad:
        # A NOTE, NEVER AN EXCUSE, and the difference is measured rather than read off the filter
        # name. This used to annotate any flagged card whose filter looked narrowband with "#51, not
        # a render fault" and that annotation is how five genuinely broken cards were waved through
        # twice. Rank-deficiency is a property of the DATA and shows in BOTH halves of a card, so the
        # note is only earned when the RAW half is cast the same way; when the raw half is neutral
        # the enhance did it, whatever the filter says.
        rid = r["file"].split("_")[0]
        m = meta.get(int(rid)) if rid.isdigit() else None
        why = ""
        if m:
            filt = (m.get("filter") or "").lower()
            raw_p = os.path.join(img_dir, "%s_raw.png" % rid)
            raw_cast = None
            if os.path.exists(raw_p):
                rr, rb = channel_ratios(raw_p)
                raw_cast = max(abs(rr - 1.0), abs(rb - 1.0)) > 0.25
            if any(t in filt for t in ("ultimate", "extreme", "enhance", "nbz", "sii", "oiii", "ha")):
                if raw_cast:
                    why = "   [narrowband %s and the RAW half is cast the same way: #51]" % m["filter"]
                elif raw_cast is False:
                    why = ("   [narrowband %s BUT the raw half is neutral: the ENHANCE did this, "
                           "not the filter]" % m["filter"])
            elif not m.get("solved"):
                why = "   [unsolved: no SPCC, so the render is on sky-background balance, #55]"
        print("%-58s %s%s" % (r["file"][:58], "  ".join(r["flags"]), why))
    print("\n%d views, %d flagged" % (len(rows), len(bad)))
    kinds = {}
    for r in bad:
        for f in r["flags"]:
            kinds[f.split("(")[0].split(":")[0]] = kinds.get(f.split("(")[0].split(":")[0], 0) + 1
    if kinds:
        print("by kind: " + ", ".join("%s %d" % kv for kv in sorted(kinds.items())))

    # THE HARD FAILURE, reported before the distributions because it is not a matter of degree: a
    # channel the enhance drove to black is a broken card, and no filter name excuses one.
    crushed = []
    if meta:
        for i in sorted(meta):
            raw = os.path.join(img_dir, "%d_raw.png" % i)
            enh = os.path.join(img_dir, "%d_enhanced.png" % i)
            if os.path.exists(raw) and os.path.exists(enh):
                hit = crushed_channels(raw, enh)
                if hit:
                    crushed.append({"id": i, "channels": hit})
    print("")
    if crushed:
        print("CRUSHED CHANNELS (black in the enhanced half, present in the raw half)")
        for c in crushed:
            m = meta.get(c["id"], {})
            detail = ", ".join("%s raw %.1f%% -> enh %.1f%%" % t for t in c["channels"])
            print("  id %-4d %-52s %s" % (c["id"], (m.get("filter") or "")[:52], detail))
        print("  %d card(s). This is a RENDER fault, not the data: the raw half still has the"
              " channel." % len(crushed))
    else:
        print("CRUSHED CHANNELS: none. No card lost a channel to the enhance.")

    drift = []
    if meta:
        drift = pair_drift(img_dir, sorted(meta))
        if drift:
            worst = sorted(drift, key=lambda d: -max(abs(d["dRG"]), abs(d["dBG"])))
            print("")
            print("PAIR COLOUR DRIFT (enhanced minus raw, signal ratios; #59)")
            for d in worst[:8]:
                m = meta.get(d["id"], {})
                print("  id %-4d dR/G %+.3f  dB/G %+.3f   %s" % (
                    d["id"], d["dRG"], d["dBG"], (m.get("object") or "")[:40]))
            spread = sorted(max(abs(d["dRG"]), abs(d["dBG"])) for d in drift)
            print("  over %d pairs: median %.3f  p90 %.3f  max %.3f"
                  % (len(spread), spread[len(spread) // 2],
                     spread[int(len(spread) * 0.9)], spread[-1]))

    if out:
        json.dump({"views": rows, "pairDrift": drift, "crushed": crushed},
                  open(out, "w", encoding="utf-8"), indent=1)
        print("wrote " + out)


if __name__ == "__main__":
    main()
