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

The consequence for this directory is that `build-msix.ps1` **pack** and **bundle** modes produce an
**unsigned** package. That is an upload artifact, not something a user can install -- which is why the
script also has a `-SignPackage` mode, purely so a build can be tested on the machine that made it.

## Testing it locally

**`-AllowUnsigned` cannot work here, and Developer Mode does not change that.** The flag is not
"install without checking a signature"; it admits packages whose *publisher* sits in the unsigned
namespace, meaning a `Publisher` string carrying the marker
`OID.2.25.311729368913984317654407730594956997722=1`. Our publisher is the one Partner Center
assigned, so by definition it is not in that namespace and deployment refuses it:

```
Add-AppxPackage: Deployment failed with HRESULT: 0x80073D2C, The package deployment failed
because its publisher is not in the unsigned namespace.
```

That is a deliberate rule rather than an obstacle: identity is what grants a package its storage, its
file associations and its capabilities, so Windows will not hand an arbitrary identity to a package
that cannot prove it owns one. The two requirements are mutually exclusive, and no combination of
flags or Developer Mode reconciles them.

Sign it locally instead, with a throwaway certificate whose subject matches the publisher:

```powershell
./build-msix.ps1 -SignPackage artifacts/AstroPhotoViewer.msixbundle
```

That mints (or reuses) a self-signed code-signing certificate in `Cert:\CurrentUser\My`, signs the
package straight from the store so no `.pfx` or password ever lands on disk, exports the public half
beside it, and prints the two commands to finish with. Only the first needs elevation, and only once
per certificate:

```powershell
Import-Certificate -FilePath AstroPhotoViewer.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage AstroPhotoViewer.msixbundle
# and to remove it again
Get-AppxPackage SharpAstro.AstroPhotoViewer | Remove-AppxPackage
```

`Cert:\CurrentUser\TrustedPeople` is not a way around the elevated step -- app deployment does not
consult the per-user store. Until the import is done, `signtool verify /pa` reports "terminated in a
root certificate which is not trusted", which is the signature being *valid* and merely untrusted; it
is not a signing failure and needs no re-sign.

**Signing edits the package in place, so never submit the file you tested with.** The signature is
about 7 KB of difference between an artifact Partner Center will re-sign and one it may reject. Keep
the CI download for upload and sign a copy, or re-download after testing.

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

## Explorer thumbnails

The same five types get thumbnails in Explorer from `tianwen-thumb.dll`, declared in the manifest as a
`desktop2:ThumbnailHandler` on the file type association plus a `com:SurrogateServer` class
(`windows.comServer`) that names the DLL by package-relative path. Design, measurements and the caching
model: [`docs/plans/explorer-thumbnails.md`](../../../docs/plans/explorer-thumbnails.md).

What this directory has to know about it:

- **The DLL must be in the publish tree being packed.** CI publishes `TianWen.Shell.Thumbnails` for
  each Windows RID and copies `tianwen-thumb.dll` beside `tianwen-fits.exe` before uploading the
  `tianwen-fits-win-*` artifact, so the `msix` job and the release tarball both carry it with no step
  of their own. Packing a tree without it is refused here (`Test-ManifestHandlers`), because the
  package would install fine and simply never show a thumbnail. To pack locally:

  ```powershell
  dotnet publish src/TianWen.Shell.Thumbnails/TianWen.Shell.Thumbnails.csproj -r win-arm64 -c Release
  Copy-Item src/TianWen.Shell.Thumbnails/bin/Release/net10.0/win-arm64/publish/tianwen-thumb.dll `
            src/TianWen.UI.FitsViewer/bin/Release/net10.0/win-arm64/publish/
  ```

- **One GUID, two places.** The handler's `Clsid` and the class's `Id` must be the same GUID, and
  `-ValidateOnly` checks they are (a mismatch is a package whose thumbnails silently never appear). The
  GUID is also `ThumbnailRenderer.ShellExtensionClsid` in TianWen.Lib and must never change once
  shipped.
- **`SurrogateServer` is not a choice.** A packaged shell extension may only run in the shell's own
  `dllhost.exe`; that is why the DLL initialises from a stream and why nothing of ours ever loads into
  explorer.exe. `ThreadingModel` must be `Both`, which the validation also checks.
- **Caching is Windows'.** The shell keeps `thumbcache_*.db` per user and re-asks the handler only on a
  miss or a newer file modified time. Nothing in the package caches anything.

## Assets

`Assets/*.png` are generated from `Resources/MilkyWay.ico` by `tools/bake-msix-assets.py`; the recipe
is checked in beside its output. Nothing is upscaled past the icon's 256px frame, which is why
`Square310x310Logo` and `Wide310x150Logo` are absent (neither is required for certification, and the
wide one cannot come from square art without cropping).

## Still to do

- Store submission is manual: upload the `.msixbundle` from the CI artifact to the draft submission.
  Automating it is `microsoft/microsoft-store-apppublisher@v1.1` plus `msstore reconfigure` and
  `msstore publish -id 9PMDZP16TGBG`, and what it waits on is a Partner Center side that does not
  exist yet: an Entra tenant associated with the account (gear -> Account settings -> Tenants), an app
  registration added under Account settings -> User management -> Microsoft Entra applications with the
  **Manager** role, and **four** repo secrets, not the three this bullet used to claim --
  `AZURE_AD_TENANT_ID`, `AZURE_AD_APPLICATION_CLIENT_ID`, `AZURE_AD_APPLICATION_SECRET`, `SELLER_ID`.
  Checked 2026-09-04. Three things about it are not guessable from the workflow you would write:
  - **`msstore publish` takes `.msix` or `.msixupload`, NOT `.msixbundle`** -- which is the only
    artifact this directory produces. A `.msixupload` is a zip around the bundle, so closing that gap
    is a third mode of `build-msix.ps1`, not a step in the workflow.
  - **`publish` DELETES the pending draft** and recreates it from the last published submission, so it
    has to run before any metadata edit, never after. `-nc, --noCommit` stops at draft.
  - App updates through GitHub Actions are **free-products-only** today. Astro Photo Viewer is free, so
    this is a note for whoever prices it, not a blocker.
- `tianwen-gui` is not packaged. Nothing here is viewer-specific except the manifest's file types and
  executable name, so a second package is mostly a second manifest.
