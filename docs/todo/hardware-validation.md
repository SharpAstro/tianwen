# TODO -- Hardware Validation (the bench queue)

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

**What this file is.** Every check of SHIPPED code that only a real device (or a real night) can answer,
indexed by the GEAR it needs -- because that, not the code area, is what gates it. The other `docs/todo/*.md`
files are split by subsystem, which is right for desk work and useless for "what do I test while the EQ6 is in
the room": until 2026-08-30 these items were scattered across nine files.

**One home per item.** The checkbox lives HERE and only here. The owning plan or architecture doc keeps the
design context (the *why*) and points at this file with one line; it does not carry a second checkbox, because
two copies drift (one ticked at the bench, the other open for months). Tick here, and add the measured result
to the owning doc's narrative if it changes a decision. Feature work that merely NEEDS gear (a new driver, a
data collection run) is not a validation: it stays in its own backlog and is listed at the end without a box.

Each item states: the gear, the procedure, what to observe, and what it validates.

## SkyWatcher / Synta mount (EQ6-class; any Synta board over serial or WiFi)

One indoor bench session covers items 1-9: no sky needed, only a mount that can sync, goto and be pulsed.
**None of these re-test the mount LIMIT logic**, which is validated three ways already (pure `MountLimitsTests`,
`SessionMountLimitTests` + `MountLimitWatcherTests`, the fake-SkyWatcher E2Es in `SessionObservationLoopTests`,
and the live GUI run of 2026-08-30). They test the DRIVER'S MODEL OF THE MOUNT that feeds the limit: that a
real Synta board lands where `SkyToSteps` says, that the Dec-encoder half means what `GetSideOfPierAsync`
reports, that the axis angle is the counterweight's real elevation. The limit is only as right as the state
under it, and the fake asserts that state by construction.

- [ ] **1. Axis-solution choice per goto** (`SkywatcherMountDriverBase.SkyToSteps(ra, dec, PointingState)`).
      Sync at home, goto a target EAST of the meridian, then one WEST. Observe: the eastern goto lands
      counterweight-DOWN (through-the-pole solution, Dec axis mirrored through home), the western one on the
      straight solution; `GetSideOfPierAsync` reports `ThroughThePole` east and `Normal` west, agreeing with
      where the counterweight actually is. Validates the GSServer `RaDecToAxesXy` port
      ([mount-safety-limits.md](../plans/mount-safety-limits.md), "the pointing state"; the fake executes
      whatever step targets it is given, so this is unverified by construction).
- [ ] **2. Forced flip** (`SetSideOfPierAsync`): from item 1's western target, force the other state. Observe:
      the mount swaps to the other axis solution for the SAME sky coordinates and reports the new pier side;
      pointing (RA/Dec read-back) unchanged within the goto tolerance.
- [ ] **3. Axis-angle tier** (`GetAxisAngleAsync(Primary)`): at several hour angles either side of the
      meridian, in both pointing states, compare `|angle| - 90` deg with the counterweight's real elevation
      above horizontal (a phone inclinometer on the bar is enough). Observe the sign convention holds in this
      hemisphere; repeat in the north if a northern mount is ever on the bench. Validates the mechanical tier
      of the mount limit (`MountLimitBasis.AxisAngle`).
- [ ] **4. Sidereal tracking after a GOTO.** Goto with tracking on. Observe whether the board auto-resumes
      sidereal tracking itself (the fake does; GSS does not rely on it). Low risk either way --
      `Session.EnsureTrackingAsync` re-asserts before focus/imaging -- but it decides whether the driver
      should send the tracking start itself after every goto.
- [ ] **5. Iterative goto refinement.** Time a few gotos with the wire trace on. Observe whether the 30 arcsec
      tolerance / 2-pass cap converges against the real motor ramp and stop-wait (EQMOD does the same
      multi-pass goto); tune `SlewToRaDecCoreAsync` if a real board needs a third pass or a longer settle.
- [ ] **6. Slew-start grace** (GSS finding 2): with the wire trace on, does a real controller report
      `running` late after `:J`, and for how long? The 2 s `SlewStartGrace` was sized from GSS's account and
      the fake's `slewStartLatencyMs` knob. Validates
      [gss-parity-audit.md](../plans/gss-parity-audit.md) finding 2.
