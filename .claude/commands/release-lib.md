Release a SharpAstro sibling library to NuGet and update TianWen to consume it.

Usage: /release-lib <library-name>
Example: /release-lib DIR.Lib
Example: /release-lib SdlVulkan.Renderer

## Library locations

| Library | Repo | csproj | CI workflow |
|---------|------|--------|-------------|
| SharpAstro.Fonts | `../../sharpastro/Fonts.Lib` | `src/SharpAstro.Fonts/SharpAstro.Fonts.csproj` | `.github/workflows/dotnet.yml` |
| DIR.Lib | `../../sharpastro/DIR.Lib` | `src/DIR.Lib/DIR.Lib.csproj` | `.github/workflows/dotnet.yml` |
| Console.Lib | `../../sharpastro/Console.Lib` | `src/Console.Lib/Console.Lib.csproj` | `.github/workflows/dotnet.yml` |
| SdlVulkan.Renderer | `../../sharpastro/SdlVulkan.Renderer` | `src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj` | `.github/workflows/dotnet.yml` |

## Steps

1. **Bump version** in the library repo. Update BOTH:
   - `<VersionPrefix>X.Y.0</VersionPrefix>` in the `.csproj`
   - `VERSION_PREFIX: X.Y.${{ github.run_number }}` in `.github/workflows/dotnet.yml`
   - Increment minor for new features, major for breaking changes

2. **Build and test** the library locally:
   ```
   cd <repo>/src && dotnet test
   ```

3. **Commit and push** the version bump in the library repo

4. **Wait for NuGet** - the CI pipeline builds, packs, and publishes to nuget.org.
   Use `dotnet package search <package-name> --exact-match --source https://api.nuget.org/v3/index.json`
   to poll until the new version appears. This typically takes 2-5 minutes after CI completes.

5. **Update downstream** (TianWen) `src/Directory.Packages.props`:
   - Update `<PackageVersion Include="<package>" Version="X.Y.Z" />` to the exact
     version published by CI (includes the run number, e.g. `2.3.445`)

6. **Build and test** TianWen:
   ```
   cd src && dotnet restore && dotnet build && dotnet test TianWen.Lib.Tests
   ```

## Dependency order

```
SharpAstro.Fonts --> DIR.Lib --> Console.Lib    --> TianWen
                             --> SdlVulkan.Renderer --> TianWen
```

- **SharpAstro.Fonts** is the root. Only bump when its own code changes.
- **DIR.Lib** depends on SharpAstro.Fonts. When DIR.Lib gets a minor bump,
  ALL downstream libs need a release even if their code didn't change - this
  keeps all versions in sync and ensures CI builds pick up the new DIR.Lib
  transitively.
- **Console.Lib** and **SdlVulkan.Renderer** both depend on DIR.Lib but NOT
  on each other, so they can be released in parallel.

Full release chain when DIR.Lib changes:
1. (If Fonts.Lib changed) Release SharpAstro.Fonts first, wait for NuGet
2. Update DIR.Lib's `Directory.Packages.props` to new Fonts version (if bumped)
3. Release DIR.Lib (steps 1-4), wait for NuGet
4. In parallel:
   a. Update Console.Lib's `Directory.Packages.props` to the new DIR.Lib version,
      release Console.Lib (bump minor even if code unchanged)
   b. Update SdlVulkan.Renderer's `Directory.Packages.props` to the new DIR.Lib
      version, release SdlVulkan.Renderer
5. Wait for both to arrive on NuGet
6. Update TianWen's `Directory.Packages.props` for all changed packages

## Notes

- With `UseLocalSiblings=true` (all three sibling repos present), TianWen uses
  ProjectReference and ignores the PackageVersion. The version bump only matters
  for CI and for machines without the sibling repos.
- Never use `dotnet nuget locals all -c` to clear cache (breaks concurrent processes).
  Just bump the version instead.

The library to release is: $ARGUMENTS
