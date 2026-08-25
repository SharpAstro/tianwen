---
name: test-run
description: Run a TianWen test suite so a failure can ALWAYS be identified afterwards, and hunt intermittent failures. Use whenever running a full suite, when a test failed but the name was not captured, or when the user says a test is flaky / intermittent / "it passed the second time". Captures a TRX, never truncates output, and diffs failure sets across repeats.
---

Usage: `/test-run [project] [--filter <pattern>] [--repeat N]`

Default project is `TianWen.Lib.Tests`. For a name pattern on a single run, `/test-filter`
is lighter; use this one when the run must leave evidence behind.

## The rule this skill exists for

**A test run you cannot attribute is a test run you have to do again.** A failure count with
no name tells you nothing: you cannot tell a real regression from a flake, and you cannot tell
today's flake from last week's. So every run writes a TRX and every run keeps its full output.

Three ways the evidence gets lost, all observed in this repo:

1. **Piping through `head`/`tail`.** `dotnet test ... | tail -4` keeps the summary line and
   discards the `[FAIL]` block with the test name, the assertion message and the stack. The
   summary is the one part you can reconstruct; the rest is gone.
2. **Building while a suite runs.** `TianWen.Lib.Tests` references `TianWen.Cli`, so a build
   copies `tianwen.dll` into the running test output directory, fails with MSB3027 *and* can
   disturb the run. Never build until the suite reports.
3. **A failed build followed by `--no-build`.** The tests then execute stale binaries and the
   result is meaningless. The tell is the TRX `total` not matching what you expect.

## Running

```bash
cd src
OUT="$SCRATCH/testrun-$(date +%H%M%S)"; mkdir -p "$OUT"
dotnet test TianWen.Lib.Tests -p:UseLocalSiblings=false --no-build   --logger "trx;LogFileName=run.trx"   --logger "console;verbosity=detailed"   --blame-hang --blame-hang-timeout 5min   --results-directory "$OUT" > "$OUT/console.log" 2>&1
```

Redirect to a file, never a pipe. `--blame-hang` matters locally too: without it a hung test
looks identical to a slow one and the run simply never ends.

## Reading the result -- from the TRX, not the console

```python
import glob, xml.etree.ElementTree as ET
NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
for path in glob.glob(OUT + '/*.trx'):
    r = ET.parse(path).getroot()
    c = r.find(f'{NS}ResultSummary/{NS}Counters')
    print(dict(c.attrib))
    for u in r.iter(f'{NS}UnitTestResult'):
        if u.get('outcome') == 'Failed':
            print('FAILED:', u.get('testName'))
            for m in u.iter(f'{NS}Message'):
                print('   ', (m.text or '')[:800])
```

Check `total` as well as `failed`: a filter that matches nothing exits 0, and a stale-binary
run reports a count that does not match the source.

## Hunting a flake

Run it repeatedly and compare the failure SETS, not the counts.

- **Failed once, passed on repeat, different name each time** -> environmental (contention,
  disk, thread-pool starvation). Look at what else was running.
- **Same name every few runs** -> a real intermittent test. Fix the test.
- **No `Failed!` line at all and the run just stops** -> a HANG, not a failure. See below.

Never conclude "flake" from a single clean re-run. Two clean repeats plus a named cause is
the bar; without a name, say so explicitly rather than implying it was diagnosed.

## When it hung rather than failed

`--blame-hang` writes `Sequence_*.xml` beside the dumps. The test still running is the one
that hung -- everything else is `Completed="True"`:

```python
r = ET.parse('Sequence_*.xml').getroot()
print([e.get('DisplayName') for e in r.findall('Test') if e.get('Completed') != 'True'])
```

That is how `DeviceOwnershipTests.AFinishedRunGivesTheRigBack` was identified: 4824 of 4825
complete, exactly one open. It drove a real `Session` from outside `[Collection("Session")]`
and starved. **Any test that calls `SessionTestHelper.CreateSessionAsync` belongs in that
collection and wants `[Fact(Timeout = ...)]`** -- starvation makes such a test hang instead of
fail, which costs a five-minute timeout and a multi-GB dump instead of one red test.

On CI the same artifacts are uploaded as `test-blame-<leg>`. The dump is large (4.8 GB
uncompressed in one case); the `Sequence_*.xml` beside it is ~2 MB and is usually all you need,
so download the artifact ZIP and extract just that rather than letting `gh run download`
expand the dumps.

## Checking whether a suite is still alive

xUnit v3 tests are **self-hosted executables**, so the process is `TianWen.Lib.Tests.exe` --
there is no `testhost`. Looking for `testhost` reports nothing and makes a healthy run look
dead, which is how two suites ended up running concurrently in one session.

```bash
tasklist //FI "IMAGENAME eq TianWen.Lib.Tests.exe"
```

## Never run the unit and functional suites at the same time

`TianWen.Lib.Tests` and `TianWen.Lib.Tests.Functional` must run **one at a time**, and so must
two copies of either. This is the single most reliable way to manufacture a failure that is not
real.

**Machine-specific, and stated as such:** this is a hard rule on the win-arm64 Adreno box, which
is where it has actually bitten. The suites are tuned for it -- `xunit.runner.json` pins
`maxParallelThreads: 4` against 12 cores precisely because defaulting to the core count thrashed
it, and cutting parallelism made the suite both faster *and* green. A machine with a lot more
cores may well take both at once; do not assume this box will.

The functional suite is the sensitive half: it drives session loops through `Task.Run` with
`FakeTimeProvider` pumps, so its correctness depends on timer callbacks being scheduled
promptly. Starve the thread pool and those fire late -- targets "set" before imaging starts,
waits expire early -- and the test fails for a reason that has nothing to do with the code.

The asymmetry is what makes contention so expensive to reason about: **a green run under
contention is still meaningful, a red one is not.** So a failure observed while something else
was running has to be re-run before it means anything, which is the whole run wasted.

## Never

- `dotnet build` while a suite runs (see the lock trap above).
- Concluding anything from a run whose build step errored.
- Starting a long stack / GUI run alongside a suite on this box, for the same reason.
