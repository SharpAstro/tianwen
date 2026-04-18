---
name: release-lib
description: Release a SharpAstro sibling library (DIR.Lib, Console.Lib, SdlVulkan.Renderer, Fonts.Lib, FITS.Lib, ZWOptical.SDK, QHYCCD.SDK) to NuGet and update TianWen to consume the new version. Handles the full chain - bump, push, poll NuGet, update downstream Directory.Packages.props. Use when the user asks to release, publish, ship, or cut a NuGet package for a SharpAstro library.
---

Usage: `/release-lib <library-name>` (e.g. `/release-lib DIR.Lib` or `/release-lib SdlVulkan.Renderer`).

## Library locations

Note csproj paths vary — not every repo uses `src/<Lib>/<Lib>.csproj`:

| Library | NuGet Package | Repo | csproj | CI workflow |
|---------|---------------|------|--------|-------------|
| SharpAstro.Fonts | `SharpAstro.Fonts` | `../../sharpastro/Fonts.Lib` | `src/SharpAstro.Fonts/SharpAstro.Fonts.csproj` | `.github/workflows/dotnet.yml` |
| DIR.Lib | `DIR.Lib` | `../../sharpastro/DIR.Lib` | `src/DIR.Lib/DIR.Lib.csproj` | `.github/workflows/dotnet.yml` |
| Console.Lib | `Console.Lib` | `../../sharpastro/Console.Lib` | `src/Console.Lib/Console.Lib.csproj` | `.github/workflows/dotnet.yml` |
| SdlVulkan.Renderer | `SdlVulkan.Renderer` | `../../sharpastro/SdlVulkan.Renderer` | `src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj` | `.github/workflows/dotnet.yml` |
| FITS.Lib | `FITS.Lib` | `../../sharpastro/FITS.Lib` | `CSharpFITS/CSharpFITS.csproj` | `.github/workflows/dotnet.yml` |
| ZWOptical.SDK | `ZWOptical.SDK` | `../../sharpastro/zwo-sdk-nuget` | `ZWOptical.SDK.csproj` (repo root) | `.github/workflows/dotnet.yml` |
| QHYCCD.SDK | `QHYCCD.SDK` | `../../sharpastro/QHYCCD.SDK` | `QHYCCD.SDK.csproj` (repo root) | `.github/workflows/dotnet.yml` |

`FITS.Lib`, `ZWOptical.SDK`, and `QHYCCD.SDK` are not yet wired for sibling auto-detection
(consumed as PackageReference only). Their release flow is the same as the others, but
local in-tree iteration requires a local nupkg feed rather than switching to ProjectReference.

## Steps for a single library release

1. **Bump version** in the library repo. Update BOTH:
   - `<VersionPrefix>X.Y.0</VersionPrefix>` in the `.csproj`
   - `VERSION_PREFIX: X.Y.${{ github.run_number }}` in `.github/workflows/dotnet.yml`
   - Increment minor for new features, major for breaking changes

2. **Build and test** the library locally:
   ```
   cd <repo>/src && dotnet test
   ```

3. **Commit and push** the version bump in the library repo

4. **Wait for NuGet publication** - CI builds, packs, and publishes to nuget.org.
   Poll until the new version appears (typically 2-5 minutes after CI completes):
   ```
   dotnet package search <PackageName> --exact-match --source https://api.nuget.org/v3/index.json
   ```
   The exact published version includes the CI run number, e.g. `2.3.445`.
   You MUST have this exact version number before proceeding to downstream updates.

5. **Update downstream `Directory.Packages.props`** with the EXACT version from step 4:
   - If this is a leaf library (Console.Lib, SdlVulkan.Renderer), update TianWen
   - If this is DIR.Lib, update Console.Lib AND SdlVulkan.Renderer first (see chain below)

6. **Build and test** the downstream project:
   ```
   cd src && dotnet restore && dotnet build && dotnet test
   ```

## Dependency order

```
SharpAstro.Fonts --> DIR.Lib --> Console.Lib        --> TianWen
                             --> SdlVulkan.Renderer  --> TianWen
```

- **SharpAstro.Fonts** is the root. Only bump when its own code changes.
- **DIR.Lib** depends on SharpAstro.Fonts. When DIR.Lib gets a minor bump,
  ALL downstream libs need a release even if their code didn't change - this
  keeps all versions in sync and ensures CI builds pick up the new DIR.Lib
  transitively.
- **Console.Lib** and **SdlVulkan.Renderer** both depend on DIR.Lib but NOT
  on each other, so they can be released in parallel.

## CRITICAL: Full release chain when DIR.Lib changes

Each step MUST wait for the previous NuGet publication before proceeding.
Do NOT push downstream repos until their `Directory.Packages.props` has the
exact published version - CI will fail because it doesn't have sibling repos
and the old NuGet versions won't have the new APIs.

1. (If Fonts.Lib changed) Bump + push SharpAstro.Fonts, poll NuGet for exact version
2. Update DIR.Lib's `Directory.Packages.props` to the new Fonts version (if bumped)
3. Bump + push DIR.Lib, poll NuGet for exact version (e.g. `DIR.Lib 2.3.445`)
4. In parallel:
   a. Update Console.Lib's `Directory.Packages.props` with exact DIR.Lib version,
      bump Console.Lib minor, push. Poll NuGet for exact version.
   b. Update SdlVulkan.Renderer's `Directory.Packages.props` with exact DIR.Lib version,
      bump SdlVulkan.Renderer minor, push. Poll NuGet for exact version.
5. ONLY AFTER both Console.Lib and SdlVulkan.Renderer are on NuGet:
   Update TianWen's `src/Directory.Packages.props` with ALL three exact versions:
   ```xml
   <PackageVersion Include="DIR.Lib" Version="2.3.445" />
   <PackageVersion Include="Console.Lib" Version="2.1.123" />
   <PackageVersion Include="SdlVulkan.Renderer" Version="3.1.67" />
   ```
6. Commit + push TianWen. CI will now restore the correct NuGet versions.

## Polling for NuGet availability

```bash
# Check if package version is available (repeat every 30s until it appears)
dotnet package search DIR.Lib --exact-match --source https://api.nuget.org/v3/index.json
```

The version published by CI is `X.Y.<run_number>` where `<run_number>` comes
from `${{ github.run_number }}` in the workflow. Check the GitHub Actions run
to see the exact run number, or just poll the NuGet search output.

## IMPORTANT: Do NOT push downstream until packages are on NuGet

TianWen CI does not have sibling repos. Every push triggers a CI build that
restores from NuGet. If `Directory.Packages.props` references a version that
isn't published yet, CI will fail. This wastes CI minutes and creates noise.

**Rule: never push a repo whose `Directory.Packages.props` references an
unpublished package version.** Commit locally, wait for NuGet, update the
version, THEN push.

The same applies to Console.Lib and SdlVulkan.Renderer when DIR.Lib is bumped:
do not push them until DIR.Lib's new version is confirmed on NuGet and their
`Directory.Packages.props` is updated.

## Notes

- With `UseLocalSiblings=true` (all three sibling repos present), TianWen uses
  ProjectReference and ignores the PackageVersion. The version in
  `Directory.Packages.props` only matters for CI and machines without siblings.
- Never use `dotnet nuget locals all -c` to clear cache (breaks concurrent
  processes). Just bump the version instead.
- The intermediate libraries (Console.Lib, SdlVulkan.Renderer) also have their
  own `Directory.Packages.props` that reference DIR.Lib - these must be updated
  with the exact published DIR.Lib version before their own CI push.
