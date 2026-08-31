# Sibling builds, CPM, the web projects in CI, and releasing

The reasoning behind the build wiring TianWen shares with its SharpAstro siblings. `CLAUDE.md` keeps
the rules and the sibling table; this file keeps the history that explains them. The org-wide
mechanism itself is documented once, in the org root's `.github` clone (`../.github/CLAUDE.md`
"Versioning" + `../.github/docs/dotnet-ci-pattern.md`), NOT here and not in this repo's own
`.github/`.

## Central package management: no opt-outs left in `src/`

**A new one needs a real technical justification, not "this project is not in the solution".**
`TianWen.UI.Web` + `.E2E` were the two opt-outs and each drifted exactly as you would expect: a
sibling-family bump sweeping `Directory.Packages.props` cannot see an inline pin, so WebGl.Renderer sat
two minors behind and became the last consumer on DIR.Lib 7.0 after the rest moved to 7.4 (the graph
then unified DIR.Lib by highest-version rather than by intent), and `Microsoft.NET.Test.Sdk` sat at
18.6.0 inline against 18.3.0 centrally.

**Being outside a solution never had any bearing on CPM**, which resolves by walking directories, so
the opt-out bought nothing.

## The `UseLocalSiblings` gate, and the two ways it drifts

**A sibling gated on the switch must also be in that property's own `Exists(...)` list.**
WebGl.Renderer was gated but absent from the list, so a box with every *other* sibling cloned resolved
it to `true` and aimed a `ProjectReference` at a path that was not there.

`QHYCCD.SDK` (`../QHYCCD.SDK/QHYCCD.SDK.csproj`) and `FITS.Lib`
(`../FITS.Lib/CSharpFITS/CSharpFITS.csproj`) used to be outliers (the latter via a separate
`UseLocalFitsLib` switch) and were folded into the one switch; **there is no per-library switch
anymore.** Trade-off: a missing checkout of *any* listed sibling flips the whole set back to packages
(all-or-nothing), which is fine on a dev box that has them all.

**`open-vs.ps1` generates `TianWen.local.slnx`** at the repo root (gitignored) by re-rooting
`src/TianWen.slnx` and appending a `/Siblings/` folder, so Go To Definition lands in sibling *source*.
Its project list **must** match the `UseLocalSiblings` `Exists(...)` conjunction and **nothing enforces
that**: it had drifted to `../StbImageSharp` for all seven codec projects (that repo is `../Codecs`
now) and was missing three others. A generated solution with unresolvable entries loads with them
silently unloaded rather than failing, so touch one file and re-read the other.

## The web projects: out of the solution, in CI

**They were both out of `TianWen.slnx`, treated as one decision, and only one of them earned it.**
`TianWen.UI.Web.E2E` genuinely must stay out: it is `IsTestProject` + Playwright, so a solution-wide
`dotnet test` would sweep a suite that needs a browser and a served app. Run it explicitly:
`dotnet test TianWen.UI.Web.E2E`.

**`TianWen.UI.Web` is IN the solution as of 2026-08-31.** Its exclusion rested on the *deploy* being
heavy -- `pages.yml` is a mono AOT publish, far too heavy for the per-push loop -- but that is an
argument about `publish`, not about `build`. A plain solution build compiles it interpreted, with no
AOT and no relink, so it needs no `wasm-tools` workload; measured cost is **+4 s on a no-op
incremental build** (5.1 s -> 9.1 s).

What that buys is the failure it now catches. The web host consumes `TianWen.UI.Abstractions` from
`.razor` files, and those sit in exactly the blind spot of both local checks: a rename sweep filtered
to `--include=*.cs` cannot see a `.razor` call site, and an out-of-solution project is not compiled by
`dotnet build`. The two overlap precisely, so `MarkSessionSaved` -> `MarkSaved` passed a clean grep AND
a clean solution build, then failed CI ~15 min later on two lines of `Planner.razor`. With the project
in the solution the same edit fails locally in 9 s, with the identical `CS1061` -- verified by
reintroducing it.

`dotnet.yml`'s extra step is therefore now E2E-only; the `Build` step covers the web app. `open-vs.ps1`
re-roots the base slnx, so the generated local solution picks it up with no change there -- which also
makes it coherent with `WebGl.Renderer`, already in that solution precisely because `TianWen.UI.Web`
consumes it.

