# TODO -- Hardware Validation (the bench queue)

**The bench queue is GitHub issues labelled [`bench`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Abench)** since 2026-09-24, one issue per
check, each naming its gear, procedure, what to observe and what it validates. Filter by gear with a
search term (`label:bench SkyWatcher`). Until 2026-08-30 these checks were scattered across nine files;
from then until the migration this file held them, indexed by the GEAR that gates them.

**One home per item still holds, and the home is the issue.** A plan or architecture doc keeps the
design context (the *why*) and points at the issue; it never carries a second checkbox, because two
copies drift. Close the issue at the bench, and add the measured result to the owning doc if it
changes a decision.

What stays below is context a single issue cannot carry: what one bench session covers, and the work
that NEEDS gear but is not a validation.

## SkyWatcher / Synta mount (EQ6-class; any Synta board over serial or WiFi)

One indoor bench session covers items 1-9: no sky needed, only a mount that can sync, goto and be pulsed.
**None of these re-test the mount LIMIT logic**, which is validated three ways already (pure `MountLimitsTests`,
`SessionMountLimitTests` + `MountLimitWatcherTests`, the fake-SkyWatcher E2Es in `SessionObservationLoopTests`,
and the live GUI run of 2026-08-30). They test the DRIVER'S MODEL OF THE MOUNT that feeds the limit: that a
real Synta board lands where `SkyToSteps` says, that the Dec-encoder half means what `GetSideOfPierAsync`
reports, that the axis angle is the counterweight's real elevation. The limit is only as right as the state
under it, and the fake asserts that state by construction.

## Gated on gear but NOT validations (tracked in their own backlogs)

- ZWO + QHY native raw video (Phase D) and Canon Live View (Phase E) --
  [planetary-native-video.md](../plans/planetary-native-video.md).
- `train-guide-model` CLI (records N real worm cycles as the teacher signal) -- `TODO.md`.
- QHY294 gain-1600 dark library for the denoiser dataset -- `imaging.md`.
- Three nights with `SessionConfiguration.SaveIntermediates` on, on both main rigs (ASI533 + Samyang,
  SV605CC + SH61), so the deconvolver's real-defocus validation has ladders; none exist as of
  2026-09-02 -- `docs/plans/deconvolver-training.md` H6 / E6. **It costs nothing but disk: the frames
  are taken either way, the switch just keeps them.** What E1 added (2026-09-06) is what makes a night
  COUNT rather than merely happen: the oracle recovers a blur fully only to about 1.3x the anchor's own
  FWHM and is outside 10 percent past 1.6x, so the rungs that decide the advertised range are the ones
  landing between roughly 1.1x and 2x the anchor width. A ladder whose rungs all sit under 1.1x
  measures nothing, and one that jumps straight past 2x measures a regime nothing can solve. Check the
  step size against the rig's CFZ before the night, not after, and keep the anchor frame: every number
  H6 wants is a rung compared to it.
- **A third broadband night** (any rig, any broadband or light-pollution filter), because the denoiser
  pool is otherwise entirely narrowband and H4 step 2 rests on two SV545 `IDAS LPS-D3` sessions --
  `docs/plans/denoiser-training.md` H4 and its open question, which currently answers itself with
  "probably none, step 2 is a pilot until a third night exists".
- **Any mono session at all.** Both in-house models are one-shot-colour by DATA, not by design:
  `N2nDenoiser` refuses a mono input outright rather than tiling it across three slots, and the
  deconvolver plan carries the same "waits on mono data, as everywhere" line. One mono night does not
  make a mono model, but it is the difference between a measured refusal and an untested one.
- **Nights on the under-represented (train, filter) pairs**, because the PSF draw the deconvolver's
  exporter is being calibrated on is thinner than it looks: 10 of 17 cells in the 79-session bake hold
  ONE session and two cells carry 43 of the 79 (E0, 2026-09-06). A per-cell (FWHM, beta) distribution
  is only supportable for the two or three populated cells and everything else falls back per train --
  `docs/plans/deconvolver-training.md`, E0's results.
- One recording night for the neural guider (`train-guide-model`, open-loop worm cycles + closed-loop
  P), then one guided night with the model admitted -- `docs/plans/neural-guider-training.md` N7.
- QHY, and any other vendor, active-region support: phases P1-P5 of
  [sensor-active-area.md](../plans/sensor-active-area.md), deferred 2026-09-07 until a real body is
  connected. Overscan is model-dependent and only the SDK knows the geometry, so the connect-time
  `GetQHYCCDEffectiveArea` / `GetQHYCCDOverScanArea` / `CAM_IGNOREOVERSCAN_INTERFACE` query measures
  nothing without a camera to answer it, and that one answer decides the scope of everything after
  it; a body that honours `CAM_IGNOREOVERSCAN_INTERFACE` needs no crop from us at all. Canon (P0)
  shipped without hardware only because a raw file carries its own `SensorInfo` rect.
  First measurement when a body is attached: one LONG dark (QHY illustrate with 300 s), then look at
  both margins. Hot pixels run through the optic black strip and not through the overscan strip,
  which identifies which edge is which and which of the two may be used as the bias reference.
- Seed ZWO EAF `MaxStep` from hardware during discovery -- `TODO.md` (item 18 above is its confirmation).
