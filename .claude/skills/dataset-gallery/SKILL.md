---
name: dataset-gallery
description: Build a browsable gallery of a dataset bake's session masters, each shown enhanced beside the raw master, and publish it as an Artifact. Use when the user wants to look over a bake's output, compare masters across sessions, spot a session that came out wrong, or asks for "a gallery of the masters", "show me what the stacker produced", "a code artifact with the masters".
---

# A gallery of a bake's session masters

`tianwen dataset build` leaves a `session-masters/` folder of linear FITS, one per session, and nothing
that lets you LOOK at them together. This builds that: one card per master, showing it enhanced, the
same crop before enhancement, and a 1:1 patch of its sky, published as an Artifact.

## The whole run, in order

```
python tools/dataset-gallery/build_rows.py  <store> rows.json
python tools/dataset-gallery/build_views.py <store> rows.json .artifact-gallery/img [--jobs N]
python tools/dataset-gallery/check_views.py .artifact-gallery/img --json qc.json
python tools/dataset-gallery/inject_rows.py rows.json .artifact-gallery/bake-masters.html
```

**Four scripts, four jobs, no overlap: the table, the pictures, the measurement, the page.** There
used to be seven, and the two that mattered split one job down the middle -- `batch_enhance` rendered
the raw view and `render_views` the enhanced one, hours apart -- which is what let the pair disagree
about colour. `render_pairs` (the superseded side-by-side sheet) and `sync_gallery` (a second,
hand-rolled copy of the row parsing and the rendering) are gone with them.

`build_views` is five tianwen verbs per master, in this order, which is the heart of the skill:

```
tianwen image autocrop <master> -o crop.fits --margin 0.02
tianwen solve crop.fits --update-fits                      # only if it arrives unsolved
tianwen image render crop.fits -o raw.png                  # prints the white balance it solved
tianwen image sharpen crop.fits -o sharp.fits --ai-backend rc
tianwen image render sharp.fits -o enh.png --white-balance <the triple raw.png printed>
```

**THE ORDER IS LOAD-BEARING: the raw view is rendered FIRST because its white balance is what the
enhanced view inherits.** They are not two askings of one question. The enhance has already flattened
the background and pulled the noise down, so a second SPCC solve reads a frame the first never saw,
and its answer lands on top of a calibration the pixels already carry. That double correction is what
put an olive wash over cards whose raw half was fine. `image render` prints
`[render] white-balance R,G,B (SPCC)` and `--white-balance` takes exactly that back, which is the
same sharing `MasterPostProcessor` has always done across the split-plate TIFFs. Fed its own printed
triple, the render reproduces itself to 1 part in 65535 on 0.02 percent of pixels -- the rounding of
the six printed decimals and nothing else.

**Since the bake retains a coverage plane and a WCS, the first two are cheaper and better**: `autocrop`
reaches its exact tier from the `.rejection.fits` sidecar instead of estimating from the noise, and a
master that already carries a WCS needs no solve at all. Check `solved` and `coverage` on the rows
before assuming either step is still needed; a store baked before that change has neither.

**One PNG per view, and the publish happens in batches.** `<id>_enhanced.png`, `<id>_raw.png`,
`<id>_crop.png`. The 255-file cap is PER PUBLISH and files left out of a call are kept, so three views
of ninety masters take three calls rather than a sprite. The sprite this replaced forced both views
into one box at one width, which made the crop invisible: every card read as "before and after are the
same size" while the crop had taken 15 percent of the canvas.

**A row whose three PNGs all exist is skipped; `--force` redoes it.** The enhance is the expensive
stage, so re-running the script over a finished gallery costs nothing and a half-finished one costs
only what is missing. There is no longer a mode that redoes one half on its own: the halves have to
share a solve, so they are made together or not at all.

**One outdir. Two is how a gallery ends up showing a run nobody meant to publish.** Cards built from
a stale enhance folder look like a broken crop verb: no margin crop, no plate solve, and no way to
tell by eye. A file with no `CRPIX` did not come from this pipeline -- check that before measuring
anything else.

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

