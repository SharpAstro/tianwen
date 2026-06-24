---
name: bump-version
description: Bump TianWen's version number across all version locations that must stay in sync (the AssemblyVersion in TianWen.Lib.csproj, the VersionPrefix default in each published-app csproj — Cli, Server, UI.FitsViewer, UI.Gui, AI.MCP — and VERSION_PREFIX in .github/workflows/dotnet.yml). Use when the user asks to bump, update, or increment TianWen's version.
---

Usage: `/bump-version <major.minor>` (e.g. `/bump-version 0.9`) or pass the version as an argument.

The version locations (6 csproj defaults + the CI workflow). The workflow `VERSION_PREFIX` is the
authoritative release version — it overrides the csproj defaults via `-p:Version=` in CI — but bump the
csproj defaults too so local builds + the NuGet `AssemblyVersion` stay in sync:
1. `src/TianWen.Lib/TianWen.Lib.csproj` - `<AssemblyVersion>X.Y.0.0</AssemblyVersion>`
2. `src/TianWen.Cli/TianWen.Cli.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
3. `src/TianWen.Server/TianWen.Server.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
4. `src/TianWen.UI.FitsViewer/TianWen.UI.FitsViewer.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
5. `src/TianWen.UI.Gui/TianWen.UI.Gui.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
6. `src/TianWen.AI.MCP/TianWen.AI.MCP.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
7. `.github/workflows/dotnet.yml` - `VERSION_PREFIX: X.Y.${{ github.run_number }}`

The csproj VersionPrefix lines are guarded `<VersionPrefix Condition=" '$(VersionPrefix)' == '' ">X.Y.0</VersionPrefix>`;
match the literal `>X.Y.0</VersionPrefix>` (the sibling `<Version ...>$(VersionPrefix)</Version>` lines need
no change). A CRLF-preserving byte replace (python, asserting exactly one hit per file) is the safe way.

Steps:
1. Read the current version from all locations
2. Show the user what will change (old -> new)
3. Update all locations
4. Verify all now have the new version (and no stray old version remains)
5. Do NOT commit - let the user review and commit when ready
