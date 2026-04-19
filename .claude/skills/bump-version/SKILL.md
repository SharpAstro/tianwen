---
name: bump-version
description: Bump TianWen's version number across ALL four locations that must stay in sync (TianWen.Lib.csproj AssemblyVersion, TianWen.Cli VersionPrefix, TianWen.UI.FitsViewer VersionPrefix, .github/workflows/dotnet.yml VERSION_PREFIX). Use when the user asks to bump, update, or increment TianWen's version.
---

Usage: `/bump-version <major.minor>` (e.g. `/bump-version 0.9`) or pass the version as an argument.

The four locations (documented in memory "Version Bump Process"):
1. `src/TianWen.Lib/TianWen.Lib.csproj` - `<AssemblyVersion>X.Y.0.0</AssemblyVersion>`
2. `src/TianWen.Cli/TianWen.Cli.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
3. `src/TianWen.UI.FitsViewer/TianWen.UI.FitsViewer.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
4. `.github/workflows/dotnet.yml` - `VERSION_PREFIX: X.Y.${{ github.run_number }}`

Steps:
1. Read the current version from all four files
2. Show the user what will change (old -> new)
3. Update all four files
4. Verify all four now have the new version
5. Do NOT commit - let the user review and commit when ready
