# macOS disk images for the viewer and the GUI

`tianwen-fits` (as **Astro Photo Viewer**, the same name as the Store listing) and `tianwen-gui`
(as **TianWen**), each wrapped in a `.app` bundle on a `.dmg`, one image per architecture, built by
the `dmg` job of `.github/workflows/dotnet.yml` on the `macos-latest` runner from the publishes the
`publish-apps` matrix already makes. **There is no Mac in the loop: the runner is the Mac.** Every
Apple-only tool this needs (`codesign`, `sips`, `iconutil`, `hdiutil`, `notarytool`, `stapler`) is
on it, App Store Connect is a website, and a certificate request is an `openssl` command.

```bash
# on a Mac, from the repo root
dotnet publish src/TianWen.UI.FitsViewer/TianWen.UI.FitsViewer.csproj -r osx-arm64 -c Release
bash packaging/macos/build-dmg.sh --app fits \
    --publish-dir src/TianWen.UI.FitsViewer/bin/Release/net10.0/osx-arm64/publish \
    --version 7.1.0 --out artifacts/tianwen-fits-osx-arm64.dmg

# anywhere: what CI checks on every push (templates, entitlements, icons, the script itself)
bash packaging/macos/build-dmg.sh --validate-only

# anywhere: assemble the .app and stop before the tools that need macOS
bash packaging/macos/build-dmg.sh --app gui --publish-dir <publish> --version 7.1.0 --bundle-only --out artifacts
```

Do NOT pass `-r` to `dotnet restore` or `dotnet build`, for the reason the MSIX README gives: only
`dotnet publish -r` is valid in this graph.

## Free or paid, and what each buys

| | no Apple Developer Program | with it ($99 / year) |
|---|---|---|
| the `.dmg` builds | yes | yes |
| the app is signed | **ad-hoc** (`codesign -s -`), which `dotnet publish` already does; Apple silicon refuses to run unsigned code at all | **Developer ID Application** certificate, hardened runtime, secure timestamp |
| the image is signed | no | yes |
| notarized and stapled | no | yes |
| opening a downloaded copy | Gatekeeper: "cannot be opened because the developer cannot be verified". Since macOS 15 the right-click > Open bypass is gone; it is System Settings > Privacy & Security > **Open Anyway**, once per app | double-click |
| review, sandbox | none | none: those are Mac App Store requirements, not notarization's |

The lane is written so both rows work from the same script: without the secrets it signs ad-hoc,
says so, skips notarization and still produces the image; with them it signs, notarizes and staples.
Nothing about the build changes between the two, so the day the secrets appear nothing else does.

## Why a `.dmg` and not the Mac App Store

