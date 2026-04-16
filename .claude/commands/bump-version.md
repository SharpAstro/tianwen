Bump TianWen's version number. Updates ALL four locations that must stay in sync.

Usage: /bump-version <major.minor>
Example: /bump-version 0.9

The four locations (documented in memory "Version Bump Process"):
1. `src/TianWen.Lib/TianWen.Lib.csproj` - `<AssemblyVersion>X.Y.0.0</AssemblyVersion>`
2. `src/TianWen.Lib.CLI/TianWen.Lib.CLI.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
3. `src/TianWen.UI.FitsViewer/TianWen.UI.FitsViewer.csproj` - `<VersionPrefix>X.Y.0</VersionPrefix>`
4. `.github/workflows/dotnet.yml` - `VERSION_PREFIX: X.Y.${{ github.run_number }}`

Steps:
1. Read the current version from all four files
2. Show the user what will change (old -> new)
3. Update all four files
4. Verify all four now have the new version
5. Do NOT commit - let the user review and commit when ready

The new version is: $ARGUMENTS