**Being outside the solution is not a reason to be outside CI, and for a while it was treated as one.**
`dotnet.yml`'s `build` job now compiles both projects explicitly, after its artifact uploads, the QUICK
way: interpreted, no AOT, no relink (so no `wasm-tools` workload), reusing the libraries the job just
built, and passing the same `-p:Version` so nothing rebuilds. That closes the hole where a change to
`TianWen.UI.Abstractions` -- or a sibling pin bump moving `WebGl.Renderer` -- broke the web host and no
PR check could say so; it surfaced only in `pages.yml`, after merge, as a broken deploy of main. It
does NOT cover the AOT leg, trimming, or anything at runtime, and it compiles `TianWen.UI.Web.E2E`
without running it.

**Keep the version properties in that step identical to the `Build` step above**: a different
`-p:Version` regenerates AssemblyInfo and turns a ~1 min incremental compile into a full rebuild of the
graph.

## Releasing a sibling: three traps the org doc does not cover

- **`DOTNET_NOLOGO: 1` must be in the workflow `env:`.** The version is captured from `dotnet msbuild
  -getProperty` stdout, so the SDK's first-run banner must not be able to land in it. Pair it with a
  shape check that *fails the run*, so a renamed or unresolvable property cannot quietly stamp every
  package as `.<run>`.
- **Release notes go in `CHANGELOG.md` at the repo root, never beside the number.** They used to live
  in the workflow's `env:` comment block, justified by the double hyphen several entries contain, which
  XML forbids inside a comment -- but that only ever ruled out the *csproj*, and markdown has neither
  problem. Nothing read them there (no `PackageReleaseNotes`, no read-back), so they were 90% of a CI
  file: DIR.Lib's `dotnet.yml` was 612 comment lines of 674. Converted for `DIR.Lib`, `Console.Lib`,
  `SdlVulkan.Renderer`, `WebGl.Renderer` and `Fonts.Lib`; newest entry first, one `## Major.Minor`
  section each.
- **A test step that rebuilds a `GeneratePackageOnBuild` project without `-p:Version` publishes a
  second, stray package.** It packs again at the csproj default `X.Y.0` into the same `bin/Release` the
  publish job globs with `**/*.nupkg`; both get pushed and `--skip-duplicate` hides it by making the
  re-push a no-op rather than an error. This cost WebGl.Renderer a stray package on every run for
  fifteen releases. Pass `--no-build` to `dotnet test` (what most repos do) or
  `-p:GeneratePackageOnBuild=false`. To audit: list a package's versions and look for a bare `X.Y.0`
  beside the run-numbered ones.

`LALR.CC` is deliberately exempt from the shared shape (tag-driven, version guarded against the pushed
`vX.Y.Z`); leave it alone.

## TianWen's own conversion to that shape (2026-08-09)

It was seven hand-edited places until then. `<VersionMajorMinor>` lives in `src/Directory.Build.props`
and everything derives: `VersionPrefix` for every project (guarded on empty so CI's `-p:Version` wins),
`AssemblyVersion` in `TianWen.Lib.csproj` as `$(VersionMajorMinor).0.0`, and CI's `VERSION_PREFIX`.
`/bump-version` edits the one line. **A version literal in a csproj or the workflow is a regression**;
delete it and let it derive.

Two things specific to this repo:

- **The read-back writes BOTH `$GITHUB_ENV` and a `build` job output**, because `$GITHUB_ENV` is
  **per-job** and five jobs here consume `VERSION_PREFIX` (`build`, `test-unit`, `test-functional`,
  `publish-apps`, `release`). The bare-step form that single-job repos use (Lzip.Lib, the reference
  implementation) would have set it for `build` only and left the rest empty, silently malforming
  `-p:Version=` and tagging a release `v.`. So `build`'s "Resolve version prefix" step exports
  `version-prefix`, and every consumer declares `needs: build` plus a job-level
  `env: VERSION_PREFIX: ${{ needs.build.outputs.version-prefix }}`, which leaves all the `run:` lines
  untouched. **A new job that builds or publishes needs both halves.** It lives in `build` rather than
  a dedicated `version` job on purpose: `build` already checks out and sets up the SDK, so a separate
  job would only serialise its own runner startup onto every push and PR.
- **It closed a latent bug.** `TianWen.Lib` sets `GeneratePackageOnBuild` but had no `VersionPrefix` of
  its own, so a local `dotnet build` packed it at the SDK default **1.0.0** while its `AssemblyVersion`
  read 6.1; there was a 43 MB `TianWen.Lib.1.0.0.nupkg` sitting in `bin/Release` to prove it. The
  shared `VersionPrefix` now covers every project.
