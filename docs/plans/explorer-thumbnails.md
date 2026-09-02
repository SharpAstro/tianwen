# Explorer thumbnails for the Store viewer

Asked by the user 2026-09-02: *"can you investigate if it would be possible for the viewer store app to
show thumbnails in the explorer for the registered file types?"*, then *"get thumbnails working with the
stripped 3 MB lib"*. Answer: yes, and it shipped the same day as `tianwen-thumb.dll`, a 3 MB native COM
DLL that rides inside the viewer's publish tree.

`tianwen-fits` is in the Microsoft Store as **Astro Photo Viewer** and owns `.fits .fit .fts .fz .ser`
as an open-with candidate. Explorer draws those files as a generic icon, because nothing on the machine
can decode them. A thumbnail handler fixes that for the whole shell: Explorer's icon views, the open
dialog, the details pane and any app that asks the shell for a file's thumbnail.

Companion docs: [`../architecture/desktop-shell.md`](../architecture/desktop-shell.md) (the layering the
handler joins), [`../../packaging/windows/msix/README.md`](../../packaging/windows/msix/README.md)
(how the DLL travels in the package).

## How Windows asks for a thumbnail, and what that forces

The shell activates a COM object registered for the file type under the `IThumbnailProvider` handler id
(`{E357FCCD-A995-4576-B01F-234630154E96}`), initialises it with the file, and calls
`GetThumbnail(cx)` for a bitmap no larger than `cx` on either side. Three facts about that protocol
decide the design:

1. **A packaged (MSIX) shell extension may only run out of process.** The manifest hook is
   `desktop2:ThumbnailHandler Clsid` on the file type association, and the CLSID must be a
   `com:SurrogateServer` class: the shell loads the DLL into its own `dllhost.exe`, never into
   `explorer.exe`. An in-proc registration is not available to a package at all. Consequence: the
   handler must initialise from a stream (`IInitializeWithStream`), because `IInitializeWithFile` and
   `IInitializeWithItem` need the handler in-process. Minimum OS is Windows 10 1703, far below the
   package's 19041 floor, and the element requires `Windows.FullTrustApplication`, which the viewer is.
2. **The handler is given a stream and nothing else.** No path, no extension. So one COM class serves
   all five types and sniffs the container from the first bytes: FITS opens with `SIMPLE  =`, SER with
   `LUCAM-RECORDER`. A tile-compressed `.fz` is FITS whose pixels sit in an extension HDU, which the
   FITS reader already walks to, so it needs no case of its own.
3. **The shell owns the cache.** See the caching section below; the handler holds no state between
   calls.

## Why a .NET DLL is acceptable here when managed shell extensions never were

The historical objection to managed shell extensions is the shared CLR: one runtime version per process,
loaded into `explorer.exe` for the life of the session, colliding with any other managed extension. None
of that applies to a **NativeAOT** shared library. It carries its own statically linked runtime, exports
`DllGetClassObject` and `DllCanUnloadNow` as plain C entry points via `UnmanagedCallersOnly`, and its
COM plumbing is the .NET source-generated kind (`GeneratedComInterface` / `GeneratedComClass`), which
is AOT-compatible by design and is exactly the shape of Microsoft's own source-generated COM server
sample. Add the surrogate rule above and nothing of ours ever loads into Explorer at all.

Two NativeAOT facts shape the code. Unloading is unsupported, so `DllCanUnloadNow` always answers
`S_FALSE`; harmless, because the surrogate exits on its own idle timer and takes the DLL with it. And a
COM vtable method is synchronous, so `GetThumbnail` blocks on the renderer's task: the one blocking wait
in the product, unavoidable and commented as such.

## Where the pieces live

