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

### Blocking is inherent to the Synta pulse, not a design slip

**The motor boards have no "pulse for N ms" primitive.** An RA pulse changes the step period (`:I1`)
and the driver must send the restore itself; a Dec pulse starts the axis (`:J2`) and the driver must
send the `:K2`. So SOMETHING has to hold the duration. Today that something is the caller's own
await:

| Implementation | `PulseGuideAsync` returns when |
|---|---|
| `SkywatcherMountDriverBase` | the pulse has **finished** and the rate has been restored |
| `FakeSkywatcherMountDriver` | same -- it inherits the base and overrides nothing |
| `AscomTelescopeDriver` | the COM call returned - pulse still running |
| `AlpacaTelescopeDriver` | the HTTP PUT returned - pulse still running |
| `DALCameraDriver` (ST-4) | the relay is energised; a timer opens it later |
| `FakeMountDriver` | the correction is applied; a timer clears `IsPulseGuiding` later |

`IMountDriver.PulseGuideAsync`'s doc comment describes only the physical effect, so nothing says
which of these is right, and the divergence went unnoticed. **The two fakes are on opposite sides of
it**, which is worth knowing before writing a test against either.

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
