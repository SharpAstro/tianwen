---
name: dataset-gallery
description: Build a browsable gallery of a dataset bake's session masters, each shown enhanced beside the raw master, and publish it as an Artifact. Use when the user wants to look over a bake's output, compare masters across sessions, spot a session that came out wrong, or asks for "a gallery of the masters", "show me what the stacker produced", "a code artifact with the masters".
---

# A gallery of a bake's session masters

`tianwen dataset build` leaves a `session-masters/` folder of linear FITS, one per session, and nothing
that lets you LOOK at them together. This builds that: one card per master, the enhanced view on the
card and the raw master beside it in a detail panel, published as an Artifact.

## The pipeline is four tianwen verbs, in this order, and the order is the whole skill

```
tianwen image autocrop <master> -o crop.fits --margin 0.02
tianwen solve crop.fits --update-fits
tianwen image sharpen crop.fits -o sharp.fits --ai-backend rc
tianwen image render <fits> -o <png>          # once per half
```

`tools/dataset-gallery/batch_enhance.py` runs the first three per master;
`tools/dataset-gallery/render_pairs.py` runs the fourth and composes the cards.

**NOTHING IN THE SCRIPTS DOES IMAGING.** They orchestrate verbs and compose PNGs. Every time a step
was reimplemented in Python here it was wrong in a way that took a human noticing a picture looked
off to find, which is the slowest possible feedback loop. If a step you need has no verb, ADD THE
VERB: `image autocrop`, `image deblur` and `image denoise` all exist because this gallery needed them.

## Why each step is there, measured

**autocrop, and BEFORE the enhance.** A bake master is a union canvas whose border is partial
coverage. An enhancer reads the ring as structure, a stretch takes statistics over pixels no exposure
produced, and a preview renders the absent part black. `image autocrop` calls the viewer's own
`ViewerActions.ScanForCrop`, so the gallery and the viewer crop identically.

**`--margin`, because this crop feeds a model.** The edge walk REFUSES an edge whose band never
settles, which is right for a viewer and wrong here: a refusal keeps the ramp and GraXpert then fits
it. On the HIP 80609 ASI533 master, at margin 0 the left edge sat 0.8 to 1.3 sigma low after
flattening and the preview clipped it to a dark red strip (blue fell below the black point, green
nearly, red survived). 2 percent puts it at -0.3 to +0.6 sigma for 14 percent of the canvas area.

**solve, or the colour is wrong.** `MasterPreviewRenderer` runs SPCC only when the file carries a WCS.
`tianwen stack` plate-solves its masters; **the dataset bake does not**, so a session master has no
WCS at all and the render falls back to sky-background white balance. That neutralises the BACKGROUND
and leaves the signal on the raw OSC balance: Rho Ophiuchi rendered uniformly green, Antares
included. After a solve the channel means land at 55.96 / 55.08 / 55.99 and the field reads correctly.
**GraXpert does not fix this and is not meant to** -- background extraction removes the gradient per
plane and adds each plane's own median back, deliberately preserving the levels. Gradient, background
neutralisation and white balance are three different things and only a solve buys the third.

**One solve is enough**: `image sharpen` carries the WCS through to its output, so the cropped file is
solved once and both halves render calibrated.

**render through the verb, never a private stretch.** This script used to carry its own numpy curve
(median - 2.8 MAD to black, midtones to 0.25). It washed backgrounds to light grey, blew the Orion
core to a white blob, and amplified a sub-sigma edge into a visible border. `image render` is
`MasterPreviewRenderer` + `StretchSolver`, the same path the GPU viewer and `tianwen stack`'s
`master_*.png` companion use, so a card and the app agree by construction.

## Composing and publishing

One file holds both views side by side (left enhanced, right raw), because an Artifact takes at most
255 supporting files and three views of ~92 masters is 276. The page addresses each half as a 50
percent slice with `object-fit: cover` and `object-position: left|right center`.

**The ENHANCED half sets the sheet's box.** It is the cropped picture and must never be padded; the
raw half is centre-cropped to match. Letterboxing the shorter half put black bars along the top and
bottom of every card, which reads as the coverage ring still being there.

Publish with the Artifact tool from a folder the tool can read (`.artifact-gallery/` at the repo root,
git-ignored). Re-publishing the same file path keeps the URL.

## What to check before believing a card

- **Channel coverage.** A master with a channel at 0 percent finite is a failed integration, not a dim
  one; `IntegratedMaster.Labelled` refuses it now, but an older store can hold one (the eta Car
  ASI294MC master did, from a hot-pixel mask that flagged 100 percent of blue).
- **Measure, do not squint.** Background peak-to-peak per channel says whether the flatten worked;
  sigma-from-median at the frame edge says whether the crop did; channel means say whether the colour
  is calibrated. Every wrong conclusion in this pipeline's history came from judging a JPEG by eye.
- **A preview stretch exaggerates as the data improves.** After gradient correction the background is
  uniform to ~0.2 percent MAD, so a black point a fixed few MAD below the median sits under 1 percent
  down and anything dimmer clips. That is a property of the picture being good, not of it being wrong.
