# Testing: the runner, the conventions and their reasons

What `CLAUDE.md` states in one line each, in full.

## Moved from CLAUDE.md, 2026-11-15

The text `CLAUDE.md` carried until 2026-11-15, where this document did not already hold it; `CLAUDE.md` keeps one line
per rule and points here.

### Build & Test Commands (from CLAUDE.md)

```bash
# All commands run from src/
dotnet build
dotnet test
dotnet test TianWen.Lib.Tests --filter "FullyQualifiedName~Catalog"
```

**Tests run on Microsoft.Testing.Platform (MTP), not VSTest.** xunit.v3 4.x dropped the VSTest
bridge and the .NET 10 SDK refuses it outright ("Testing with VSTest target is no longer
supported"), so the opt-in lives in `global.json` -- this repo's ONLY one, which also states the SDK
FLOOR the repo builds with (`rollForward: latestFeature`): read the version there, never from here. Each test project is an
`Exe`, and `Microsoft.NET.Test.Sdk` / `xunit.runner.visualstudio` / `coverlet.collector` are gone
(nothing ever collected coverage; `Microsoft.Testing.Extensions.CodeCoverage` is the MTP
equivalent if it is ever wanted). What changes at a call site:

| VSTest | Microsoft.Testing.Platform |
|---|---|
| `--logger "console;verbosity=detailed"` | `--output Detailed` |
| `--logger "trx;LogFileName=x.trx"` | `--report-trx --report-trx-filename x.trx` |
| `--blame-crash` | `--crashdump` |
| `--blame-hang --blame-hang-timeout 5min` | `--hangdump --hangdump-timeout 5min` |
| `Sequence_*.xml` (written on crash) | `<app>_<hash>_crash.sequence.log`, TSV, written AS THE RUN GOES and deleted when the run exits cleanly (so a crash, a hang AND a kill leave one, and a green run leaves none) |
| reading `<Counters total=` back out of the TRX to catch a filter that matched nothing | `--minimum-expected-tests N` |

**`--filter` survived unchanged, VSTest syntax and all**, as did `xunit.runner.json` and the
`[Collection]` / parallelism rules (`docs/architecture/session-test-harness.md`). A stale `--logger` or `--blame-*` fails the run with an
unknown option rather than collecting less, which is the good outcome. The dump and TRX options
come from `Microsoft.Testing.Extensions.CrashDump` / `.HangDump` / `.TrxReport`, referenced by
`TianWen.Lib.Tests` and `.Functional` only, so they do not exist on the other two suites.

### Testing Conventions (from CLAUDE.md)

- **xUnit v3** with `[Fact]` / `[Theory]` + `[InlineData]`; **Shouldly** for assertions; **NSubstitute** for mocks
- Test data: embedded resources in `Data/` subdirectories
- **Never use reflection in tests**: add an `internal` property/method instead (test project has `InternalsVisibleTo`)
- **A test's temporary folder comes from `TempFolders`**, never a bare `Directory.CreateTempSubdirectory`, which
  nothing removes (1,253 had piled up in `%TEMP%`, #1197). It is made under the tests' common root,
  `%TEMP%/TianWen.Lib.Tests/t`, deleted when disposed (a test class's field, disposed after each test), and anything
  a delete missed is swept after a day. A node harness owns its own (`NodeHarness.StartAsync(onItsSocket: true)`,
  `KeptNode`). A helper that names a folder after its test takes the test's `TempFolders` (`FitsFixture.CreateTempDir`,
  `TempStackingWorkspace`): a fixed parent of its own under `%TEMP%` is a folder nothing sweeps (#1323: about
  9,000 folders and 1.1 GB in a week). `TestTempFoldersTests` fails on a bare `CreateTempSubdirectory` in any test project. **Every test process points TMP, TEMP and TMPDIR at a run folder of its own**, `<original temp>/TianWen.Lib.Tests/r<8 hex>` (`TestRunTemp`, a module initializer linked into all four projects, #2083): a bare `Path.GetTempPath()` and every process a test starts land there, `TestOutputRun` deletes it when the run ends (read-only marks cleared) and the next run sweeps a dead run's (a live one holds `.run.lock`); a nested test host reuses it through `TIANWEN_TEST_RUN`. **`TempFolders.Root` and the dated output keep their roots under the ORIGINAL temp** (`TestRunTemp.Root`), never `GetTempPath()` after the redirect: kept output must outlive the run folder, and a node's socket path must stay under about 104 characters. A dated output folder (`SharedTestData.CreateTempTestOutputDir`) lives as long as its test (`TestOutput`), and `TIANWEN_KEEP_TEST_OUTPUT=1` keeps it.
- **A test that drives a real GPU goes in `TianWen.Lib.Tests.Gpu`, never the unit suite** (#1634): a fault inside a GPU
  driver cannot be caught in .NET and ends its whole process, and the unit host died twice on one in NVIDIA's Vulkan driver.
  The run shares ONE instance and device (`SharedGpuStack`); a fixture derives from `OffscreenGpuFixtureBase`, which resizes
  the one target per class, and the class is `[Collection(GpuCollection.Name)]`, or the fixture throws naming both holders.
- **A thread stress test runs off CI** (the owner, 2026-10-10; `StressTestGate.SkipOnCi`): one that hammers an object
  from several threads for wall-clock time measures the runner, and on a shared 4-core runner its loops held the thread
  pool and stalled the whole test process into the hang dump (#1385). Its loops take threads of their own
  (`TaskCreationOptions.LongRunning`), never pool workers. A deterministic test of a known interleaving runs on CI as any other.
- **A test class that calls `GuiTheme.Apply`, `ToggleNight` or `CycleTheme` is `[Collection(ThemeCollection.Name)]`** (#2169): the
  theme is process-static and its `State` and `PaletteGeneration` feed the sprite keys, Night tints and palette colours of every
  render test, and a `[Collection]` alone does not stop other collections running beside it, so `ThemeCollection` is defined with
  `DisableParallelization = true`. The class puts the theme back in a `finally`; `ThemeCollectionSourceTests` fails one outside it.
- **A wall-clock latency bar is opt-in, and never widened** (#2031): it skips unless `TIANWEN_TIMING_TESTS=1` (`TimingTestGate`,
  never on CI), is `[Trait("Category", "Timing")]` and its class is in the non-parallel `TimingCollection`, so it measures alone.
  What the code DOES (the primitive `HighResolutionWait.Arm` chose, the order of a pulse's events) is pinned in the normal suite
  (`HighResolutionWaitTests`, `DALCameraPulseTests`).
- **A whole suite is CI's to run, not a workstation's** (#2345). The PR's checks run the unit suite in four shards of under
  10 minutes each; on one machine the same suite is 57 minutes of summed test time after a whole build, and on a node
  it holds pilot's one job slot for the length of it (a run of 2026-11-20 held it for over 35 minutes, for a PR that had
  already merged). Locally, build the test project and run the tests of the change by `--filter`. A whole local run
  is for when the owner asks for one, or for hunting an intermittent failure (the `test-run` skill).
- **What a test allocates is measured, not guessed**: `TIANWEN_TEST_COST_FILE` and `tools/test-cost-report.py`
  (`docs/architecture/ci-test-times.md`, "What the tests allocate"). Read bytes and collection counts; a time from a
  busy machine says nothing.
- **A test that takes 30 s or more on a CI runner is `[Trait("Category", "Heavy")]`** (the owner, 2026-10-10): the PR legs
  filter it out (`dotnet.yml`) and `heavy-tests.yml` runs it nightly against main and on dispatch; a local `dotnet test`
  runs everything. Tag by MEASUREMENT on a green x64 leg (its log prints each test's time), never by guess: 28 such tests
  were 46 % of the suite's summed time and held the x64 leg at 21 to 28 minutes.
- **Avoid duplication**: extract shared setup to helpers (e.g., `SessionTestHelper`)
- **A shared fixture plus an assertion about a FIRST write is an order dependency**, whether or not
  today's order satisfies it. `IClassFixture<T>` lives for the whole class, so its state carries from
  one test to the next: establish the precondition in the test (write a known-different shape, then
  the one under test) rather than inheriting whatever the previous test left. Test order is not a
  contract and it moved under us: `StretchUboChangeDetectionTests` passed for as long as an ALTERING
  test happened to run immediately before the one that needed it, and the xunit 3.2.2 to 4.x upgrade
  reordered the class and turned it red in Release only.
