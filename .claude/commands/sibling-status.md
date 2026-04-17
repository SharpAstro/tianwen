Show git status, current branch, and last commit across all SharpAstro sibling repos.

All paths in this skill are **relative to the tianwen git root** (not `src/`). Before running
any commands, cd to the repo root (`git rev-parse --show-toplevel`) so sibling paths resolve
consistently regardless of where the user invoked the skill from.

From the tianwen repo root, every sibling repo lives one level up, at `../<name>`. Enumerate
every subdirectory of `../` that contains a `.git` directory (or file, for worktrees) and
treat each as a sibling to inspect. Always include the current repo (`.`) in the output.

For each repo, show:
1. Current branch name
2. Commits ahead/behind remote (if tracking)
3. Any uncommitted changes (short status, first few lines)
4. Last commit message (one line)
5. The VersionPrefix (or AssemblyVersion as a fallback) from the repo's main `.csproj`.
   csproj path varies by repo:
   - `src/<repo>/<repo>.csproj` — DIR.Lib, Console.Lib, SdlVulkan.Renderer, Fonts.Lib (as SharpAstro.Fonts)
   - `CSharpFITS/CSharpFITS.csproj` — FITS.Lib (package name differs from csproj name)
   - `<repo-root>/<Name>.csproj` — ZWOptical.SDK (in `zwo-sdk-nuget/`), QHYCCD.SDK
   If none of the above patterns match, skip silently — don't guess.

Format as a compact table with repo names in a stable order (alphabetical is fine, with tianwen
last so the user sees it as the "current" row). Flag any repo that has uncommitted changes or
is ahead of remote.

**Dependency chain to keep in mind when advising on push safety:**
- TianWen directly consumes: `DIR.Lib`, `SdlVulkan.Renderer`, `Console.Lib` (sibling auto-detect
  + PackageReference fallback), plus `FC.SDK`, `FITS.Lib`, `ZWOptical.SDK`, `QHYCCD.SDK`,
  `TianWen.DAL` as PackageReference only (sibling working copies exist for FITS.Lib, ZWO, QHY
  but are not wired for auto-detection).
- `Fonts.Lib` (published as `SharpAstro.Fonts`) is transitive via `DIR.Lib`. An AHEAD Fonts.Lib
  only blocks a TianWen push if `DIR.Lib` also has an unpushed commit that relies on it.
- `SdlVulkan.Renderer` and `Console.Lib` both depend on `DIR.Lib`; their published packages
  pin a specific DIR.Lib version.

When any of the directly-or-transitively-consumed repos is AHEAD of remote, call it out
explicitly as a push-safety risk rather than a generic "ahead" flag — the user cares about
whether TianWen's CI will resolve packages, not about arbitrary repos being ahead.
