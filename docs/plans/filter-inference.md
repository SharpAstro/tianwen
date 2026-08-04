# Filter Inference from Raw CFA Pixels

**Status: method validated on 48 sessions / 7,161 frames, tooling NOT committed.** The measurement
exists only as scratch scripts plus a provenance folder on `D:`, so a fresh checkout can reproduce
the *tagging* and none of the *inference*. This plan is the path from that state to committed,
re-runnable tooling. Companion to [astro-archive-survey.md](astro-archive-survey.md) (what is in the
archive) and [ai-denoise-deconv.md](ai-denoise-deconv.md) (why the archive needs to be labelled at
all: a training pool that mixes 3 nm dual-band with broadband is not one population).

## 1. The problem

**No FITS header in the archive names a filter.** Neither SharpCap nor N.I.N.A. wrote a `FILTER`
card for a filter screwed into the train, because nothing electronic knew it was there. Folder names
carry a hint on roughly a third of paths, and the hints are actively misleading in two ways the
owner had to point out:

- **`RGB` and `LUM` are processing modes, not filters** on an OSC sensor. A dual-band stack processed
  as RGB in PixInsight lands in a folder called `RGB`. Treating those tokens as filter evidence put
  4,533 frames of false evidence into the first pass.
- **Mono frames in an OSC archive are extracted, not captured.** 130 sessions / 5,942 frames have no
  `BAYERPAT` on a camera that has one, including two PIPP Moon runs of 3,059 and 2,000 frames. They
  are narrowband-extraction *outputs*, so their apparent "filter" is a channel name.

So the filter has to come from the pixels.

## 2. The mechanism, and what it can actually resolve

A filter's passband layout is imprinted on the **raw, undebayered** frame, because the Bayer matrix
samples it at three different spectral positions. The raw histogram is therefore multimodal with one
mode per CFA colour, and the *relative* mode positions encode the passband. Two measurements fall
out:

| measurement | what it is | what it separates |
|---|---|---|
| **sky rate** | background above bias, in e-/px/s, divided by airmass | passband *width*, to within a factor of about 2 |
| **background B/G** | blue over green background ratio | passband *layout*, which is the discriminating one |

**B/G is the discriminator; R/G is useless** (the populations overlap). Physically: a 3 nm Ha/OIII
dual-band drops OIII 500.7 mostly into G and leaves B nearly empty, while a quad-band's Hb window at
486 nm opens B up. Measured on this archive:

- **3 nm population: B/G = 0.5639 +/- 0.0099** over 44 sessions
- **L-Quad Enhance: 1.009 to 1.082**, which puts the nearest quad-band **43 standard deviations**
  off the 3 nm mean

**Honest resolution limits.** 3 nm versus broadband is a factor of 40 in sky rate, trivial. 3 nm
versus L-eNhance is about 6x, doable. **Two different dual-bands are not separable** by this method,
and neither are two quad-bands. Absolute sky rate alone is confounded by sky brightness, moon,
airmass and f-ratio, which is why the layout ratio carries the verdict and the rate is corroboration.

**The strongest single piece of evidence that the metric tracks the filter and not the night:** a
session under a 93% moon at +33 degrees altitude produced *less* background than a moonless night on
the same rig. Only a narrow passband does that.

## 3. Bias must be measured, never fitted

This is the load-bearing correction and the reason the first pass produced four wrong verdicts,
including flagging as "NOT 3 nm" a session sitting in a folder literally named
`Vela SNR P2a 240s L-Ultra -10d`.

The first pass fitted bias per frame from a photon-transfer relation. For one camera setting whose
true gain is 0.197 e-/ADU it returned gains from **0.119 to 0.344**, and on one frame a bias of
**-7177 ADU**. Replacing it with bias measured from the archive's own bias frames, keyed on
`(gain, offset)`, cut the within-population scatter **7.4x** (B/G spread 0.274 to 0.042, sd 0.073 to
0.010) and moved the L-Quad separation from "12x the largest internal gap" to 43 sd.

It also forced me to retract my own explanation of the data. I had reported B/G tracking sky rate at
r = 0.913 and read it as "B/G needs signal to be meaningful". That correlation was an artifact of
both quantities sharing the same bias error. With measured bias, r = +0.14. **A correlation between
two quantities derived from the same bad intermediate is not a finding.**

Reference values on this archive:

| gain | offset | R | G | B | n | note |
|---|---|---|---|---|---|---|
| 121 | 20 | 796 | 796 | 796 | 200 | grey |
| 252 | 20 | 780 | 780 | 780 | 100 | grey |
| 212 | 20 | 788 | 790 | 788 | 144 | grey |
| 121 | 13 | 649 | **516** | 649 | 100 | **not grey**: ZWO white balance was on in that era |

That last row is why the pipeline undoes white balance using the **bias frame's own channel ratios**
rather than assuming a grey pedestal. The folder names from that era record `78r 63b`, which
corroborates it independently.

Gain model, validated: `g = 0.7949 * 10^(-gain/200)` e-/ADU for the ASI533 under N.I.N.A.'s times-four
recording scale. It reproduces an independently fitted gain-252 value to **0.9%**.

## 4. Negative results, recorded so they are not retried

Four things I expected to work and which do not. Each is cheap to re-attempt and a waste of time.

