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
await ctx.TimeProvider.PumpUntilCompletedAsync(loopTask, TimeSpan.FromSeconds(5), TimeSpan.FromHours(4),
    progress: () => ctx.Session.ImagingLoopTicks, cancellationToken: ct);
```

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
