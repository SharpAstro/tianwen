# MSIX package for the FITS viewer

`tianwen-fits`, packaged for the Microsoft Store as **Astro Photo Viewer**.

```powershell
# from the repo root
dotnet publish src/TianWen.UI.FitsViewer/TianWen.UI.FitsViewer.csproj -r win-arm64 -c Release
./packaging/windows/msix/build-msix.ps1 `
    -PublishDir src/TianWen.UI.FitsViewer/bin/Release/net10.0/win-arm64/publish `
    -Arch arm64 -Version 6.2.0.0 -OutFile artifacts/AstroPhotoViewer-arm64.msix
```

Do NOT pass `-r` to `dotnet restore` or `dotnet build` anywhere in this graph: `SdlVulkan.Renderer`
multi-targets `net10.0-android`, Android runs on Mono, and a RID applied to that target framework
asks NuGet for a `Microsoft.NETCore.App.Runtime.Mono.win-arm64` that cannot exist. Only
`dotnet publish -r` is valid.

## Why MSIX at all, and why only with the Store

| | |
|---|---|
| Package/Identity/Name | `SharpAstro.AstroPhotoViewer` |
| Package/Identity/Publisher | `CN=122BD22F-D2B3-495A-80BD-1B05280ADEBF` |
| PublisherDisplayName | `SharpAstro` |
| Package Family Name | `SharpAstro.AstroPhotoViewer_jgmekrdtdb020` |
| Store ID | `9PMDZP16TGBG` |

Those are assigned by Partner Center and must match byte for byte, or the upload is rejected for an
identity mismatch that does not say which field was wrong.

**The Store re-signs the package after certification.** That is the whole reason for this format:
free, no certificate to buy or rotate, and no SmartScreen warning at all, which is better than
anything achievable with a certificate we bought ourselves. MSI or EXE through the Store gets none of
that -- Microsoft does not re-sign those, so they still need a certificate chaining to the Microsoft
Trusted Root Program.

**The counterpart trap: MSIX cannot ship unsigned at all.** A signature is structural;
`Add-AppxPackage` refuses an untrusted package outright, with no click-through equivalent to
SmartScreen's "run anyway". So MSIX is better than a plain tarball *with* the Store behind it and
worse without: today's unsigned `.tar.gz` runs for anyone willing to click past a warning, and an
unsigned `.msix` on the same release page installs for nobody.

The consequence for this directory is that `build-msix.ps1` deliberately produces an **unsigned**
package. It is an upload artifact, not something a user can install.

## Testing it locally

Needs Developer Mode (Settings, System, For developers), because there is no signature:

```powershell
Add-AppxPackage -AllowUnsigned artifacts/AstroPhotoViewer-arm64.msix
# and to remove it again
Get-AppxPackage SharpAstro.AstroPhotoViewer | Remove-AppxPackage
```

Then check the Start menu entry reads "Astro Photo Viewer", and that right-click, Open with offers it
for `.fits` and `.ser` **without** having changed which app currently owns those extensions.

## Version rules

`build-msix.ps1` asserts all three, because each fails late and unhelpfully otherwise:

- four parts, `Major.Minor.Build.Revision`
- every part at most **65535** (`makeappx` rejects a larger one with a schema complaint that does not
  mention the version)
- the fourth part must be **0** -- the Store reserves it, and rejects a non-zero revision at upload
  time, long after CI went green

CI composes it as `$(VersionMajorMinor).$(github.run_number).0`.

## File type associations

`.fits .fit .fts .fz .ser`, declared once in `AppxManifest.xml` under
`windows.fileTypeAssociation`. TIFF and Canon RAW are opened by the viewer but deliberately left
unregistered: they are already owned by general-purpose photo tools, and every association adds an
"Open with" entry whether or not anyone wants one.

This **supersedes** `FileAssociationRegistrar.cs` for packaged installs. That class stays for the
tarball, where it writes the same registrations by hand. Neither can make the app the default
handler: the shell keeps that choice in a per-user `UserChoice` key sealed with a hash it will not
accept from an installer or a package, so both routes register a *candidate* and the user assigns it
in Settings.

## Assets

`Assets/*.png` are generated from `Resources/MilkyWay.ico` by `tools/bake-msix-assets.py`; the recipe
is checked in beside its output. Nothing is upscaled past the icon's 256px frame, which is why
`Square310x310Logo` and `Wide310x150Logo` are absent (neither is required for certification, and the
wide one cannot come from square art without cropping).

## Still to do

- **Add the Store deep link to <https://sharpastro.github.io/> once the app is live.** Partner Center
  only issues it after the product goes live, which is why the site does not carry it yet.
- Store submission is manual: upload the `.msixbundle` from the CI artifact to the draft submission.
  Automating it needs a Partner Center Azure AD app plus three repo secrets.
- `tianwen-gui` is not packaged. Nothing here is viewer-specific except the manifest's file types and
  executable name, so a second package is mostly a second manifest.
