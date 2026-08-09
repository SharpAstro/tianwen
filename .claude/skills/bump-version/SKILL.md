---
name: bump-version
description: Bump TianWen's version by editing the single VersionMajorMinor property in src/Directory.Build.props (everything else - the published apps' VersionPrefix, TianWen.Lib's AssemblyVersion, and CI's VERSION_PREFIX - derives from it). Use when the user asks to bump, update, or increment TianWen's version.
---

Usage: `/bump-version <major.minor>` (e.g. `/bump-version 6.2`) or pass the version as an argument.

## There is exactly ONE place

```xml
<!-- src/Directory.Build.props -->
<VersionMajorMinor>6.1</VersionMajorMinor>
```

Everything derives from it and **must not be hand-edited**:

| derived | where | how |
|---|---|---|
| `VersionPrefix` (all projects) | `src/Directory.Build.props` | `$(VersionMajorMinor).0`, guarded on empty so CI's `-p:Version` wins |
| `AssemblyVersion` | `src/TianWen.Lib/TianWen.Lib.csproj` | `$(VersionMajorMinor).0.0` |
| `VERSION_PREFIX` | `.github/workflows/dotnet.yml` | the `version` job reads the property back with `dotnet msbuild -getProperty:VersionMajorMinor` and exposes it as a job output |

**This replaced a seven-location hand-edit** (converted 2026-08-09), which is what this skill used to
automate. If you find a literal version anywhere in a csproj or the workflow, that is a regression:
delete it and let it derive.

Semantics, per the org convention: `X.Y` -> `X.(Y+1)` additive, `X.*` -> `(X+1).0` breaking. **The
patch segment is CI's** (`github.run_number`) and is never written by hand.

## Steps

1. Read the current `VersionMajorMinor` from `src/Directory.Build.props`.
2. Show the user old -> new.
3. Edit that one line.
4. Verify, using the same command CI uses, that it resolves and keeps its shape:
   ```bash
   dotnet msbuild src/Directory.Build.props -getProperty:VersionMajorMinor -nologo
   ```
   Then confirm it propagated (expect `X.Y.0` for each, and `X.Y.0.0` for Lib's AssemblyVersion):
   ```bash
   cd src && for p in TianWen.Lib TianWen.Cli TianWen.Server TianWen.UI.Gui TianWen.UI.FitsViewer TianWen.AI.MCP; do
     echo "$p $(dotnet msbuild $p/$p.csproj -getProperty:VersionPrefix -nologo)"
   done
   ```
5. Confirm no stale literal survived anywhere:
   ```bash
   grep -rn "<VersionPrefix>[0-9]\|<AssemblyVersion>[0-9]" src/*/*.csproj
   grep -n "VERSION_PREFIX:" .github/workflows/dotnet.yml   # expect only the job-env indirections
   ```
6. Add the release-notes entry to the workflow `env:` if the user wants one. **Release notes live
   there, not beside the number** — several entries contain a double hyphen, which XML forbids inside
   a comment (MSBuild reports it as MSB4025 "the project file could not be loaded", which reads like
   corruption rather than punctuation).
7. Do NOT commit. Let the user review and commit when ready.

## If you are editing the props file for any other reason

**No double hyphen inside an XML comment**, ever — same MSB4025 trap as above. It bit this conversion
while it was being written.
