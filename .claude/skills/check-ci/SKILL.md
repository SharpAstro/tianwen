---
name: check-ci
description: Check GitHub Actions CI status across all SharpAstro repos (DIR.Lib, Console.Lib, SdlVulkan.Renderer, Fonts.Lib, FITS.Lib, FC.SDK, ZWOptical.SDK, QHYCCD.SDK, TianWen.DAL, tianwen). Use when the user asks about CI status, build health, latest workflow runs, or whether recent pushes passed.
---

Use `gh run list` to show the latest CI run status for each repo:

- `SharpAstro/DIR.Lib`
- `SharpAstro/Console.Lib`
- `SharpAstro/SdlVulkan.Renderer`
- `SharpAstro/Fonts.Lib`
- `SharpAstro/FITS.Lib`
- `SharpAstro/FC.SDK`
- `SharpAstro/zwo-sdk-nuget` (publishes `ZWOptical.SDK`)
- `SharpAstro/QHYCCD.SDK`
- `SharpAstro/TianWen.DAL`
- `SharpAstro/tianwen`

For each repo, show:
1. Latest run status (success/failure/in_progress)
2. Run conclusion and duration
3. Commit message that triggered it
4. If failed, show the failing step name

Use: `gh run list --repo SharpAstro/<name> --limit 1`

If a repo has no CI workflow configured (e.g. some of the SDK wrappers ship only
on manual releases), `gh run list` returns empty — note it as "no recent CI runs"
rather than treating it as a failure.

Format as a compact summary. Flag any failing or in-progress runs. When a repo
that TianWen directly consumes (DIR.Lib, Console.Lib, SdlVulkan.Renderer, FC.SDK,
ZWOptical.SDK, QHYCCD.SDK, TianWen.DAL, Fonts.Lib via DIR.Lib) is failing,
mention that a TianWen restore will fail until the upstream is green.