| piece | home | why there |
|---|---|---|
| `ThumbnailRenderer`, `ThumbnailRaster` | `TianWen.Lib/Imaging` | Pure: stream in, RGBA out. Testable headless, and shared with any future thumbnail surface (a viewer file-list strip, the hosted API). Also carries the two GUIDs (`ShellExtensionClsid`, `ThumbnailProviderHandlerId`), because the two Windows projects that need them do not reference each other. |
| `StretchModeExtensions.ResolveAuto` | `TianWen.Lib/Imaging` (moved from `ViewerActions`) | The thumbnail must show what the viewer shows on open. The resolver is three lines, which is exactly the size of rule that gets copied instead of moved; moving it keeps ONE resolver. Colour without a calibration renders Unlinked, mono Linked. |
| `SerImageBridge.ToImage(in SerHeader, ReadOnlySpan<byte>)` | `TianWen.Lib/Imaging` | The SER reader is memory-mapped and wants a path; a handler has a stream. Same materialisation as the reader path, from bytes, with the sample decode mirroring `SerReader.ReadFrame16`. |
| `TianWen.Shell.Thumbnails` (`tianwen-thumb.dll`) | `src/` | The COM shell: `AstroThumbnailProvider` (`IInitializeWithStream` + `IThumbnailProvider`), `ThumbnailClassFactory`, `Exports`, `Gdi32.CreateTopDownBgra32`. Moves bytes across the boundary in both directions and does no imaging. |
| `ILLink.Substitutions.xml` + feature switch `TianWen.Lib.EmbeddedCatalogs` | `TianWen.Lib` | Strips the 57 MB of embedded catalogs from a consumer that sets the switch false. Pinned by `EmbeddedCatalogFeatureSwitchTests`. |
| manifest `desktop2:ThumbnailHandler` + `com:SurrogateServer`, `Test-ManifestHandlers` | `packaging/windows/msix/` | The Store registration, and the check that the GUID written in two places agrees and the DLL is in the tree being packed. |
| `FileAssociationRegistrar.RegisterThumbnailProvider` | `TianWen.UI.FitsViewer` | The tarball's registration: `HKCU\Software\Classes\CLSID\{clsid}\InprocServer32` and `<ext>\ShellEx\{E357FCCD-...}` per extension, under the EXTENSION key so it holds whichever app is the default. |
| the `publish-apps` step | `.github/workflows/dotnet.yml` | Publishes the DLL on the two Windows legs and copies it beside `tianwen-fits.exe`, which is what puts it in both the MSIX and the release tarball with no further step. |

## The render path, and why its cost is bounded by the output

`ThumbnailRenderer.RenderAsync(stream, maxEdge)`:

1. sniff, decode one frame (FITS via `Image.TryReadFitsFile(Fits)`; SER header + first frame);
2. debayer with MHC (a CFA mosaic binned as-is averages the pattern away into grey; mono and 3-channel
   frames come back as the same instance, no copy);
3. **bin first** with `Image.Downsample` so the short edge lands at or just above the requested size,
   then stat-scan, `StretchSolver.ComputeStretchUniforms` with the Auto-resolved mode, and
   `Image.RenderStretchedRgba` over that small raster;
4. box-resample to fit inside `maxEdge` x `maxEdge`, aspect kept, never upscaled (the shell pads to a
   square itself and asks handlers not to). Box, not point sampling: a star a few pixels wide falls
   between the samples of a 1:8 nearest-neighbour pass and vanishes.

The pre-bin target is clamped to `[16, 1024]`: the shell's cache tops out at 1024 px, so a 1024
request stretches at most a ~1024 px raster whatever the frame size.

## Measured

win-arm64 laptop, the 3008x3008 RGGB SV605CC light from the test data (18 MB), `cx = 256`. First the
spike (a scratch project outside the repo, 2026-09-02), then the shipped DLL.

| what | result |
|---|---|
| publish of NativeLib + TianWen.Lib + generated COM | clean; exports `DllGetClassObject`, `DllCanUnloadNow`; imports OS DLLs only (UCRT api-sets, kernel32, ole32, advapi32, bcrypt), no vcruntime |
| full COM path from PowerShell: `DllGetClassObject`, `IClassFactory`, `IInitializeWithStream` over a real `IStream`, `GetThumbnail` | all `S_OK`, 256x256 32bpp HBITMAP |
| `IInitializeWithStream` read of the 18 MB frame | 21 ms |
| `GetThumbnail`: debayer + bin + stretch + resample | 108 to 111 ms |
| DLL size, TianWen.Lib resources untouched | 59.6 MB (`.rdata` 57.7 MB, `.text` 1.7 MB) |
| DLL size, catalogs stripped by a global ILC substitution (spike) | 3.07 MB |
| **shipped `tianwen-thumb.dll`**, catalogs stripped by the feature switch | **2.81 MB** (`.text` 1.67 MB, `.rdata` 0.9 MB); exports `DllGetClassObject`, `DllCanUnloadNow` |
| shipped DLL through the REAL shell pipeline (`IShellItemImageFactory::GetImage`, `SIIGBF_THUMBNAILONLY`), no handler registered | `0x8004B200` WTS_E_FAILEDEXTRACTION, as it should |
| same, after a per-user registration: cold, including the shell starting its surrogate | `S_OK`, 256x256 32bpp, **197 ms** |
| same file asked again | `S_OK` in **4 ms**, served from the shell's cache, handler not called |

