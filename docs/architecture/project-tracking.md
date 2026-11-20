# Project tracking, structure and skills

What `CLAUDE.md` states in one line each, in full.

## Moved from CLAUDE.md, 2026-11-15

The text `CLAUDE.md` carried until 2026-11-15, where this document did not already hold it; `CLAUDE.md` keeps one line
per rule and points here.

### Project Tracking Docs (from CLAUDE.md)

**The backlog is GitHub issues, not a file** (since 2026-09-24, when `TODO.md` and `docs/todo/*.md` were
migrated, 345 open items). Labels: `area:astrometry` / `drivers` / `guider` / `imaging` / `infra` /
`sequencing` / `ui`; `bench` (only a real device or night can answer it); `needs-triage` (the old inbox);
priority is the organisation's **Priority issue field** (Urgent, High, Medium, Low), never a label (the `priority:*` labels went 2026-11-11); `from-todo` marks the migration. **Close an item through the PR that
does it** (`Closes #n`), so it cannot drift from the code, which is the whole reason for the move: the
same task used to be kept in `TODO.md`, an area file, a plan and a tracking issue, and a PR landing
updated at most one of them. **Never write a new open `- [ ]` into `TODO.md` or `docs/todo/`**; open an
issue (`gh issue create --label area:...`). A plan keeps the design and the *why*, and names its issue.
**The link runs both ways, down to the paragraph** (user, 2026-09-25):

- An issue for a planned feature links the plan's SECTION: a heading anchor on `main`, such as
  `docs/plans/planetary-stacking.md#e-de-rotation-winjupos-style`.
- The plan names the issue at that section.
- A bold-lead paragraph cannot be linked, so give it a heading first.
- A plan's open item with no issue is untracked work, whatever its status table says.
- A plan with open work has a **milestone** of the same name, holding its issues. A new issue joins its plan's milestone, and **a PR carries the milestone of the plan it advances** (`gh pr create --milestone <plan>`). `tools/plan-issue-report.py` (the `plan-report` skill) checks all of it, and the `plan-report` workflow runs it `--strict` on every PR touching `docs/plans/` and weekly, so a renamed heading cannot leave an issue's link dead. What is happening is the repository's shared issue views and the Milestones page, not a generated report.

Canonical project state otherwise lives in these markdown files; read the relevant ones before starting
non-trivial work:

| File | Purpose |
|------|---------|
| `docs/plans/summary.md` | Current status of every plan in `docs/plans/` (DONE / PARTIAL / NOT STARTED) cross-checked against the codebase |
| `docs/plans/*.md` | Per-feature implementation plans with phasing tables |
| `docs/architecture/*.md` | Architecture deep-dives, one file per subject, where `CLAUDE.md` keeps only the rules that bite (`ls docs/architecture/` is the index) |
| `TODO.md`, `docs/todo/*.md` | The DONE archive, by area (the open items are issues now; see above). Kept because a done entry often records the measurement or the reason behind a decision |
| Issues labelled `bench` | The bench queue (was `docs/todo/hardware-validation.md`): every check only a real device or night can answer, one issue each, its gear in the body; a plan keeps the *why* and a pointer, never a second checkbox |
| `docs/known-limitations.md` | Root causes of limitations/bugs (the *why*); read before "fixing" a suspected bug |
| `CHANGELOG.md` | Released version history, newest first, one section per `MAJOR.MINOR`. A version bump adds its entry **in the same commit** (`/bump-version` step 6), so a number can never ship without its note. The GitHub Release lists the commits; this says what the release was FOR and what breaks |

**A doc never cites a commit by hash.** Every PR merges through the queue by squash (rebase before 2026-11-08), so a hash written into a plan or
TODO before its PR lands is rewritten on landing, and the 2026-04-22 LFS migration rewrote everything
before it; sixteen dangling hashes were found in the docs on 2026-09-06. Cite the commit SUBJECT in
quotes, a release tag (`v6.3.1352`), or a PR or issue number (`#227`), all of which survive both.

### Custom Skills (from CLAUDE.md)

