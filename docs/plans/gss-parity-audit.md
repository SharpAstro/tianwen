# GSServer parity audit: fixes, ideas and limits worth porting

**Status:** audit complete for the pulse/slew/queue band; two real findings, one feature
(limits) split out into [`mount-safety-limits.md`](mount-safety-limits.md).

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
if (correction.HasRaCorrection)  { await _pulseTarget.PulseGuideAsync(raDir,  correction.RaPulseDuration,  ct); }
if (correction.HasDecCorrection) { await _pulseTarget.PulseGuideAsync(decDir, correction.DecPulseDuration, ct); }
```

This is GSS #76 ("Fix sequential execution of pulse guide call for equatorial mounts. Change
`AxisPulse()` to execute asynchronously and to support independent Ra and Dec pulses").

Both corrections are derived from ONE star measurement at ONE instant, and RA and Dec are
independent motors. Serialising them means the Dec pulse begins `raMs` after the measurement it
answers, and the pair costs `raMs + decMs` instead of `max(raMs, decMs)`. `MaxPulseMs` is 2000,
so the worst case is **4 s of pulsing per guide frame** - longer than a typical guide exposure,
at which point the loop computes each correction from a frame taken while the previous pair was
still moving the mount, and oscillates.

### Why it has never been seen, which is the more important half

**`PulseGuideAsync` has no stated completion contract, and its implementations disagree:**

| Implementation | Returns when |
|---|---|
| `SkywatcherMountDriverBase` | the pulse has **finished** (awaits `SleepAsync(duration)`) |
| `AscomTelescopeDriver` | the COM call returned - pulse still running |
| `AlpacaTelescopeDriver` | the HTTP PUT returned - pulse still running |
| `DALCameraDriver` (ST-4) | the relay is energised; a timer opens it later |
| `FakeMountDriver` | the correction is applied; a timer clears `IsPulseGuiding` later |

`IMountDriver.PulseGuideAsync`'s doc comment describes only the physical effect, so nothing
says which of these is right, and the divergence went unnoticed.

**The native SkyWatcher driver is the only one that blocks, and the fake is not one of them.**
So the serialisation is invisible to the entire functional suite - which drives
`FakeMountDriver`, returns immediately from both pulses, and therefore measures a loop that
behaves like the ASCOM path and unlike the hardware path it is meant to model. The stall
appears only on real SkyWatcher hardware. This is the test-double-diverges-from-the-real-thing
shape, and it is why guide tuning validated in tests need not transfer to a rig.

### The fix has two parts, and the contract is the load-bearing one

1. **State the contract on `IMountDriver.PulseGuideAsync`** and make every implementation meet
   it. The ASCOM semantics are the right target (return when the pulse has been COMMANDED;
   `IsPulseGuiding` carries progress) because four of five already do it, it is what the ASCOM
   spec says, and it is the only one that lets a caller overlap axes at all.
2. **Then issue both axes together in `GuideLoop`.** Only meaningful once (1) holds - against
   today's blocking SkyWatcher driver, issuing "concurrently" would still serialise.

Making the fake match the real driver rather than the other way round would preserve the bug
and hide it in a different place; the fake is already right.

**Do not simply fire-and-forget the two calls.** A dropped `ValueTask` loses exceptions and
cancellation, and `ValueTask` may not be awaited twice. Whatever the shape, a failed pulse must
still surface to the loop.

## Finding 2: an "in progress" flag that is not observable when the starter returns

`fb6e682` and `2c9cd16` are the same defect in two places: `SlewToCoordinatesAsync` returned
before `IsSlewing` was set, and `PulseGuide` returned before `IsPulseGuiding` was. A client that
starts the operation and immediately polls sees `false` and concludes it already finished.

The generalisable rule: **an asynchronous operation's progress flag must be observable before
the call that starts it returns.** Ours holds for pulses today only because the SkyWatcher
driver blocks - which is finding 1's problem. Once `PulseGuideAsync` returns at command time,
`_pulseGuideInFlight` must be incremented before the write and cleared by whatever ends the
pulse, or we will have ported GSS's bug in the act of fixing the other one.

Slews are worth a matching check against `_isSlewingRa` / `_isSlewingDec` for the same property.

## Not audited

Everything outside the pulse/slew/queue band: alignment models, AltAz rate prediction (already
referenced by [`altaz-mount-support.md`](altaz-mount-support.md)), pPEC, autohome sensors and the
advanced (`X`) command set.

## Related

`gss-oracle-transcripts.json` was generated from the stale tree, so it pins GSS's **pre-fix**
behaviour for anything in the table above. Regenerating against `origin/master` may legitimately
turn `SkywatcherGssOracleTests` red where GSS has since changed; that is a decision, not a
regression, and the transcripts should record which upstream revision produced them.
