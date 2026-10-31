# PILOT.md

What a plane can and cannot do in this repo. The node quotes this page whole into the planner, worker and reviewer
briefs, so it stays short. `CLAUDE.md` holds the architecture and conventions; this page holds only what a flight
needs to plan, build, check and land.

## What a plane can and cannot do

- A plane is a git worktree at `<repo>/.claude/worktrees/<code>` on `flight/<code>`. Work and commit only there. Do not
  push, open a PR or merge; the controller lands the flight. Leave nothing uncommitted, and put scratch output
  outside the plane.
- Sibling clones are not yours. The `UseLocal*` switches compile sibling SharpAstro source (`DIR.Lib`, `Console.Lib`,
  `SdlVulkan.Renderer` and others) when the clone sits beside this repo, so a change there reaches this build with no
  version bump. A change that spans repos is not a step of this plane: name it and leave it.
- No device, mount, camera or GUI window is reachable from a plane. Hardware behaviour is checked through the fake
  drivers and the simulator tests; a window is checked by the CPU-rendered tests. A live run is the controller's.
- Never wait with a job that sleeps or polls: use `pilot leg wait --for-file <path>` and end the turn. Long builds
  and test runs go through `pilot run -- <command>`. The machine is shared, so a job may wait its turn first.

## How a flight merges

- main is protected by a merge queue that **rebases** each PR (the ruleset's `merge_method` is `REBASE`): every
  commit of the branch is replayed onto main, and the queue drops a PR whose commits do not replay cleanly, even when
  a plain merge of the branch would. This is the opposite of pilot's own repo, whose queue squashes.
- So the branch's history matters here. Take main by a merge (`git fetch origin`, `git merge origin/main`), never a
  rebase, and never push with `--force`. A clean merge replays fine. A merge that RESOLVED a conflict keeps the
  resolution in the merge commit, which the rebase drops, so the branch stops replaying: say so in the leg's report,
  and the step that prepares the PR for the queue lays the branch on main as one commit with the reviewed tree.
- Keep a flight's commits few and each one whole. A fixup that only undoes an earlier commit of the same branch is a
  replay hazard; fold it in before the leg ends.

## Build and check

Always `-c Release`, as CI does; a Debug build of a consumer of a Release package can fail with a misleading
`CS0234` about a missing `Diagnostics` namespace. All commands run in `src/`.

```bash
cd src
dotnet build TianWen.slnx -c Release
dotnet test TianWen.Lib.Tests -c Release --no-build --filter "Category!=Heavy"
dotnet test TianWen.Lib.Tests.Functional -c Release --no-build
```

- Tests are xunit.v3 on Microsoft.Testing.Platform, not VSTest: `--logger` and `--blame-*` fail the run. `--filter`
  keeps its VSTest syntax. `global.json` sets the SDK floor.
- CI runs `TianWen.Lib.Tests` (without `Heavy`), `TianWen.Lib.Tests.Functional` and `TianWen.Lib.Tests.Gpu`. A
  `Heavy` test runs nightly; `TianWen.Lib.Tests.Simulators` runs on demand. A plane's check runs the unit and functional
  suites and leaves `.Gpu` to CI on purpose: a GPU fault ends the whole test process, and the machine is shared with
  other flights. A narrower check filters with `--filter "FullyQualifiedName~<Name>"`.

## Done-when kinds and what each needs

- **Build and test passes:** the Release build and the named project and filter, run in `src`.
- **A device or session behaviour:** a test on the fake drivers or the session test helper, on the fake clock; no
  test waits out a real ramp or exposure.
- **A page or window looks right:** a CPU-rendered test; a live window is the controller's to look at.
- **A change in a sibling repo or a published package:** its own flight, not a step here.

## Writing

No em-dashes, and no `--` in their place: a full stop, a comma, a semicolon or parentheses. This holds for prose,
comments and commit messages.
