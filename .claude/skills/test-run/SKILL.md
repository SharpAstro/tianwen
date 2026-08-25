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

## FIRST: check there is head-room

**A run started on a loaded box produces a result you cannot use.** Red means nothing (it may be
starvation), and you will have spent the wall clock anyway. Check before launching, not after
puzzling over a failure.

```powershell
$os = Get-CimInstance Win32_OperatingSystem
$freeGB = [math]::Round($os.FreePhysicalMemory/1MB, 1)
# Any self-hosted test executable, from ANY repo -- xUnit v3 tests are their own .exe
$others = Get-Process | Where-Object { $_.Name -like '*Tests*' -or $_.Name -eq 'testhost' } |
          Select-Object Name, Id, @{n='WS_MB';e={[math]::Round($_.WorkingSet64/1MB)}}
"free physical: $freeGB GB"
if ($others) { "OTHER TEST HOSTS RUNNING:"; $others | Format-Table -AutoSize } else { "no other test hosts" }
```

**Do not start when:**

- another test host is running -- **from any repository**, and
- free physical memory is under ~6 GB on this 16 GB box (the unit suite peaks around 4-5 GB with
  `maxParallelThreads: 4`), or the pagefile is already carrying gigabytes.

**The cross-repo case is the one that catches people.** Another Claude session working in a different
repository runs its own suite on the same machine: a `PDF.Lib.Tests.exe --filter ".../PageVulkanRender/..."`
run in `drawboard/pdf-viewer` took ~6 GB of a 16 GB box and drove free memory to 0.9 GB with 4.9 GB of
pagefile in use. A process check for `TianWen.Lib.Tests.exe` sees none of that, reports "nothing
running", and the run proceeds into a machine that has no room -- which is how a session test that
passes cleanly gets read as a regression. Match on `*Tests*`, never on this repo's name.

When the box is busy: wait, and say so. Do not "just try it and see" -- that is how a contended red
gets promoted to a diagnosis.

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
complete, exactly one open. The name is all the dump gives you, and it is not a diagnosis: this
one was called starvation for a day (it drove a `Session` from outside `[Collection("Session")]`,
and every measurement had been taken on a loaded box), and it was a race -- a cancelled fake
guider loop that survived its cancellation and fought the next loop for one camera. It failed 6 of
9 runs **in isolation on a quiet machine**. Once the name is known, run that ONE test alone,
repeatedly, before believing any environmental story; then instrument (here: fake time traversed
per thread, and the session log sink wired into the test DI) rather than reason. **Any test that
calls `SessionTestHelper.CreateSessionAsync` belongs in that collection and wants
`[Fact(Timeout = ...)]`** -- a wedged run hangs instead of failing, which costs a five-minute
timeout and a multi-GB dump instead of one red test, and the bound is what made this nameable.

On CI the same artifacts are uploaded as `test-blame-<leg>`. The dump is large (4.8 GB
uncompressed in one case); the `Sequence_*.xml` beside it is ~2 MB and is usually all you need,
so download the artifact ZIP and extract just that rather than letting `gh run download`
expand the dumps.

## Checking whether a suite is still alive

xUnit v3 tests are **self-hosted executables**, so the process is `TianWen.Lib.Tests.exe` --
there is no `testhost`. Looking for `testhost` reports nothing and makes a healthy run look
dead, which is how two suites ended up running concurrently in one session.

```powershell
Get-Process | Where-Object { $_.Name -like '*Tests*' -or $_.Name -eq 'testhost' } |
  Select-Object Name, Id, @{n='WS_MB';e={[math]::Round($_.WorkingSet64/1MB)}}
```

Match on `*Tests*` rather than this project's name, for the same reason as the head-room check: the
process competing for the box is often another repository's.

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
