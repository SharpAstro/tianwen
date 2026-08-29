# GSServer parity audit: fixes, ideas and limits worth porting

**Status:** audit complete for the pulse/slew/queue band; three real findings (one already
fixed, Finding 3), one feature (limits) split out into [`mount-safety-limits.md`](mount-safety-limits.md).

GSServer (GSS, GPL-3.0, `rmorgan001/GSServer`) is the reference open-source Synta driver and
the oracle TianWen's SkyWatcher protocol implementation was written against. It has kept
fixing bugs since; this walks the commits that touch code we ported, says which apply, and
records the ones that do not so the same commit is not re-read next year.

**Read `origin/master`, not a local checkout.** The clone at `sebgod/GSServer` was 208 commits
behind when this audit started, and `SkyServer.cs` / `SkySettings.cs` alone differ by
+4599/-1534 lines across that gap. An earlier pass off the stale tree concluded "GSS has no
horizon limit", which is wrong on current upstream. Every verdict below is against
`origin/master` at `a2a4d09`.

## Verdicts

| GSS commit | Subject | Applies? |
|---|---|---|
| #89 | Fix RA pulse guiding for GEM mounts | **No** - immune by construction, now structurally |
| `2c9cd16` (#118) | Pulse guide wait | **No** - no queue between caller and wire |
| `765bd26` | Pulse duration loops | **No** - we never had the latency compensation |
| `7441ccd` (#76) | Pulse guide async dual axis | **YES - finding 1** |
| `fb6e682` (#109) | Slewing race condition | **Partly - see finding 2** |
| `6e6dba9` | Rollover / rollunder test support | **No** as a fix; raises a question we should answer |
| `f70d821` (#116) | Queue race condition | **No** - we have no command queue |

### #89 - RA pulse guiding on GEM mounts. Not applicable.

An RA pulse OFFSETS sidereal rather than replacing it, so it commands `(1 +/- f) x sidereal`
and changes only the step period (`:I`) while the axis keeps running. `:I` sets magnitude;
direction lives in the motion mode (`:G`), which a live pulse does not touch. GSS takes its
rate as an arbitrary `double` from ASCOM, so an East pulse could compute a negative combined
rate, which needs the axis to REVERSE - and `:I` alone runs it at the right speed in the
wrong direction. GSS fixed it by stopping the axis, re-issuing `:G` with a flipped direction
bit, and restarting.

We are immune because our rate is bounded at `1.0x`. That used to be an `int` lookup plus a
comment; it is now `SkywatcherGuideRate`, whose `EastRateFactor` cannot go negative across a
closed five-member set, checked by enumerating it. **Reintroducing an unbounded rate
reintroduces GSS's bug**, which is why `Nearest()` is the only way in from a `double`.

### `2c9cd16` - pulse guide wait. Not applicable.

GSS queues pulse commands on a background `SkyQueue` thread, so ASCOM `PulseGuide` returned
before the hardware had been told anything. Two consequences it fixed: `IsPulseGuiding` was
set by the ASCOM layer rather than at the point of dispatch (so it could be true before, or
false after, the real thing), and the caller could not know when the pulse actually began.
The fix threads a `ManualResetEventSlim` down to the wire and blocks the ASCOM call on it
with a 5 s timeout.

We have no queue: `SendCommandAsync` writes and reads the ack inline on the caller's own path,
and the duration timer starts AFTER that write returns. Both halves of the fix are structural
here.

### `765bd26` - pulse duration loops. Not applicable, and worth not re-inventing.

GSS used to subtract the measured command latency from the pulse duration:

```csharp
var raSpan = duration - (Now - LastI1RunTime);
if (raSpan > 0 && raSpan < duration)   // <-- the trap
{
    while (sw1.Elapsed < raSpan) { Thread.Sleep(10); }
}
```

The guard skips the wait entirely at BOTH ends: a stale `LastI1RunTime` makes `raSpan <= 0`,
and a fresh one makes `raSpan >= duration`. Either way the loop never runs and **the pulse is
zero length** - `:I` is sent at the guide rate and the rate is restored immediately. GSS
deleted the compensation and now waits the full `duration`.

We already wait the full duration from after the write. **Do not add latency compensation**:
it buys a few ms of accuracy and costs a silent no-op pulse whenever the measurement is off.

### `6e6dba9` - rollover/rollunder. Not a fix, but check the question it asks.

This adds a `raw` flag to the position read and a `SetAxisPositionCounter` so a test can PLACE
an axis near the 24-bit wrap. Note GSS's setter has `//steps += 0x800000;` commented out while
its getter subtracts the offset by default - asymmetric, which is presumably what the harness
was built to explore.

Ours is symmetric (`EncodePosition` adds `POSITION_OFFSET`, `DecodePosition` subtracts it), so
the specific asymmetry does not exist here. **Open question we have not answered:** what our
`j`/`E` pair does when an axis genuinely crosses the 24-bit boundary. `DecodePosition` returns
`[-0x800000, 0x7FFFFF]`, so a crossing decodes as a +/-16777216-step jump. At an EQ6's ~9.02M
steps/rev the counter holds about +/-0.93 revolution from home, which a long unattended session
can plausibly reach. Filed, not fixed - it needs a decision about whether we track the wrap or
re-home.

### `f70d821` - queue race. Not applicable.

Same reason as `2c9cd16`: the race is between GSS's queue consumer and its producers -- a results
dictionary keyed by command id, where a very fast command could complete before its caller had
registered a wait handle. We serialise per command on the port lock and have no queue, so the whole
mechanism is absent.

One IDEA in it survives the verdict, though: the same commit adds `CommandQueueStatistics`, logged
when the queue stops. We have no queue to instrument, but the analogous measurement -- the
distribution of serial round-trip times per command -- is worth having for the same reason, and we
currently have none. A failing USB cable, a saturated port or a mount that has begun retrying
presents as "guiding got worse", and nothing in the logs separates those from seeing. Not filed as
work yet; noted so the idea is not lost with the commit it came in.

---

## Finding 1: RA and Dec guide pulses are issued SEQUENTIALLY

`GuideLoop` applies the RA correction and then the Dec correction, each awaited:

```csharp
if (correction.HasRaCorrection)  { await _pulseTarget.StartPulseGuideAsync(raDir,  correction.RaPulseDuration,  ct); }
if (correction.HasDecCorrection) { await _pulseTarget.StartPulseGuideAsync(decDir, correction.DecPulseDuration, ct); }
```

This is GSS #76 ("Fix sequential execution of pulse guide call for equatorial mounts. Change
`AxisPulse()` to execute asynchronously and to support independent Ra and Dec pulses").

Both corrections are derived from ONE star measurement at ONE instant, and RA and Dec are
independent motors. Serialising them means the Dec pulse begins `raMs` after the measurement it
answers, and the pair costs `raMs + decMs` instead of `max(raMs, decMs)`. `MaxPulseMs` is 2000,
so the worst case is **4 s of pulsing per guide frame** - longer than a typical guide exposure,
at which point the loop computes each correction from a frame taken while the previous pair was
still moving the mount, and oscillates.

### Correction: this is a SkyWatcher-only defect, and its mirror image is universal

Written first as though the loop serialised on every mount. It does not. `await` only waits where
the implementation blocks, and only `SkywatcherMountDriverBase` does -- ASCOM, Alpaca, the DAL ST-4
path and `FakeMountDriver` all return once commanded, so on those mounts **RA and Dec already
overlap today, by accident**. Two consequences, and the second is the one that matters:

- The 4 s worst case above is real, but only on Synta hardware (and the SkyWatcher fake).
- **On every OTHER mount the guide loop never waits for a pulse to finish before measuring the next
  frame.** Nothing in `GuideLoop` polls `IsPulseGuiding`; the await was doing that job by accident
  on one driver family and doing nothing everywhere else. That is a correctness bug in its own
  right, it exists right now, and it is what the shared `WaitForPulseCompleteAsync` in step 5 is
  actually for -- not the tidy-up this plan first called it.

So the two halves of step 5 are not one change: overlapping the axes is a SkyWatcher speedup, while
waiting for the pair to complete is a fix for everyone else. Neither substitutes for the other.

### Blocking is inherent to the Synta pulse, not a design slip

**The motor boards have no "pulse for N ms" primitive.** An RA pulse changes the step period (`:I1`)
and the driver must send the restore itself; a Dec pulse starts the axis (`:J2`) and the driver must
send the `:K2`. So SOMETHING has to hold the duration. Today that something is the caller's own
await:

| Implementation | `StartPulseGuideAsync` returns when |
|---|---|
| `SkywatcherMountDriverBase` | the pulse has **finished** and the rate has been restored |
| `FakeSkywatcherMountDriver` | same -- it inherits the base and overrides nothing |
| `AscomTelescopeDriver` | the COM call returned - pulse still running |
| `AlpacaTelescopeDriver` | the HTTP PUT returned - pulse still running |
| `DALCameraDriver` (ST-4) | the relay is energised; a timer opens it later |
| `FakeMountDriver` | the correction is applied; a timer clears `IsPulseGuiding` later |

`IMountDriver.PulseGuideAsync`'s doc comment described only the physical effect, so nothing said
which of these was right, and the divergence went unnoticed. **The two fakes are on opposite sides of
it**, which is worth knowing before writing a test against either. (Past tense: the method is
`StartPulseGuideAsync` now and the contract IS stated. The table above still describes what each
implementation actually does, which the rename did not change.)

So the fix is NOT "make the SkyWatcher driver return early" -- it cannot, without moving the wait
somewhere. It is GSS #76's actual shape: run the hold on its own task, return once the hardware has
been COMMANDED, and let `IsPulseGuiding` carry progress. That is the contract four of the six already
meet and the one the ASCOM spec states.

**That move is not free, and the cost is why this is filed rather than done.** Once the hold runs off
the caller's path, the RA restore (`:I1`) and the Dec stop (`:K2`) contend for the port lock with each
other and with every telemetry poll; a pulse that throws has no caller to throw to; and cancellation
has to reach a task nobody is awaiting. `_pulseGuideInFlight` must then be incremented BEFORE the
write and cleared by whatever ends the pulse -- see finding 2, which is the same defect waiting to be
introduced by this fix.

### Why the sequencing has never been seen

Two reasons, and neither is "the fake behaves differently from the driver" -- one of them does.

- **`GuideLoopTests` builds `FakeMountDriver` directly**, which returns once the pulse is commanded.
  The dedicated guide-loop suite therefore never blocks on a pulse at all, and the serialisation is
  genuinely absent there.
- **Where the blocking driver IS driven** (`FakeCameraMountCouplingTests`, on
  `FakeSkywatcherMountDriver`), `FakeTimeProviderWrapper.SleepAsync` **auto-advances fake time**. The
  serialisation happens exactly as it would on hardware, costs zero wall-clock, and nothing asserts on
  the fake time it consumed. So it is exercised and invisible at the same time.

A test that would catch it has to assert on **fake time traversed per guide frame**, not on wall
clock and not on the corrections themselves -- the same instrument that settled the
`DeviceOwnershipTests` starvation-vs-race question.

**Do not simply fire-and-forget the two calls.** A dropped `ValueTask` loses exceptions and
cancellation, and `ValueTask` may not be awaited twice. Whatever the shape, a failed pulse must
still surface to the loop.

### Decision: `StartPulseGuideAsync` -- the call STARTS a pulse, `IsPulseGuiding` says when it ended

A blocking contract was considered first and rejected. Recording why, because the blocking argument
is superficially attractive (structured concurrency, errors flowing to the caller) and it is wrong
here for reasons that only show up when you count the implementations.

**Six of the eight already start-and-return, and one of them was explicitly built for overlap.**
`MeadeLX200ProtocolMountDriverBase` sends `:Mgd####`, lets the MOUNT hold the duration, records
`_pulseGuideEndTicks` locally so `IsPulseGuidingAsync` can answer, and updates it under a CAS loop
commented *"Keep the latest end time (overlapping pulses)"*. That driver anticipated simultaneous
dual-axis pulses years before this audit noticed the question. `SkywatcherMountDriverBase` and the
fake that inherits it are the outliers, not the standard.

**A proxy driver cannot honestly block.** To make `AscomTelescopeDriver` or `AlpacaTelescopeDriver`
wait for completion, they would sleep the duration locally -- an approximation of something the remote
driver actually knows. `IsPulseGuiding` on that driver observes the truth. Forcing a blocking contract
would replace a real observation with a local guess in exactly the drivers that have a real one.

**The dual-axis objection to the async shape does not survive contact.** The argument was that
`IsPulseGuiding` is one flag for both axes, so a caller cannot ask which axis finished. True, and
irrelevant: nothing wants to know. The guide loop wants "no pulse is still running", which is exactly
what one flag answers, and calibration pulses one axis at a time. Per-axis waiting would only matter
to a caller that reacts to one axis landing before the other, and there is none.

**The 1.9x calibration cost is not evidence for either contract.** It is what mixing two contracts
costs, and it disappears the moment ONE of them is stated -- under blocking because the await is the
wait, under start-and-return because the driver no longer double-charges the caller. It argues for
deciding, not for deciding a particular way.

**The rename is the load-bearing half, not the semantics.** `PulseGuideAsync` returning once the pulse
is *commanded* is ASCOM's contract and what most of the tree does, but the name says nothing, which is
how one driver came to block without anyone noticing. `StartPulseGuideAsync` makes a blocking
implementation obviously wrong at the point somebody writes it. That is worth more than any doc
comment on `PulseGuideAsync` would be.

**What this owes, and none of it is free:**

- **The SkyWatcher `:I` restore has no caller to throw to.** Start-and-return means a background
  hold, and a restore that fails leaves the axis at `(1 +/- f) x sidereal` **forever** -- the worst
  failure in this whole area. It needs a fault path that reaches the session, not just a log line.
  **Done, ahead of the rename: see Finding 3.** The claim first written here -- that the blocking
  shape gets this right for free -- was wrong, and checking it is what turned up Finding 3.
- **Dual-axis is a CAPABILITY and ASCOM has no word for it.** There is `CanPulseGuide` and nothing
  about simultaneity, so a driver may legally throw on the second axis. SkyWatcher (independent
  motors), the ST-4 relay path (four separate lines) and LX200 (already overlapping) can; an arbitrary
  ASCOM driver is unknown. We need our own flag, and `GuideLoop` must fall back to sequential where it
  is false -- which means the sequential path stays, as the answer for mounts that require it rather
  than as an accident.
- **Cancellation has to reach a pulse nobody is awaiting**, and `_pulseGuideInFlight` must be
  incremented BEFORE the write (finding 2, which this change is the most likely way to introduce).
- **`WaitForPulseCompleteAsync` stays and moves.** It is the right shape for this contract -- coarse
  hop then fine convergence, landing near the true end without oversleeping or polling the whole
  duration -- and both calibration and the guide loop will want it, so it belongs somewhere shared
  rather than private to `GuiderCalibration`.

**The survey, which makes the change much smaller than it first looks.** Every implementation,
against the contract above:

| Implementation | Today | Under the contract |
|---|---|---|
| `MeadeLX200ProtocolMountDriverBase` | `:Mgd####`, mount holds the duration, local end-tick CAS'd for overlap | **already correct, and the model** |
| `AscomTelescopeDriver` / `AscomCameraDriver` | return after the COM call | already correct |
| `AlpacaTelescopeDriver` / `AlpacaCameraDriver` | return after the PUT | already correct |
| `DALCameraDriver` (ST-4) | energises the relay, a timer opens it | already correct |
| `FakeMountDriver` | applies the correction, a timer clears the flag | already correct |
| `FakeCameraDriver` (ST-4) | **delegates to the coupled mount** | follows whatever the mount does |
| `SgpMountDriverBase` | throws (serial pulse unsupported) | unaffected |
| `CanonCameraDriver` | no-op | unaffected |
| **`SkywatcherMountDriverBase`** | **blocks on `SleepAsync(duration)`** | **the only one that changes** |
| `FakeSkywatcherMountDriver` | inherits the above | follows automatically |

So this is a mechanical rename plus **one** driver conversion, and all of the risk is in that one --
which is also the driver where a lost `:I` restore is unrecoverable. It gets its own commit.

Note `FakeCameraDriver` awaits the coupled mount, so the ST-4 fake inherits the SkyWatcher fake's
blocking today; that is the path `FakeCameraMountCouplingTests` drives, and it converts for free.

**Work, in order:**

0. **DONE.** Verify the restore and stop commands, so the conversion has a fault path to preserve
   rather than one to invent under time pressure. Finding 3.
1. **DONE, and as TWO methods rather than one rename.** The plan as first written renamed
   `PulseGuideAsync` to `StartPulseGuideAsync` and stopped there, which would have left every caller
   holding a primitive whose documentation had to warn that *awaiting it is not waiting for the
   pulse*. A trap you document is still a trap. So:

   - **`StartPulseGuideAsync`** (`IMountDriver` / `ICameraDriver` / `IPulseGuideTarget`) is the
     primitive: commands the hardware and returns, `IsPulseGuiding` carries progress and must
     already be true by then. 82 references, 29 files, no behaviour change. This is what ASCOM
     specifies, and the only shape that lets a caller drive two axes at once.
   - **`PulseGuideAsync`** (`PulseGuideTargetExtensions`, on the internal guider surface) is the
     composite: start AND wait. This is what a caller almost always means.

   **The codebase had already written the composite eight times by hand** -- every
   `GuiderCalibration` pulse was `StartPulseGuideAsync` followed on the very next line by
   `WaitForPulseCompleteAsync` with the same duration. Those eight pairs are now eight single calls
   and the private helper moved to the extension, where the guide loop can reach it too. Net -34
   lines in `GuiderCalibration`, zero behaviour change.

   **Why the composite is an extension, and on the guider surface only.** A driver has nothing to
   contribute to it -- it is entirely expressible as the primitive plus `IsPulseGuidingAsync` -- so
   letting drivers implement it is a chance to get it subtly different, not an opportunity. And the
   callers who want waiting are the guide loop and the calibration routine; the Alpaca device plane
   and the planetary recenter nudge genuinely want start-and-return, and handing them a same-named
   blocking overload to trip over buys nothing.

   `SkywatcherMountDriverBase` now openly VIOLATES the primitive's contract and says so in its own
   doc comment -- the point of renaming before converting: one visible violation in one named place
   beats a disagreement nobody can see. The Alpaca wire name is a string literal (`"pulseguide"` in
   `AlpacaMembers.cs`) and is untouched.
2. **DONE as part of 1.** Hoist `WaitForPulseCompleteAsync` out of `GuiderCalibration` -- it is the
   waiting half of the composite, so it moved there rather than to a helper class.
3. Give the composite a **two-axis overload** and add the simultaneous-dual-axis capability
   (`CanPulseGuideSimultaneously`) behind it, answered per driver. **The branch belongs INSIDE the
   composite, not in `GuideLoop`**: start both and wait once where the mount allows it, else start
   RA, wait, start Dec, wait. The plan previously had `GuideLoop` keeping "a sequential fallback for
   mounts that need it", which is caller-side branching for a fact only the driver knows.
4. `GuideLoop` calls that overload once with both corrections, replacing the two bare awaits. **This
   is a bug fix, not the speedup** -- see the correction under finding 1: on every mount family
   except Synta the loop currently never waits for a pulse at all, and the composite is what fixes
   that. The overlap is the SkyWatcher half of the same change.
5. Convert `SkywatcherMountDriverBase` to a background hold. Moved LAST deliberately: it is the only
   implementation that violates the primitive's contract, it is the whole risk of the sequence, and
   with steps 3-4 already in place the guide loop is waiting properly before the driver stops doing
   the waiting for it. Reversing that order would leave a window where nothing waits on any mount.
6. Pin with **fake time traversed per guide frame** -- under `FakeTimeProviderWrapper` the difference
   is free in wall time, so a wall-clock assertion cannot see it either way.

## Finding 2: an "in progress" flag that is not observable when the starter returns

`fb6e682` and `2c9cd16` are the same defect in two places: `SlewToCoordinatesAsync` returned
before `IsSlewing` was set, and `PulseGuide` returned before `IsPulseGuiding` was. A client that
starts the operation and immediately polls sees `false` and concludes it already finished.

The generalisable rule: **an asynchronous operation's progress flag must be observable before
the call that starts it returns.** Ours holds for pulses today only because the SkyWatcher
driver blocks - which is finding 1's problem. Once `StartPulseGuideAsync` returns at command time,
`_pulseGuideInFlight` must be incremented before the write and cleared by whatever ends the
pulse, or we will have ported GSS's bug in the act of fixing the other one.

Slews are worth a matching check against `_isSlewingRa` / `_isSlewingDec` for the same property.

## Finding 3: the commands that END a pulse were never verified. FIXED

Found while checking what the blocking shape actually guarantees, in order to preserve it through
the conversion. It guarantees less than the bullet above claimed: `SendCommandAsync` threw only on a
**write** that returned false. A firmware refusal (`!2`) reached `LogWarning` and returned normally,
and a read timeout -- a null ack -- reached no code path whatsoever.

Three commands are affected, all in a pulse's `finally`, and they are the ones whose failure is not
self-correcting:

| Command | Path | What a lost one leaves behind |
|---|---|---|
| `:I1` sidereal | RA pulse while tracking | RA tracking at up to **2x sidereal** (or, after an East pulse at 1.0x, a thousandth of it) until something else sets the rate |
| `:K1` | RA pulse while NOT tracking | the RA axis running at the combined rate, nothing scheduled to stop it |
| `:K2` | Dec pulse | the Dec axis running, same |

**Every other command in the driver fails FORWARD** -- a lost `:G` loses a pulse, a lost `:J` loses a
move, and the guide loop re-issues a correction on its next frame. These three fail BACKWARD: the
mount is left running at a rate the driver believes it has already cancelled. That asymmetry is the
whole selection rule, and it is why making *all* commands fatal would be wrong rather than merely
thorough -- it would turn a recoverable hiccup into a stopped guider.

**The fix.** `SendCommandRawAsync` classifies the round-trip (`=` accepted / `!X` refused / null =
no answer, which is a timeout and a genuinely different fact); `SendCommandAsync` keeps the
best-effort behaviour for everything else; `SendCommandVerifiedAsync` retries three times and then
throws `SkywatcherDriverException`. Retrying is half the fix and not a detail: a single serial
hiccup must not end the night, so only an exhausted budget is a fault. No delay between attempts --
a refusal is instant and a timeout has already spent the port's.

**No new plumbing was needed to surface it**, which is worth knowing before anyone builds some: a
throw from `StartPulseGuideAsync` propagates out of the guide loop, `BuiltInGuiderDriver` turns it into a
`GuidingErrorEvent`, and `Session.ImagingLoopAsync` drains that queue, logs the reason by name and
restarts the guider. The chain already existed and had nothing being fed into it.

**One accepted consequence:** the restore runs in a `finally`, so on a cancelled pulse whose restore
also fails, the `SkywatcherDriverException` REPLACES the `OperationCanceledException`. That is the
right precedence -- "the axis is still running at the pulse rate" outranks "the pulse was cancelled",
and cancellation is usually shutdown, which is exactly when a runaway axis is worst. It costs
nothing on the normal path, because the restore only throws when it genuinely failed.

**Not fixed, same defect class, deliberately left:** `StopAxisAndWaitAsync` and
`DecPulseAsMicroGotoAsync` both poll for FullStop and, on timing out at 3.5 s, `LogWarning` and
return as though they had succeeded. An axis that will not stop is the same kind of news. They are
left alone because `StopAxisAndWaitAsync` has eight call sites including park and slew, so throwing
there is a wider behaviour change than this commit should carry -- and, unlike the three above, they
verify by OBSERVATION, which is the stronger check already.

Pinned by `SkywatcherPulseRestoreTests` (both failure modes, both axes, the retry, and the
best-effort commands staying best-effort), with the fault injected through
`FakeSkywatcherSerialDevice.InjectCommandFault` -- which faults a command BEFORE the state machine,
so a refused restore genuinely leaves the fake running at the pulse rate.

## Not audited

Everything outside the pulse/slew/queue band: alignment models, AltAz rate prediction (already
referenced by [`altaz-mount-support.md`](altaz-mount-support.md)), pPEC, autohome sensors and the
advanced (`X`) command set.

## Related

`gss-oracle-transcripts.json` was generated from the stale tree, so it pins GSS's **pre-fix**
behaviour for anything in the table above. Regenerating against `origin/master` may legitimately
turn `SkywatcherGssOracleTests` red where GSS has since changed; that is a decision, not a
regression, and the transcripts should record which upstream revision produced them.
