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

**The pump advances on its own thread, so a fake device that reads the clock twice for one event can see it move
in between.** The fake camera reported its start from one reading and armed the timer that ends the exposure from a
second; a step landing between the two put the end that step late, past the session's wait for the reported end and
its fifth of a tick of polling, and one fetch was given up on CI
(`GivenHighAltitudeTargetWhenImagingLoopThenHighUtilization`, `FetchesGivenUp` 1 against 0; #1414 made that check).
The camera now ends a due exposure itself whenever its frame or state is read, at the reported start plus the
duration, and changes its state and the exposure it belongs to under one lock. A new fake device states each event's
instant once and derives the rest from it; `FakeCameraExposureTests` moves the clock as the timer is armed.

## Test collections and parallelism in full (moved from CLAUDE.md, 2026-10-09)

CLAUDE.md keeps one line per rule; this is the section's full text as it stood, moved verbatim ("this file" in it is CLAUDE.md).

Tests grouped into `[Collection("X")]` by functional area. **Any test that drives a `Session` belongs in
`[Collection("Session")]`: the rule is about what a test DOES, not what it is CALLED**, and they run
sequentially so several sessions' concurrent `Task.Run` + `FakeTimeProvider` timer callbacks cannot
starve the pool. It used to be written as "all `Session*Tests`", and three classes drove real sessions
from outside every collection because their names did not match: `DeviceOwnershipTests`,
`SessionFaultCounterTests` and `SessionScoutClassifierTests`. If it calls
`SessionTestHelper.CreateSessionAsync`, it is a session test.

**A fake-clock `SleepAsync` must throw on a cancelled token, and a guider's `StopCaptureAsync` must not
return until its loop has exited**, or the next target's guide loop starts on a camera the previous one
hasn't released yet: `DeviceOwnershipTests.AFinishedRunGivesTheRigBack` was this, a race misdiagnosed
as starvation for a day. Full story: `docs/architecture/session-test-harness.md`.

**No wall-clock timeout inside a test**, a `CancellationTokenSource` or a `Stopwatch` budget alike;
use `[Fact(Timeout = ...)]` and wait on `TestContext.Current.CancellationToken`, which xunit cancels at
it (inner timeouts cause flakes). #940 was one: a 10 s budget inside a 60 s test ran out while a machine
stalled by other work had not yet scheduled a node's first journal write, 40 ms of work on a quiet one.
`NodeWait` (the functional tests) is the shape: bounded only by the test's timeout, it logs what it sees
as it changes, so a timeout says whether the code was stuck or starved. **A test that drives a whole run
needs that bound**: a wedged run hangs rather than fails, and an unbounded hang is a five-minute
`--hangdump` timeout plus a multi-GB dump instead of one red test.

**Under xunit 4.x that bound is reliable** with the default `parallelAlgorithm`, Conservative, which all
three suites use: 4.0.1's `IFactAttribute.Timeout` documentation calls timing and timeouts undefined only
under `Aggressive`. Older xunit documented them as undefined whenever parallelization was on, and this
file said so until #940. Never set `parallelAlgorithm: aggressive` without giving that up.

**A `Timeout` on a SYNCHRONOUS test does nothing at all** and the analyzer now says so
(`xUnit1069`): the framework can fail the test but cannot interrupt a body that never awaits. 36
such attributes were decoration and were removed in 9.0; if you add one, the test must reference
`TestContext.Current.CancellationToken` for it to mean anything.

**Less parallelism is faster here, and the config only counts if it is copied to the output.** All three
test projects carry an `xunit.runner.json` (`maxParallelThreads: 4`; Simulators pins 1 +
`parallelizeTestCollections: false`) **and** a matching
`<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />`. `TianWen.Lib.Tests`
had neither for a long time while this file claimed otherwise, so xUnit silently defaulted to the core
count and thrashed the box; adding both cut the suite from 8m45-12m to 7m46 **and made it green**:
contention was dominating. Never diagnose a slow suite by re-running it repeatedly: one run with a TRX
logger, then rank durations.

`SessionTestHelper` defaults to `FakeMountDriver`; pass `mountPort: "LX200"` or `"SkyWatcher"` only for
protocol-specific tests.

**Use the cooperative time pump** (`FakeTimeProviderWrapper.PumpUntilCompletedAsync`) for a session loop
run via `Task.Run`, **never choose a step** (each advance goes to the next parked sleep or one-shot timer,
#1122), and **always pass the progress probe**: the budget bounds a STALL, not the run,
because a `PeriodicTimer` tick coalesces and registers no waiter, so an unobserved advance is budget
spent for nothing (measured 33-50 minutes of budget for one 30-minute observation). Pattern, the
measurements and why a naive `while (pumped < budget) { Advance(); }` loop is wrong:
`docs/architecture/session-test-harness.md`.

**Never** use `SleepAsync(subExposure)` in a pump loop; it advances fake time even when the `Task.Run`
hasn't been scheduled yet, causing targets to "set" before imaging starts. `Advance` fires timers
synchronously; `Task.Delay(1)` yields to the thread pool.

## How much the misaligned-Skywatcher guider settle varies (#1910)

`GivenBuiltInGuiderWithMisalignedSkywatcherAndCameraPulseWhenCalibrateAndGuideThenStaysLockedWhileMountDrifts`
(`FakeCameraMountCouplingTests`) once settled at `TotalRMS=4.46" RaRMS=0.16" DecRMS=4.46"` on CI (run 40770669522,
x64 functional leg, PR #1873) against its 3" bound. #1910 asked how often that happens and why.

**Method.** A temporary `[Theory]` ran the unchanged scenario 400 times (two batches of 200) on win-x64, 16 cores,
Release, `maxParallelThreads: 4`. Each case printed the At-settle statistics, the hand-pump iterations, the fake
time pumped and the chosen guide star. Batch 1 ran with nothing else on the machine (23 min). Batch 2 ran while a CPU
burner was meant to keep the box busy, but the burner exited after about 26 of its 50 minutes, so only the first
part of that batch was loaded (23 min). The cases of one theory share a class and run one after another, so even
the loaded batch never had four of them contending. The theory is removed again and no seam was added or kept.

**Distribution of the At-settle RMS (arcsec).**

| Batch | Column | min | median | p95 | max | over 3" |
|---|---|---|---|---|---|---|
| 1, idle, 200 runs | Total | 0.021 | 0.158 | 0.212 | 0.244 | 0 |
| | Ra | 0.000 | 0.043 | 0.123 | 0.174 | 0 |
| | Dec | 0.003 | 0.141 | 0.197 | 0.215 | 0 |
| 2, partly loaded, 200 runs | Total | 0.012 | 0.062 | 0.186 | 1.987 | 0 |
| | Ra | 0.001 | 0.049 | 0.120 | 0.175 | 0 |
| | Dec | 0.002 | 0.029 | 0.162 | 1.987 | 0 |

**What was found.**

- The 4.46" settle did not reproduce in 400 runs. The worst run, 1.99" Dec (batch 2, run 36), is about 8 times the
  next worst Total of either batch (0.244") and 32 times batch 2's median, so the Dec distribution has a real tail,
  but it stays under the 3" bound.
- Suspect 1 (a fixed star field) is wrong as stated: the guide star the loop locks varies run to run (173 distinct
  positions in 200 idle runs, 138 in the batch 2 runs), clustering near (595, 490), (51, 270) and (198, 305). Odd-star
  runs settled as well as the rest, and the 1.99" run locked an ordinary star at (586.65, 480.91) with SNR 2710.
- Suspect 2 (the hand pump's real-time `Task.Delay(1)` yield) is not supported by cadence: the pump needed 434 to 468
  iterations and 108.5 to 117 s of fake time, and the 1.99" run sat in the middle (444 iterations, 111 s). Batch 2
  did not need more iterations than batch 1 (median 445 against 448). What was NOT tested is real four-way
  contention, since the burner died and the cases ran sequentially. The suspect is not cleared, only not shown.
- `GuideFrameCount` read 1 at settle in every run, so it carries no signal and was not used.
- Reading: the 4.46" CI settle looks like the far tail of a mostly tight but occasionally spiky Dec distribution
  on a loaded runner. The 3" bound sits well over ten times above the typical settle. Nothing measured ties a bad run
  to a frame count, a pump cadence or a star choice, so the cause of the tail itself is not established.

**For the fix leg.** Do not widen the bound to hide it. Drive the scenario with the cooperative
`PumpUntilCompletedAsync` plus the progress probe (the pump the rest of the suite uses, #1122), which removes the
dependence on a real-time yield. To judge any fix, repeat the scenario under real contention (several such tests at
once from separate classes) and read the Dec RMS tail, since an idle sequential batch cannot show it.

### What the fix changed, and what a loaded re-run showed (#1910)

**The hand pump is gone.** Every guide loop in `FakeCameraMountCouplingTests` (the calibration waits, the 175 s
sustained run, the fresh-model run, the dither settle, the cloud-fail wait, the lost-lock and recover waits) now goes
through `FakeTimeProviderWrapper.PumpUntilAsync`, a pump-until-condition overload beside `PumpUntilCompletedAsync`
that shares its body (`PumpCoreAsync`) and takes the same progress probe (`GuideFrameCount`). The test helpers are
`PumpGuiderAsync`, `PumpUntilGuidingAsync` and `PumpForAsync`. The two mount-only loops (the slew and advance checks)
keep their hand step on purpose: they have no guide loop to pace against and do not set `ExternalTimePump`.

**The first loaded repeat still failed, and showed the real mechanism.** With the cooperative pump and nothing else
changed, 120 runs of the target test under twelve CPU burners on win-x64 (16 cores) failed 2 times, both in the
sustained phase and not at settle: `TotalRMS` 5.17" and 5.93" against the 5" bound (At-settle 0.50" and 0.60", so
the 3" settle bound held). Both runs carry the same signature, one frame with a single RA excursion of 26.0" (a
`corrRA` of -859 ms in one) while Dec stayed near 0.35". That is about 1.7 s of sidereal drift, a guide tick that was
never observed, not a spread in the guider's own behaviour. The cause: the pump gave the pool and a released sleep
only `SettleBound` (2 ms) to settle before the next advance. The guide loop wakes on a `PeriodicTimer`, which
coalesces: a tick that fires while its continuation is still queued is dropped. On a saturated runner 2 ms is not
enough, so the pump advanced over the tick and the next frame saw the mount drift of both intervals at once.

**The change.** `PumpUntilAsync` takes an optional `settleBound`, used for both settle waits of an advance. The
guider pumps pass 25 ms (`GuideSettleBound`). It costs nothing on an idle runner (the wait ends as soon as the pool
is quiet) and only waits where the pool is backlogged. The default stays 2 ms for every other caller. No test bound
was widened: both failures were a skipped tick, not a distribution to fit a bound to.

**Unloaded repeat after the fix: 120 of 120 passed** (a temporary `[Theory]` of 120 runs of the target test, since
removed; about 7 minutes on an ordinary, unloaded run). That shows no regression and no new unloaded flake. It does
NOT show the fix works under load, because the unloaded runs never failed even before the fix.

**The loaded repeat was NOT run, and why.** The owner's rule is never to load the machine on purpose (no CPU burner,
no stress or soak loop), and the owner declined a request to make an exception. So this section makes no claim, from
measurement, that the fixed pump is clean under load. The reasoning for the loaded case: the settle wait ends as soon
as the thread pool is quiet, so a 25 ms bound costs nothing on an idle pool and adds waiting only where the pool is
backlogged, which is exactly the saturation that let a coalesced `PeriodicTimer` tick be dropped. A loaded repeat of
the target test (120 runs beside enough CPU burners to saturate the box) remains owed, and it is the one check that can
tell whether 25 ms is enough.