**One solve is enough, and now one WHITE BALANCE too**: `image sharpen` carries the WCS through to
its output, so the cropped file is solved once; the raw view's balance is then handed to the enhanced
view rather than re-fitted. The skill claimed the first half of this long before the second was true.

**render through the verb, never a private stretch.** This script used to carry its own numpy curve
(median - 2.8 MAD to black, midtones to 0.25). It washed backgrounds to light grey, blew the Orion
core to a white blob, and amplified a sub-sigma edge into a visible border. `image render` is
`MasterPreviewRenderer` + `StretchSolver`, the same path the GPU viewer and `tianwen stack`'s
`master_*.png` companion use, so a card and the app agree by construction.

## Composing and publishing

Three PNGs per card, each its own file, rendered at 1200 px. They are looked at to judge a crop edge
and a noise floor, and at the old 512 a 130 px border band on a 4108 px master was sixteen screen
pixels. PNG throughout: a JPEG puts its ringing exactly at the frame border, which is the thing being
inspected.

Publish with the Artifact tool from a folder the tool can read (`.artifact-gallery/` at the repo root,
git-ignored). Re-publishing the same file path keeps the URL. **Split the `files` map across calls of
at most 255 entries** -- the cap is per publish, and every file left out of a call is kept, so the
artifact ends up holding all of them.

## Rolling it out while the batch is still running

Publishing in parts is the point: ninety masters is over an hour of GPU, and a partial publish lets
the owner see the treatment early instead of approving it blind at the end. Render the rows whose
enhanced FITS exists, publish those files, and repeat; cards whose images are not in a call keep the
ones they have, so the gallery reads as mixed until the batch catches up.

## What to check before believing a card

- **Channel coverage.** A master with a channel at 0 percent finite is a failed integration, not a dim
  one; `IntegratedMaster.Labelled` refuses it now, but an older store can hold one (the eta Car
  ASI294MC master did, from a hot-pixel mask that flagged 100 percent of blue). **A fix does not
  rewrite files already baked**, so such a master answers `autocrop` with "the scan left nothing",
  which is correct -- every pixel is absent when a whole channel is NaN -- and the card has to be
  built from a re-bake of that session instead, pointing `build_views` at the store that holds it.
- **A flipped night is THREE masters and there are only TWO sides of a meridian.** The split (#45)
  writes the combined master over the whole night plus one per pier side, so the three cards share a
  target, a date and a train and look like duplicates. They are not, and the sub counts prove it: the
  Lobster Nebula night is 100 subs combined, 32 on one side and 68 on the other. `build_rows` reports
  the side as `flip` (`a` / `b` / `both` / null) and groups the set with `flipGroup`; the page badges
  every such card and offers a "whole nights" filter that hides the two halves.
- **A dual-narrowband master renders teal and that is the DATA, not the render (#51).** Under an
  L-Ultimate or L-eNhance the OSC frame carries Ha in red and OIII in BOTH green and blue, so the
  colour space is rank-deficient -- two real channels in three slots -- and SPCC, which is
  broadband-only, has no honest curve to fit. The cast is easy to misread as a render bug because it
  APPEARS at the enhance: unenhanced, the sky gradient and the noise floor cover the G-equals-B
  degeneracy, and flattening plus denoise takes away exactly the things that were hiding it. Check the
  row's `filter` before chasing it, and do not "fix" it in the renderer.
- **Measure, do not squint.** Background peak-to-peak per channel says whether the flatten worked;
  sigma-from-median at the frame edge says whether the crop did; channel means say whether the colour
  is calibrated. Every wrong conclusion in this pipeline's history came from judging a JPEG by eye.
- **A preview stretch exaggerates as the data improves.** After gradient correction the background is
  uniform to ~0.2 percent MAD, so a black point a fixed few MAD below the median sits under 1 percent
  down and anything dimmer clips. That is a property of the picture being good, not of it being wrong.