1. **Star colour locus.** Prediction: narrowband tightens the stellar colour distribution. Measured
   the *opposite* (spread 0.194 for broadband LPS rising to 0.423 for 3 nm). Cause: SNR confound.
   Narrowband frames have noisier stars, and noise widens a colour locus faster than a passband
   narrows it.
2. **Channel lock** (are two channels correlated across stars). Fails even SNR-matched, because at
   matched *luminance* SNR a narrowband frame has flux in one channel and pure noise in the others,
   so there is nothing to correlate.
3. **Normalising sky rate for optics made it worse.** L-Quad appeared to pass *less* sky than
   broadband LPS after normalisation, because that particular frame sat at airmass 1.6, sun -19.4
   degrees, on the galactic plane. Per-frame geometry beats per-rig normalisation.
4. **Per-frame bias fitting**, per section 3.

## 5. What is already committed

The **write** end is done, tested, and was used for all 4,724 cards written in the reorganisation:

- **`tianwen dataset tag-filter`** (`src/TianWen.Cli/DatasetSubCommand.cs`): dry run by default,
  `--frame-type` defaulting to Light + Flat + DarkFlat, `--overwrite-existing` off so filling a blank
  is separated from overruling a value, and `--hard-links` defaulting to `Refuse` so a de-duplicated
  archive cannot be edited through one of its names by accident.
- **`FitsHeaderEditor`** (`src/TianWen.Lib/Imaging/Calibration/`): the write is never in place. It
  builds a temp file, verifies the pixel payload against the original, and only then `File.Replace`s
  with a backup.

The **inference** end does not exist in the repo at all.

## 6. Phasing

| Phase | Deliverable | Notes |
|---|---|---|
| **F1** | `dataset bias-library` | Scan an archive for `IMAGETYP=BIAS`, group on `(camera, gain, offset, temperature)`, emit per-channel medians + n to a JSON library. Per-channel, never grey-averaged, so the white-balance era is representable. This is the artifact everything else keys on. |
| **F2** | frame-scale detection | GCD of pixel values distinguishes N.I.N.A.'s times-four 14-to-16-bit scaling from an unscaled writer. Currently an assumption carried in a comment; SharpCap sessions were set aside wholesale because of it. Must be per-frame and reported, never inferred from `SWCREATE` alone. |
| **F3** | `dataset measure-filter` | Per session: sample frames from the middle of the run, resolve bias from the F1 library, compute sky rate in e-/px/s / airmass and background B/G, emit a row per session. Read-only, writes no headers. |
| **F4** | band derivation + classification | Cluster the F3 rows, or match them against a committed reference band table, and emit a proposed `FILTER` per session with the basis and the deviation. Feeds `tag-filter` as input, and must stay a *proposal* that a human locks in. |
| **F5** | `archive organize` | The reorganise-into-a-new-root tool: copy with verification, never write to the source, dedup hard-linked frames to one copy, file calibration by what it *is*. Already proven once at 5,750 files / 96.95 GiB with 0 failures; needs to become code. |

**F1 through F3 are the reusable core**; F4 is where judgement lives and should stay assisted rather
than automatic. F5 is independently useful and does not depend on F1 to F4.

## 7. Invariants for whoever builds this

- **Never write to the source archive.** The reorganisation model (copy to a new root, verify both
  sides, leave the original untouched) is not a safety dance to be optimised away. There is one copy
  of this data.
- **A session is one filter.** Every measurement here samples frames from the middle of a run and
  attributes the verdict to the whole session. That assumption is the owner's and it held on all 48
  sessions, but it is an assumption, so a per-session spread that exceeds the population sd is a
  signal to stop, not to average.
- **A folder name states what a frame is, never what it applies to.** Calibration-to-light
  association is many-to-many (one flat set serves several nights; one night draws on sets shot weeks
  apart), so it belongs in a map file. Filing flats under the *session* date left 10 of 18 session
  dates with no flats folder at all.
- **Calibration folders must key on temperature**, not just gain and offset. Without it a bias folder
  silently merged two sets taken seven months and 5 degrees C apart, and daylight dark-flats at +22 C
  hid inside a folder named `DARK`.
- **A whole-file hash cannot verify a tagged frame.** Tagging rewrites the header, so the file is
  *supposed* to differ. Only a pixel-payload comparison (read with `do_not_scale_image_data` so
  `BZERO`/`BSCALE` cannot mask a difference) proves the science data survived.

## 8. Known gaps

- **Groups C and beyond are unmeasured.** 16 SV605CC + SH61 EDPH sessions (also IMX533, so the bands
  transfer, but they need their own bias frames), the 18,354 frames with `TELESCOP='?'`, and the
  Newtonian's history (which appears exactly once, as `SWQ8`).
- **Askar D1/D2 and IDAS D3 have zero textual presence anywhere in the archive.** If they were used,
  nothing but pixels will say so, and section 2's limits mean a dual-band-versus-dual-band call is
  not available.
- **HIP 80609 (Rho Oph), 2026-04-21, L-Quad Enhance, has no usable dark.** Nearest match is 335 days
  and 4.9 C off at the wrong exposure. Needs a matching dark or dark scaling.
- **The times-four scaling claim in CLAUDE.md was established on the 14-bit ASI533 and may not hold
  for the 12-bit ASI585.** A 16-ADU comb is measurable in the data (53% of values on an exact 16-ADU
  grid against 6.25% expected flat), which fits the vendor SDK left-shifting RAW16 for a 12-bit
  sensor. That is inference, not measurement, so it wants a live capture to settle rather than a doc
  edit.