The 60 MB figure is the trap of this feature, so it gets its own section.

## The size trap: an embedded substitutions file only applies to its own assembly

TianWen.Lib embeds 56.7 MB of manifest resources (Tycho-2 alone is 43.5 MB); the code a thumbnail needs
is 1.7 MB. The ILLink substitutions format can remove a manifest resource by name
(`<resource name=... action="remove"/>`), and the first attempt put such a file in the **consumer**,
naming TianWen.Lib's resources. It removed nothing, and the DLL was 59.6 MB either way.

The reason is in the AOT compiler's `ManifestResourceBlockingPolicy`: an embedded
`ILLink.Substitutions.xml` is parsed per module and indexed **by the module that embeds it**, so a
consumer's file can only ever block the consumer's own resources. Two things do reach another
assembly's resources: a global `--substitution:<file>` argument to ILC (verified: 59.6 MB to 3.07 MB),
or a substitutions file embedded in **that** assembly.

Shipped as the second form, behind a feature switch, because it is self-describing and survives any
consumer: `TianWen.Lib/ILLink.Substitutions.xml` lists every resource under
`<assembly fullname="TianWen.Lib" feature="TianWen.Lib.EmbeddedCatalogs" featurevalue="false">`, and
the thumbnail project sets
`<RuntimeHostConfigurationOption Include="TianWen.Lib.EmbeddedCatalogs" Value="false" Trim="true" />`.
Nothing changes for any other consumer; the switch is off by default and only removes when set false.

Two rules ride on it. **The list has no wildcard and must name each resource exactly**, so
`EmbeddedCatalogFeatureSwitchTests` pins it against the built assembly in both directions (a catalog
added without a line fails a test rather than shipping in the thumbnail DLL; a stale line fails too).
And **a resource a thumbnail genuinely needs would be the first to leave the list**, not a reason to
drop the switch.

## Caching: Windows owns it, and the handler keeps none

The question came up directly: *do we rely on Windows or use the digest / modified-date approach and
cache in the library?* Windows.

- **The shell keeps a per-user thumbnail cache** (`thumbcache_*.db` under
  `%LOCALAPPDATA%\Microsoft\Windows\Explorer`) at discrete size classes (32, 96, 256, 1024; subject to
  change). Per the shell documentation, it calls the extractor **only when the image is not in the
  cache, or when the file's last-modified time is later than the cached copy's**. It also never scales
  up: asked for a size it does not have, it takes the next larger cached entry and scales down.
- **So the handler is stateless by construction.** One instance per request, initialised with the
  stream, asked once, released. It is hosted in a surrogate the shell tears down between bursts, so a
  handler-side cache would have to be on disk, keyed on the same path + mtime the shell already keys
  on: a second copy of the shell's own index, kept in step by hand.
- **The fingerprint approach stays where it is.** `MasterCache.ReadFingerprint` keys stacking masters on
  their inputs, a different question (has the *stack* changed?) that the shell cannot answer. It is not
  a thumbnail cache and should not become one.
- **What that costs us**: the cache invalidates on mtime alone. A change to the stretch algorithm will
  not refresh thumbnails already in the cache until the files are touched or the user clears the cache
  (Disk Cleanup, Thumbnails). A file rewritten in place with its mtime preserved keeps its old picture.
  Both are the shell's rules for every file type, and both are acceptable for astronomical captures,
  which are written once.
- **A future in-app thumbnail strip should READ the shell's cache** (`IThumbnailCache`,
  `CLSID_LocalThumbnailCache`) rather than build a third one. On Windows that gives the file list the
  same pictures Explorer shows, for free, populated by the same handler.

## Phasing