App Sandbox is mandatory on the Store, and the viewer's file list is built by scanning the folder of
whatever file was opened (`ViewerActions.ScanFolder`, from `Path.GetDirectoryName`). Under the
sandbox a double-clicked `foo.fits` grants access to `foo.fits` alone, so the Finder-open path would
silently lose its file list; it would work only when the user picks the *folder* through the open
dialog (SDL's is `NSOpenPanel`, which the sandbox honours) and the grant were kept as a
security-scoped bookmark. The GUI talks to cameras over USB and mounts over serial, which the sandbox
gates behind entitlements and, for serial, refuses outright. Notarization asks for none of that. A
Store lane is a second step, after the folder-access question is decided; it would take a `.pkg`
(`productbuild`), an Apple Distribution certificate and a provisioning profile, on the same runner.

## What the script does, and the decisions in it

1. **The bundle is the publish tree, whole, under `Contents/MacOS`.** `AppContext.BaseDirectory` is
   the executable's directory, and that is where `ModelResolver`, `BundledFonts` and the licence
   attachments look; moving `models/` and the notices to `Contents/Resources` would be tidier and
   would break every one of those. `codesign` seals everything under `Contents/` as a resource
   either way. Only `Info.plist`, `PkgInfo` and the icon are added.
2. **`Info.plist` comes from a template per app** (`tianwen-fits.Info.plist.in`,
   `tianwen-gui.Info.plist.in`); `@VERSION@` and `@BUILD@` are the only substitutions, and the
   result is parsed back with `plistlib` before it is used. The viewer's declares FITS, SER, TIFF
   and Canon raw document types mirroring the MSIX manifest, with exported UTIs for FITS and SER
   because macOS ships none. Bundle identifiers are `com.sharpastro.tianwen.fits` and
   `com.sharpastro.tianwen.gui`; they are ours to choose, unlike the MSIX identity.
3. **The icon is the `.ico`'s 256 px PNG frame** (`ico-to-iconset.py`, stdlib only), resampled by
   `sips` and packed by `iconutil`. 512 and 1024 are upscaled from it: soft, not wrong, and a larger
   frame in the `.ico` is all it takes to fix.
4. **Every Mach-O in the bundle is signed with the same identity, inner-most first**, found by
   magic number rather than by name, so SDL3, MoltenVK, ONNX Runtime and the camera SDKs are covered
   whatever the publish contains. That is what lets library validation stay ON: `entitlements.plist`
   grants nothing, and in particular not `allow-jit` (a NativeAOT binary has no JIT and that
   entitlement is the one Apple looks at hardest). If a first launch or notarization ever reports an
   invalid signature for a library the script did not sign, sign that library; enabling
   `disable-library-validation` is the fallback, not the fix.
5. **The image is `hdiutil` UDZO** with an `/Applications` link beside the app, signed with the
   same identity, then submitted with `notarytool --wait`, stapled, and assessed with `spctl` the
   way Gatekeeper would assess it on a download -- so a failure there fails the job rather than the
   user.

## The five secrets, and how to mint them

All optional; the job runs without them. Repository secrets in GitHub, base64 where noted.

| secret | what | how |
|---|---|---|
| `MACOS_CERTIFICATE_P12` | the Developer ID Application certificate with its private key, PKCS#12, **base64** | `openssl req -new -newkey rsa:2048 -nodes -keyout devid.key -out devid.csr -subj "/CN=SharpAstro/emailAddress=..."`, upload the CSR at developer.apple.com > Certificates > Developer ID Application, download the `.cer`, then `openssl pkcs12 -export -inkey devid.key -in devid.cer -out devid.p12` (you will be asked for the password below), and `base64 -w0 devid.p12` |
| `MACOS_CERTIFICATE_PASSWORD` | the password given to `pkcs12 -export` | |
| `MACOS_SIGN_IDENTITY` | the certificate's common name, e.g. `Developer ID Application: SharpAstro (TEAMID)` | `openssl x509 -in devid.cer -noout -subject` |
| `MACOS_NOTARY_KEY_P8` | an App Store Connect API key, **base64** | App Store Connect > Users and Access > Integrations > App Store Connect API > Team key with the Developer role; it downloads exactly once |
| `MACOS_NOTARY_KEY_ID`, `MACOS_NOTARY_ISSUER_ID` | shown beside the key | |

No `.cer` is needed on the box: the job imports the `.p12` into a throwaway keychain, unlocks it,
sets the partition list so a non-interactive `codesign` may use the key, and lists the identities
it found, which is the first thing to read when a signature fails. The `.p8` never touches the
repo; it is written to `RUNNER_TEMP` for the job.

## Testing one by hand

On a Mac, `--bundle-only` gives you the `.app` to `open`; a full run gives you the `.dmg` to mount.
Off a Mac, `--validate-only` and `--bundle-only` are the reachable half, and they are what CI runs
on every push; the rest is exercised by dispatching a release from a branch (`publish-apps` and
`dmg` run branch-agnostic on purpose, like `msix`).

Two things only a first real run answers, so they are worth reading in that run's log: which
dylibs the osx publishes actually contain (the sign step prints the count), and whether `13.0` is
right for `LSMinimumSystemVersion` (the runtime enforces its own floor either way; the plist only
decides whether Finder says so politely).

The single-instance hand-off (`InstanceGate`) is not exercised on macOS: Launch Services sends a
document open to the running app as an Apple Event, which SDL delivers as a drop-file event, so
the viewer already opens the second file in the same window with no pipe involved.
