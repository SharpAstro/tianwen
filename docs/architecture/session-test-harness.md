# The session test harness: fake time, the pump, and the guider race

Moved here verbatim from `CLAUDE.md` ("Test Collections & Parallelism") on 2026-09-12; the rules
that bite stay there as one-liners. Two stories, each a day of misdiagnosis, kept whole because a
paraphrase of a race is how it comes back.

## The guider race that was called starvation for a day

**A fake-clock `SleepAsync` must throw on a cancelled token, exactly as the real one does, and a guider's
`StopCaptureAsync` must not return until its loop has exited.** `FakeTimeProviderWrapper.SleepAsync`
used to `Advance` and return whatever the token said, so a cancelled background loop ran on to its next
natural exit; and `FakeGuider` / `BuiltInGuiderDriver.StopCaptureAsync` cancelled their capture loop and
returned at once (cancelling is synchronous, the exit is not). Every target start is "stop guiding,
slew, start guiding", so the next loop began on the guide camera while the previous one was still
mid-frame: two consumers of one camera, one guide frame released twice (`ChannelBuffer: more releases
than refs`), the new loop's `GuideLoop` nulled by the old one's finally, and the session never saw its
first exposure complete. That is what `DeviceOwnershipTests.AFinishedRunGivesTheRigBack` was -- **a
race, not starvation** (6 of 9 failures in isolation on a quiet box, 0 of 10 after the fix). It was
called starvation for a day because every measurement had been taken under load; instrumenting the fake
clock (fake time traversed, per thread) is what settled it.

## The cooperative time pump, and why the budget bounds a stall rather than the run

**Cooperative time pump pattern** for tests that run session loops via `Task.Run`. Use
`FakeTimeProviderWrapper.PumpUntilCompletedAsync` and **always pass the progress probe** -- never
hand-roll the `while (pumped < budget) { Advance(); }` loop this used to show:
```csharp
ctx.TimeProvider.ExternalTimePump = true;
var loopTask = ctx.Track(Task.Run(async () => await ctx.Session.ImagingLoopAsync(...), ctx.Token));
await ctx.TimeProvider.PumpUntilCompletedAsync(loopTask, TimeSpan.FromHours(4),
    progress: () => ctx.Session.ImagingLoopTicks, cancellationToken: ct);
```

**No test chooses a step: each advance goes to the next instant something waits for** (#1122). That is the
earliest target of a parked `SleepAsync` or due time of a ONE-SHOT timer made on the clock (a fake camera's
exposure ending, a slew arriving, a `Task.Delay`), held between a finest step of 100 ms and a coarsest of 5 s
(`DefaultFinestStep`, `DefaultCoarsestStep`; a test passes its own only with a reason). A periodic timer is a
POLL and never sets the step: the fake mount's axis timer fires every 100 ms all night, and stepping to it cost
tens of thousands of advances per fake hour. Its callback still fires once per period inside the advance, and a
`PeriodicTimer` coalesces by design.

**An advance is cheap because nothing in it sleeps on a timer.** A parked sleep is released by the advance that
reaches it (`Advance` releases every due park) and the pump waits by YIELDING: a 1 ms `Task.Delay` measured about
11 ms on a win-arm64 laptop even with `WindowsTimerResolution` held, a parked sleep polled with one, and the pump
paid two per advance, about 22 ms. What replaced those delays, and why each is there:
- after an advance, the released sleeps leave their parks before the clock moves on, so a poll reads the instant
  it was released at;
- before an advance, the pool's queued work is picked up, so what a timer woke (the imaging loop's tick) runs;
- a 1 ms minimum dwell per advance, because nothing observable says a loop just started, or still being
  compiled, has reached its first wait: without it a healthy `PeriodicTimer` loop was overtaken by a whole
  budget of advances, in 18 ms, before it ticked once. The old delays were that dwell by accident.

Measured on the 52 pumped session tests (`SessionPhase`, `SessionImaging`, `SessionObservationLoop`,
`SessionFilter`, `SessionScoutAndProbe`), win-arm64, against the fixed-step pump the day before: **4m06 to
2m27**, 244.8 s to 145.7 s of test time, 48 of 52 faster. The two flip tests that got slower (2.2 to 4.8 s and
3.7 to 5.9 s) run their guider at its own 2 s cadence now, about 900 guide frames over 35 fake minutes where 5 s
steps allowed about 400; that is more simulation, not pump overhead (about 1.6 ms an advance). The coupled
meridian run in `SessionObservationLoopTests` went from 3m06 to 51 s.

**The budget bounds a STALL, not the run, and the probe is what makes that true.** Waiter pacing alone
is not enough: `WaiterCount` is global and a fake guider/camera is parked in `SleepAsync` more or less
permanently, so "is anyone waiting?" answers yes whether or not the driven loop has caught up -- while
the loop's own tick is a `PeriodicTimer`, which registers no waiter AND **coalesces**, dropping any tick
whose continuation is still queued. Every advance the loop does not observe is budget spent for nothing,
and how many there are is a property of the thread pool: measured on one machine, one test, nothing but
scheduling changing, a 30-minute observation cost **33 to 50 minutes** of budget. A CI runner four
collections deep is free to be an order worse, and then a healthy loop trips a fake-time cap that reads
exactly like a hang -- which is what `GivenCloudsRollingInWhenStarCountDropsThenConditionDetected` did
on 2026-09-02. With the probe the budget resets on progress, so a slow runner merely takes longer, while
a loop that has genuinely stopped still trips it. A real hang stays bounded by `[Fact(Timeout = ...)]`.
Pinned by `FakeTimePumpTests`, whose no-probe case is the old pump kept green as the shape of that
failure. The pump **throws** with the counters on give-up, so do not read a downstream
`IsCompleted.ShouldBeTrue` as the diagnosis.