| Phase | Scope | Status |
|---|---|---|
| P0 | Spike outside the repo: NativeLib + generated COM against TianWen.Lib publishes; full COM path from PowerShell; size with and without stripping | DONE 2026-09-02 (numbers above) |
| P1 | `ThumbnailRenderer` + `ThumbnailRaster` in Lib; `ResolveAuto` moved beside `StretchMode`; SER from bytes; `ThumbnailRendererTests` (mono, RGGB, `.fz`, in-memory SER 8/16-bit, refusal, no upscale, stable GUIDs) | DONE 2026-09-02 |
| P2 | `TianWen.Lib.EmbeddedCatalogs` feature switch + `EmbeddedCatalogFeatureSwitchTests` | DONE 2026-09-02 |
| P3 | `TianWen.Shell.Thumbnails` project (`tianwen-thumb.dll`), in the solution | DONE 2026-09-02 |
| P4 | MSIX manifest (`desktop2:ThumbnailHandler`, `com:SurrogateServer`), `build-msix.ps1 -ValidateOnly` GUID/threading check, pack-time DLL presence check, README | DONE 2026-09-02 |
| P5 | CI `publish-apps` publishes the DLL on win-x64 / win-arm64 into the viewer tree (reaches MSIX + tarball) | DONE 2026-09-02, first exercised on the next dispatch |
| P6 | Tarball registration in `FileAssociationRegistrar` (+ a `SER` group) | DONE 2026-09-02 |
| P7 | Verification through the real shell pipeline on this machine: temporary per-user registration of the published DLL, `IShellItemImageFactory::GetImage(THUMBNAILONLY)` on a real light, keys removed afterwards | see the log below |
| P8 | Verification on a SIGNED MSIX install (sign a copy with `build-msix.ps1 -SignPackage`, install, browse a folder of `.fits`) | OPEN: needs the next dispatch's package |
| P9 | Later, if wanted: `TypeOverlay` / `Treatment` registry hints (photo border instead of drop shadow); a strided FITS read for very large frames (reads 1/N^2 of the pixels from a seekable stream); a viewer file-list strip reading the shell cache | NOT STARTED |

## Verification log

- **2026-09-02, spike**: see the Measured table. Both the direct export and the COM path rendered the
  same 256x256 star field from the SV605CC light.
- **2026-09-02, shipped DLL (P7)**: published `TianWen.Shell.Thumbnails` for win-arm64 Release
  (2.81 MB), registered it per user under `HKCU\Software\Classes` exactly as `FileAssociationRegistrar`
  does (CLSID `InprocServer32` + `.fits\ShellEx\{E357FCCD-...}`), copied the SV605CC light to a fresh
  temp folder under a fresh name (so the shell cache could not hold a stale answer), and asked the shell
  itself via `IShellItemImageFactory::GetImage(256, SIIGBF_THUMBNAILONLY)`. Before registration:
  `0x8004B200`. After: `S_OK`, a 256x256 star field, 197 ms cold with a new `dllhost.exe` surrogate
  appearing at that instant (so the DLL ran out of process, as the packaged form will). The second ask
  returned in 4 ms from the cache without touching the handler, which is the caching model above
  observed rather than read. The keys were removed afterwards and their absence checked.

## Traps, for the next person

- **`IInitializeWithStream` or nothing.** A packaged handler cannot run in-proc, and the stream is all
  the surrogate gives it. Do not add `IInitializeWithFile` "for the tarball case"; it would create two
  code paths where the packaged one is the only one Windows will ever exercise in the Store build.
- **The CLSID is written in three places** (the C# constant, the manifest's `ThumbnailHandler` and its
  `com:Class`) and must never change once shipped. `build-msix.ps1` checks the manifest agrees with
  itself; `ThumbnailRendererTests` pins the constant. A changed CLSID is a handler Windows no longer
  finds, silently.
- **Never `throw` across the COM boundary.** Every method returns an HRESULT; a file the renderer
  cannot decode is `E_FAIL`-class back to the shell, which then draws the generic icon.
- **`DllCanUnloadNow` is `S_FALSE` forever**, by NativeAOT's rules, and that is fine.
- **The substitutions trap above.** A consumer-side `ILLink.Substitutions.xml` naming another
  assembly's resources compiles, publishes, and removes nothing.
