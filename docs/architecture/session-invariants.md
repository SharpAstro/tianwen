# Session invariants

Paragraphs moved out of CLAUDE.md that have no other owning document. Each is a rule that bites.

## Moved from CLAUDE.md, 2026-09-29

**Session failure surfacing (`ISession.FailureReason`):** when a run ends `SessionPhase.Failed`, the
session carries a plain-language, user-actionable reason (which device to check, what to do), surfaced
verbatim by the GUI notification feed, the hosted `/state` endpoint (`SessionStateDto.FailureReason`)
and the CLI. Throw `SessionFailedException(userMessage, inner)` for failures with a clear user
explanation (the inner exception carries the technical cause to the log); anything unhandled falls to
the generic catch ("Unexpected error: …"). Init device connects go through `ConnectOrFailAsync`
(`Session.Lifecycle.cs`), which names the device + telescope and is **deliberately fail-fast** -- a
device that cannot connect at init makes the night pointless (a flip-flat we cannot open leaves the OTA
blind), so fail there rather than discover it at dawn. The END-of-session flat block is the opposite:
best-effort, so a flats failure after a successful night never flips the session to Failed. Pinned by
`SessionFailureReasonTests`.

## Moved from CLAUDE.md, 2026-10-09

CLAUDE.md keeps these as one-line rules; this is the full text, verbatim.

`Session` (`TianWen.Lib/Sequencing/Session.cs`) is the central orchestrator. **Single-mount /
multi-OTA invariant**: `Setup.Telescopes` is plural for dual-/triple-saddle rigs, but there is exactly
one `Setup.Mount`. All OTAs share pointing and the current target. Multi-OTA buys parallel capture
(per-OTA camera/filter wheel/focuser) and per-OTA focus/filter/baseline state. Any future "branch"
or "re-order" logic must operate on the OTA set as a single unit.

`RunAsync` workflow: `InitialisationAsync` → wait for twilight → `CoolCamerasToSetpointAsync` →
`InitialRoughFocusAsync` → `AutoFocusAllTelescopesAsync` → `CalibrateGuiderAsync` → `ObservationLoopAsync`.
See the class XML doc + the relevant `docs/plans/*.md` for details on each phase.

**Guider calibration pier-side invariant:** `CalibrateGuiderAsync` (`Session.Lifecycle.cs`) slews to
HA **−0.5h** (30 min *east* of the meridian, target still approaching transit) before calibrating, NOT
west. `HA = LST − RA`, so HA < 0 = east = *before* crossing. East keeps the GEM on its pre-flip pier
side for the whole calibration, so the learned Dec guide sense matches the side rising targets are
imaged on. Calibrating west (HA > 0) is past the flip boundary on the opposite pier side → inverted Dec
sense + ambiguous flip-edge → Dec runaway. Hemisphere-independent (only apparent left/right mirrors in
the south); pinned by a both-hemisphere `[Theory]` in `SessionLifecycleTests`.

`ObservationLoopAsync` waits until `ScheduledObservation.Start - ScheduledStartLeadTime` (default 3 min,
covering slew + center + guider settle) before slewing to each target, via `WaitForScheduledStartAsync`
(`Session.Timing.cs`), so the scheduler's altitude-optimised slot times are honored, on the same mount
clock (`GetMountUtcNowAsync`) as the loop condition. Same-Start / past-Start schedules (the hosted API
stamping `Start = now`, legacy callers, existing tests) short-circuit the wait and advance linearly.
Late starts proceed unclamped (the full `Duration` still runs); a lead-adjusted start beyond session end
skips the observation cleanly.

**Meridian-flip oscillation invariant:** `MeridianFlipDecision.DecideFlipAction` must be gated so the
imaging loop can never re-issue a flip it already performed. Two backstops, in order: `if (hasFlipped)
return Continue` (a per-observation flag set after a successful flip in `Session.Imaging.cs`), then
`if (pierSideChanged) return AlreadyFlipped`. The HA-zone switch only reaches `CommandFlip` when
`!alreadyOnCorrectSide`, where `alreadyOnCorrectSide` compares the current pier side against
`DestinationSideOfPierAsync(target)`. **Load-bearing on SkyWatcher**, whose pier side is the Dec encoder
(the MECHANICAL state, which tracking never changes), so an unflipped GEM tracking west keeps reporting
the state it was slewed in and a naive "flip when HA > 0" is trivially true forever -> stuck `Slewing`,
zero exposures. **Never re-introduce an HA-only flip check**; gate on the *destination* side + the
`hasFlipped` memory. Pinned by `MeridianFlipDecisionTests` + a `mountPort:"SkyWatcher"` loop test.

**No-astro-dark night-window fallback:** `SessionEndTimeAsync` (`Session.Timing.cs`) derives the dark
window via `ObservationScheduler.CalculateNightWindow`, which has a fallback chain (astronomical −18° →
amateur-astro −15° → nautical −12° → polar-night 24h). It must **never** demand `EventTimes(...).Count == 1`
for astronomical twilight: at high-summer mid-latitudes (e.g. 50.9°N at solstice the sun bottoms ~−15.7°)
the sun never reaches −18°, and the old strict read threw, killing the session at a site that simply has
no astro-dark. Pinned by a no-dark German-solstice test in `SessionLifecycleTests`.