- [ ] **7. RA pulse on a STOPPED mount** (`_raPulseOnStoppedAxis`): stop tracking, issue an RA guide pulse,
      poll `:f1`. Observe the status word during the pulse shows running + the tracking-mode bit exactly as
      the driver assumes (the mask exists because that signature is indistinguishable from tracking).
- [ ] **8. Verified restore commands** (`SendCommandVerifiedAsync` on `:I1`, `:K1`, `:K2`): pulse while
      tracking and read back the step period; unplug/replug the serial cable mid-pulse. Observe: an accepted
      restore acks `=`; after the hiccup the step period is back to sidereal (never left doubled -- the
      trailed-subframe failure this closed has only been reproduced synthetically), and a refusal `!X`
      surfaces as `SkywatcherDriverException`, not a log line.
- [ ] **9. Driver-enforced stop as a LIMIT EVENT** (P5, `Session.DetectDriverEnforcedStop`): configure a
      limit in the mount's own firmware/driver (GSServer or OnStep limits) and let it stop the mount during a
      fake-camera session. Observe two consecutive polls read not-slewing + tracking off, so the run ends as
      `MountLimitKind.DriverEnforced`, not as a device fault and not with `EnsureTrackingAsync` fighting it.
- [ ] **10. Dec backlash in pulse guiding** (GSS converts configured backlash steps into extra pulse time,
      capped +1000 ms so PHD2's 2 s return expectation holds). Measure a real mount's Dec lash with a
      reversal test before deciding whether to implement it; the built-in guider's calibration absorbs
      steady-state lash partially. (Moved from `drivers.md`.)

## ASCOM / Alpaca telescope and camera (any real remote driver, or the OmniSim)

- [ ] **11. GSS finding 2 for ASCOM/Alpaca**: `Slewing` / `IsPulseGuiding` / `ImageReady` timing on a real
      driver -- do they read true at once after the command, or late? LX200 and SkyWatcher are right by
      construction; these inherit whatever the remote driver does. Check against a live device or the
      Alpaca OmniSim via `TianWen.Lib.Tests.Simulators` (`TIANWEN_ALPACA_SIM`). Validates
      [gss-parity-audit.md](../plans/gss-parity-audit.md) finding 2, ASCOM/Alpaca half.
- [ ] **12. Axis rates** on a driver that reports them (`AlpacaTelescopeDriver.cs:315`,
      `AscomTelescopeDriver.cs:320` are the code items in `drivers.md`; this is the confirmation that the
      parsed rates match what the driver's own UI shows).

## LX200-protocol / OnStep mounts

- [ ] **13. `:Q#` also stops pulse guiding** (`MeadeLX200ProtocolMountDriverBase.cs:873`): pulse, send `:Q#`
      mid-pulse, observe the axis stops and `IsPulseGuidingAsync` clears. (Moved from `drivers.md`.)
- [ ] **14. OnStep axis angle**: OnStep exposes raw steps but its axis model is unstudied, so it stays on the
      hour-angle tier of the limit (`GetAxisAngleAsync` null). Read a real controller's step counts at home
      and at known hour angles to derive the model before implementing.
- [ ] **15. LX200 pointing state**: the LX200 base driver reports a COMPUTED state (`HA >= 0 -> Normal`,
      "the mount handles the flip"), which is why `TrustedPointingState` hands the limit `Unknown` there.
      Whether a given LX200 mount tracks past the meridian until the next goto is a per-mount fact: observe
      one, and if it reports its side (`:Gm#`) mark that driver `Measured`.

## Cameras

- [ ] **16. ZWO USB re-plug identity**: does `ZWOptical.SDK` re-enumerate the camera with the SAME device id
      after a physical re-plug? If not, reconnect must re-resolve through `IDeviceUriRegistry`, a bigger
      change than [driver-resilience.md](../plans/driver-resilience.md) describes -- gated there as "verify
      before sub-plan A merges".
- [ ] **17. Planetary auto-recenter mount-jog sign** (`FlipRa`/`FlipDec`, uncalibrated): on sky with a bright
      planet, enable the mount jog and watch one edge-blocked correction. Observe the nudge moves the disk
      TOWARD centre on each axis; if not, flip the flag for that mount. The cap bounds a wrong guess to a
      small mis-move. ([live-planetary-capture.md](../plans/live-planetary-capture.md), COM recenter.)
- [ ] **18. ZWO EAF `MaxStep`** reported by the real focuser during discovery (the seeding is a code item in
      `TODO.md`; this confirms the value against the EAF's own utility).

## Focusers and covers (native serial)

- [ ] **19. Gemini Focuser Pro** (rebadged myFocuserPro2; driver transcribed from the vendor source, never on
      a board): (a) the exact `:04#` firmware name -- recorded in probe metadata, tighten the matcher only if
      it is distinctive; (b) whether DTR-reset is required at all (the vendor gates it behind
      `ResetControllerOnConnect`; we default to assert + 2.2 s boot) and the real boot time; (c) whether
      Move/Halt are truly unacked -- if the board acks them, add a bounded drain to `SendAsync` mirroring the
      temp-comp toggle. ([gemini-focuser-pro-protocol.md](../architecture/gemini-focuser-pro-protocol.md).)
- [ ] **20. Pinned-verify on a DTR-only device**: with a Gemini FlatPanel pinned in a profile, run discovery
      and observe whether Stage 1 verifies it or it falls through to Stage 2 (the code fix -- isolate probes
      that need control lines -- is in `drivers.md`; this is the bench confirmation).

## Rotator

- [ ] **21. Any real rotator** through `AscomRotatorDriver` / `AlpacaRotatorDriver`: signed off against the
      ASCOM and OmniSim simulators only. Observe mechanical vs sky PA, `Reverse`, and the post-meridian-flip
      re-rotate preserving framing. ([rotator.md](../plans/rotator.md).)

## iOptron SkyGuider Pro

- [ ] **22. Handbox firmware patch feasibility** (STM32F103, same MCU as the iOptron SmartEQ): whether the
      standard iOptron serial protocol can be flashed to gain position reporting and goto. Needs the handbox
      and a debug probe. (Moved from `drivers.md`; the device-identity fallback stays there as a code item.)

## Needs a night rather than a device

- [ ] **23. Mount safety limits on a real GEM, end to end**: a RIG fact, not a code check -- that the
      threshold set for the longest tube / lowest Dec the rig images really clears the pier, and that the
      meridian limit warns and acts on the PRE-flip side and stays silent after a real flip (which follows
      from items 1-3 once the driver's state is trusted). The GUI half was verified live on the fake SkyWatcher
      2026-08-30 ([mount-safety-limits.md](../plans/mount-safety-limits.md), "Live verification").
- [ ] **24. Guider calibration slew at HA -0.5 h, Dec 0** against real obstructions: `Session.Lifecycle.cs`
      slews there before calibrating; the scout is OTA-only and runs after centering, so a tree at that
      pointing is found only by trying. (`sequencing.md` carries the "slew slightly off Dec 0" idea.)
- [ ] **25. An OVERSAMPLED frame, for the solver's detection-binning floor**: any light finer than
      1.5"/px (the 0.97"/px 9576x6388 polar preview is the canonical one), kept with its measured median
      FWHM. Every committed fixture is 2.87-5.97"/px, so **no test in the repo reaches the binning gate at
      all** and both halves of it are currently unmeasured: that a bin is proposed there, and whether
      `MinSampledFwhmPx` then vetoes it and costs the polar ramp its 5.5 s rung-1 budget. Observe the
      solver's `LastDetectionBinning` `(Proposed, Used)` and the wall clock of the detect stage. Validates
      [plate-solver-performance.md](../plans/plate-solver-performance.md) phase D's sampling floor -- and
      decides whether the budget has to be bought back with a central crop instead of a bin.

## Gated on gear but NOT validations (tracked in their own backlogs)

- ZWO + QHY native raw video (Phase D) and Canon Live View (Phase E) --
  [planetary-native-video.md](../plans/planetary-native-video.md).
- `train-guide-model` CLI (records N real worm cycles as the teacher signal) -- `TODO.md`.
- QHY294 gain-1600 dark library for the denoiser dataset -- `imaging.md`.
- Three nights with `SessionConfiguration.SaveIntermediates` on, on both main rigs (ASI533 + Samyang,
  SV605CC + SH61), so the deconvolver's real-defocus validation has ladders; none exist as of
  2026-09-02 -- `docs/plans/deconvolver-training.md` H6 / E6.
- One recording night for the neural guider (`train-guide-model`, open-loop worm cycles + closed-loop
  P), then one guided night with the model admitted -- `docs/plans/neural-guider-training.md` N7.
- Seed ZWO EAF `MaxStep` from hardware during discovery -- `TODO.md` (item 18 above is its confirmation).