Available in `.claude/skills/<name>/SKILL.md` (each carries its own description, which the session lists;
auto-invocable when the request matches, or via `/<name>`): `release-lib`, `release-tianwen`, `sibling-status`,
`check-ci`, `bump-version`, `run-gui` / `run-tui` / `run-fits` (build and launch DETACHED via
`tools/start-app.ps1`: pid + redirected stdout/stderr paths; survives a reaped background shell), `test-run`
(TRX + no truncation; hunts flakes), `test-filter`, `test-image-diff`, `test-output-prune`, `stack`,
`digitize-filter`, `curate-session`, `dataset-gallery`, `plan-report` (`tools/plan-issue-report.py`, also the
`plan-report` workflow), `tick-todo` (close a backlog ISSUE, preferably through its PR, and update CLAUDE.md,
the plan files and memory), `chrome-review` (runs the read-only `.claude/agents/chrome-review.md` reviewer, on
Sonnet, over a branch's UI diff for layout arithmetic the engine should own). **It runs mechanically in CI**:
`.github/workflows/chrome-review.yml` runs it on every non-draft PR whose diff touches a `.cs` or `.razor` file of the five UI projects (a push that leaves the UI diff unchanged is not reviewed again), keeps one
comment on the PR with its report, and fails the check on a high-confidence finding. Until 2026-11-19 a Stop hook
asked each session for it instead; of its 106 asks, 34 ran the agent and 20, mostly in pilot flight worktrees, were
ignored and still counted as reviewed, so the ask moved to the one place every change passes.

### Project Overview (from CLAUDE.md)

TianWen is a .NET 10 library for astronomical device management, image processing, and astrometry.
Supports cameras, mounts, focusers, filter wheels, cover/calibrators, power boxes (switches), and guiders via ASCOM, Alpaca (HTTP),
ZWO, QHYCCD, Player One, SVBONY (the SVBCameraSDK line), ToupTek (and the ten rebadged families on its SDK: Altair, Omegon, OGMA, MallinCam,
...; only ToupTek verified), Meade LX200, Skywatcher, OnStep (serial + WiFi/mDNS), iOptron SkyGuider Pro, Gemini FlatPanel
Lite (native serial cover/calibrator), Gemini Focuser Pro (native serial focuser, a rebadged myFocuserPro2), Gemini
Power&Data Hub Advanced 3 (native serial power box, `ISwitchDriver`; `docs/plans/power-boxes.md`), PHD2, and a built-in guider. Published as `TianWen.Lib` on NuGet, plus AOT-published binaries, of
which **four are release assets** (`tianwen` CLI, `tianwen-server` headless, `tianwen-gui`,
`tianwen-fits`) and two are built but not shipped as `.tar.gz` (`tianwen-mcp`, `tianwen-ascomhost`).

Repository: https://github.com/SharpAstro/tianwen

### Solution Structure (from CLAUDE.md)

```
src/
├── TianWen.slnx, Directory.Build.props (sibling auto-detect); Directory.Packages.props is at the REPO ROOT
├── TianWen.Lib/ (core, net10.0)   TianWen.Devices.Native/ (ZWO + QHYCCD drivers, so Lib carries no vendor natives)
├── TianWen.Lib.SourceGenerators/ (Roslyn: DispatchInterfaceGenerator)
├── TianWen.Lib.Tests/ (unit, xUnit v3)   .Tests.Functional/ (Session loops with FakeTimeProvider)
├── TianWen.Lib.Tests.Gpu/ (every test that drives a real GPU: its own process, one shared Vulkan device)
├── TianWen.Lib.Tests.Simulators/ (on-demand tests vs LIVE Alpaca/ASCOM simulators; gated, skip by default)
├── TianWen.Cli/ (AOT → `tianwen`)   TianWen.Server/ (headless, AOT → `tianwen-server`)
├── TianWen.AscomHost/ (Windows-only out-of-proc host for in-proc COM ASCOM drivers)
├── TianWen.Hosting.Contracts/ (wire DTOs + the shared HostingJsonContext, host AND client reference it)
├── TianWen.Hosting/ (ASP.NET Core Minimal API: REST + WebSocket + Alpaca device plane)
├── TianWen.RemoteClient/ (TianWenNodeClient / EventStream / SessionMirror)
├── TianWen.UI.Abstractions/ (widget system, layout, state, shared types)
├── TianWen.UI.Shared/ (Vulkan FITS pipeline, VkSkyMap pipeline + tab; the SDL→InputKey map is SdlVulkan.Renderer's `SdlInputMapping`)
├── TianWen.UI.Gui/ (N.I.N.A.-style GUI, AOT → `tianwen-gui`)   TianWen.UI.FitsViewer/ (AOT → `tianwen-fits`)
├── TianWen.UI.Web/ (WebAssembly showcase, WebGl renderer)   .Web.E2E/ (Playwright)   TianWen.UI.Benchmarks/ (BenchmarkDotNet)
└── TianWen.AI/ (ORT facade)   TianWen.AI.Imaging/ (image ↔ tensor bridge + enhancer wrappers)   TianWen.AI.MCP/ (MCP stdio server, AOT → `tianwen-mcp`)
```

Six projects set `PublishAot` + an `<AssemblyName>` short lower-case name: `tianwen`,
`tianwen-server`, `tianwen-fits`, `tianwen-gui`, `tianwen-mcp`, `tianwen-ascomhost`. **Only the
first four are packaged as release assets** by `.github/workflows/dotnet.yml`; adding a binary to
the release means adding it to the upload/glob steps there as well as setting the properties here.
**A program another one starts ships BESIDE it, in a checkout as in a release** (`src/ExeBeside.targets`,
P1 of `hardware-in-the-server.md`): `tianwen-server` is built and published into `tianwen-gui`'s and
`tianwen`'s own output (and the functional tests'), `tianwen-ascomhost` into the server's. List a new
one there as `<ExeBesideThis>`; never find a sibling's build output by walking up to the solution. A file
both outputs carry must be the same file, or the build fails naming it.

### Key Technologies (from CLAUDE.md)

| Area | Technology |
|------|-----------|
| DI / Logging | Microsoft.Extensions.* |
| CLI | System.CommandLine v2 + Console.Lib |
| Testing | xUnit v3 + Shouldly + NSubstitute |
| Imaging | SharpAstro codecs facade (`SharpAstro.Tiff`/`.Png`/`.Exr`/...), FITS.Lib (Magick.NET removed) |
| UI / GPU | SDL3 + Vulkan (SdlVulkan.Renderer) |
| Hosting | ASP.NET Core Minimal API, SharpAstro.Jpeg (preview encode) |
| Astronomy | ASCOM, ZWOptical.SDK, QHYCCD.SDK, IAU SOFA (C# port) |
