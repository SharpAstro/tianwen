# The desktop shell: file types, the single-instance hand-off, and the MSIX Store lane

Moved out of `CLAUDE.md` (2026-08-22), which keeps the rules and points here for the reasoning. The
packaging itself lives in
[`../../packaging/windows/msix/README.md`](../../packaging/windows/msix/README.md).

---

`tianwen-fits` ships to the Microsoft Store as **Astro Photo Viewer** (the executable keeps its
`tianwen-fits` name). That is what makes the file associations worth having, and also what creates the
problem underneath: once the shell opens `.fits` with us, every double-click is a fresh AOT process
with its own Vulkan device and font atlas, when what the user wanted was the file to appear in the
window already open.

## The MSIX Store lane

**Packaging lives in `packaging/windows/msix/`** -- `AppxManifest.xml` (the Store identity, which must
match byte-for-byte, plus `.fits .fit .fts .fz .ser`), `build-msix.ps1`, `Assets/`, and a README
carrying the reasoning. `makeappx` comes from the pinned `Microsoft.Windows.SDK.BuildTools` package
rather than whatever SDK the runner image happens to have, on the same principle as every other
pinned CI tool.

- **Two CI lanes, because a release-only lane is discovered broken at the worst moment.** The `build`
  job runs `build-msix.ps1 -ValidateOnly` on every push and PR (manifest schema, and that every asset
  the manifest names exists) in a couple of seconds with no publish; the `msix` job runs on dispatch
  only, packs both arches from the `publish-apps` artifacts and bundles them. Submission is manual.
- **Why the Store rather than a signed installer:** Microsoft re-signs the package after
  certification, so there is no SmartScreen prompt at all. The counterpart is that **MSIX cannot ship
  unsigned** -- `Add-AppxPackage` refuses an untrusted package with no click-through -- so the Store
  account is what makes this route *available*, not one channel among several.
- **Two traps here are silent.** A package with no `resources.pri` resolves NO qualified resource, and
  the only symptom is the icon coming out at the wrong SIZE; nothing warns. And `-AllowUnsigned`
  cannot install a package carrying a Store identity (0x80073D2C) -- to test locally, sign with a
  certificate whose subject matches the manifest Publisher.
- For an UNPACKAGED install `FileAssociationRegistrar` still does the registering. Both routes reach
  the same end state, and neither can do better on Windows 10/11: they make the app a *candidate*, and
  the user assigns the default in Settings.

## The macOS `.dmg` lane

`packaging/macos/` builds one disk image per app per architecture on the `macos-latest` leg of a
release (the `dmg` job in `dotnet.yml`), for `tianwen-fits` as **Astro Photo Viewer** and
`tianwen-gui` as **TianWen**. There is no Mac in the loop; the runner is the Mac, and every
Apple-only tool the lane needs is on it. The README beside the script carries the secrets, the
certificate recipe and the free-versus-paid table; what belongs here is the decisions.

- **A `.dmg`, not the Mac App Store, and the reason is the file list.** App Sandbox is mandatory on
  the Store, and `ViewerActions.ScanFolder` builds the file list from the folder of whatever file was
  opened. Under the sandbox a double-clicked `foo.fits` grants access to `foo.fits` alone, so the
  Finder-open path would silently lose its siblings. The GUI's USB cameras and serial mounts are the
  same story with fewer exits. Notarization asks for none of that: hardened runtime, Developer ID,
  a timestamp, and Apple's scan. A Store lane is a second step, after the folder-access question is
  decided, and it would be a `.pkg` on the same runner rather than a `.dmg`.
- **It works without an Apple Developer Program and does not pretend otherwise.** No secrets means
  an ad-hoc signature (what `dotnet publish` already puts on the binary; Apple silicon refuses to
  run unsigned code at all), no notarization, and a job summary that says so. The consequence is
  Gatekeeper's "developer cannot be verified" on a downloaded copy, which since macOS 15 means
  System Settings > Privacy & Security > Open Anyway rather than a right-click. With the secrets the
  same script signs, notarizes, staples and runs `spctl` the way Gatekeeper would on a download, so
  a failure there fails the job rather than the user.
- **The bundle is the publish tree, whole, under `Contents/MacOS`.** `AppContext.BaseDirectory` is
  the executable's directory, and `ModelResolver`, `BundledFonts`, `SkyMapTab`'s milkyway raster and
  the licence attachments look there; the tidier layout with data under `Contents/Resources` would
  break every one of them.
- **`Contents/MacOS` is `nested=true` in codesign's default resource rules, so NOTHING in it is
  sealed as a resource: every file there is code and must carry its own signature.** This line used
  to say `codesign` seals everything under `Contents/` as a resource either way, and that belief
  cost two release runs (2026-09-12). An unsigned data file under `Contents/MacOS` fails the signing
  of the EXECUTABLE, several commands before any verify, with `code object is not signed at all /
  In subcomponent: <that file>`, naming whichever file the walk reached first -- so it reads like
  one bad file and is actually a whole class. Stripping the `.pdb`s just promoted `LICENSE.EXCEPTION`
  into the message. Measured over seven bundle shapes on `macos-latest`: signing every file passes,
  as does moving the data to `Contents/Resources` (Apple's own layout, and the thing that breaks
  `AppContext.BaseDirectory`); dropping `--deep` from the verify fixes nothing, because the failure
  is not in the verify. A non-Mach-O file's signature lives in an extended attribute, which survives
  the `cp -R` into the image staging and `hdiutil`, verified on the mounted `.dmg`.
- **Debug artefacts are stripped before signing** (`*.pdb`, and the `*.dSYM` the AOT publish leaves
  beside the binary). They would sign fine now; they are dropped because symbolication uses the raw
  `publish/` artifact and never the `.app`, and the `.dSYM` alone is 34.8 MB of a 177 MB tree.
- **Every file is signed with the same identity, inner-most first**, Mach-Os found by magic number
  rather than by a list of names, so whatever the osx publish contains (SDL3, MoltenVK, ONNX Runtime,
  the camera SDKs) is covered. That is what lets library validation stay on: `entitlements.plist`
  grants nothing, and never `allow-jit`, which a NativeAOT binary does not need and Apple looks at
  hardest. A signature complaint about a library the script did not sign is fixed by signing it,
  not by `disable-library-validation`.
- **Validated on every push without a Mac.** `build-dmg.sh --validate-only` (beside the MSIX
  `-ValidateOnly` in the build job) parses both plist templates with the version substituted, the
  entitlements, extracts the icon frame from both `.ico` files and runs `bash -n` on itself;
  `--bundle-only` assembles the `.app` anywhere. What only a first real run answers: the dylib set
  (the sign step prints a count) and whether `13.0` is the right `LSMinimumSystemVersion`.
- **The single-instance hand-off is not exercised on macOS.** Launch Services delivers a document
  open to the running app as an Apple Event, which SDL turns into a drop-file event, so the second
  file lands in the same window with no pipe involved.

## Explorer thumbnails (`tianwen-thumb.dll`)

The full design, the measurements and the caching answer are in
[`../plans/explorer-thumbnails.md`](../plans/explorer-thumbnails.md); this section keeps the shape and
the rules. The shell activates a COM `IThumbnailProvider` for the file type; ours is a **NativeAOT
shared library with source-generated COM** (`TianWen.Shell.Thumbnails`), 3 MB, shipped INSIDE the
viewer's publish tree so the MSIX manifest can name it by a package-relative path and the tarball's
`--register` finds it beside the exe. All imaging is `ThumbnailRenderer` in TianWen.Lib; the DLL moves
bytes across the COM boundary and nothing else.

- **A packaged shell extension may only run in the shell's surrogate** (`desktop2:ThumbnailHandler`
  whose Clsid is a `com:SurrogateServer` class), never in-proc. So the handler is given a STREAM and
  nothing else: `IInitializeWithStream` is the only initialiser, and the container is sniffed from the
  first bytes (FITS `SIMPLE  =`, SER `LUCAM-RECORDER`), one class for all five types. Do not add
  `IInitializeWithFile` for the tarball case: the packaged path is the one Windows exercises in the
  Store build, and it cannot use it.
- **Why a .NET DLL is fine here when managed shell extensions never were:** the objection is the shared
  CLR loaded into explorer.exe. A NativeAOT library has its own runtime, and the surrogate rule keeps
  it out of Explorer regardless. `DllCanUnloadNow` answers `S_FALSE` forever (NativeAOT cannot unload);
  the surrogate's idle exit is how the DLL leaves memory.
- **An embedded `ILLink.Substitutions.xml` applies only to the assembly that embeds it.** TianWen.Lib
  carries 57 MB of catalogs against 1.7 MB of code the thumbnail needs; a consumer-side file naming
  them removed nothing. The switches live IN TianWen.Lib, one a group of data
  (`TianWen.Lib.EmbeddedData.Sky` / `.Stars` / `.Imaging` / `.FakeDevices`, #2155; each set false via
  `RuntimeHostConfigurationOption Trim="true"`, the thumbnail provider naming all four), and
  `EmbeddedCatalogFeatureSwitchTests` pins the lists against the assembly both ways because the format
  has no wildcard.
- **The CLSID is written in three places and must never change once shipped** (the C# constant, the
  manifest's `ThumbnailHandler`, its `com:Class`); `build-msix.ps1` checks the manifest agrees with
  itself on every push and that the DLL is in the tree being packed.
- **Caching is the shell's.** `thumbcache_*.db` per size class, re-extracted only on a miss or a newer
  modified time; the handler is stateless and the library holds no thumbnail cache. A future in-app
  strip reads `IThumbnailCache` rather than building a third.

## The single-instance hand-off

**The gate is folder-scoped, and the pipe IS the lock.** `InstanceGate` (SharpAstro.AppShell) claims a
channel built from a scope plus a normalised folder, so there is one primary *per folder*: a file whose
folder already has a window activates that window and selects the file there, and a file in a new
folder gets a new window. That needs no enumeration and no registry of live instances -- the pipe name
IS the identity. `--new-window` and `TIANWEN_FITS_SINGLE_INSTANCE=0` opt out, and a bare launch never
hands off.

- **Failure is never fatal.** Every failed path opens the document in this process instead. A stray
  window is a poor outcome; a double-click that does nothing is an unacceptable one.
- **Re-binding on a folder change is required, not optional.** The folder is not fixed for the life of
  the process -- the open dialog and a drag-drop both rescan -- so `PumpInstanceGate` releases the old
  channel and claims the new one, holding none if it is already taken. A gate still answering for a
  folder the window no longer shows is worse than having no gate.

## Activation, and both obvious spellings being wrong

**Activation restores ONLY if the window is minimised, and both of the obvious spellings are wrong.**
`sdlWindow.Activate()`, which is AppShell's own extension on `IActivatableWindow` and not a
local copy of it. Raising alone moves input
focus WITHOUT un-minimising, so a minimised window becomes foreground while parked off-screen at
-21333,-21333 and keystrokes go somewhere invisible. Restoring first fixes that and breaks the common
case, because restore un-maximises (`SW_RESTORE`; `SdlVulkanWindow.Restore`'s own summary says
"un-maximized / un-minimized") -- **which shipped to the Store, presenting as "opening a second file
un-maximises my window"**, a window-management bug with no visible connection to the file association
that caused it. The compound state needs no special case: a window minimised FROM maximised comes back
maximised.

## Who the app is: one id per app

**Each app claims one reverse-DNS id before its first window, and outside the Store package the same string
goes to Windows and to SDL.** `DesktopAppIdentity` (TianWen.UI.Shared) holds both apps' ids and names: `com.sharpastro.tianwen.gui`
("TianWen") and `com.sharpastro.tianwen.fits` ("Astro Photo Viewer"). Each `Program.cs` calls AppShell's
`AppIdentity.Claim(id, name)` after the single-instance gate (a launch that hands off and exits names nothing)
and passes `SdlMetadataFor(startMenuShortcut == StartMenuShortcut.Packaged)` to `SdlVulkanWindow.Create`.

- **On Windows** `Claim` gives the process that app id (AppUserModelID) and keeps a Start menu shortcut of
  that name carrying it, which is where the shell finds the name and icon: taskbar grouping, Start search,
  the media flyout. Without it Windows knows the app by its executable's file name. It must run before the
  first window: a window takes its process's app id when it is created.
- **Inside the Store package it does nothing** (AppShell 1.5, `StartMenuShortcut.Packaged`): the manifest
  names Astro Photo Viewer already, and an id of our own would split its windows from the package's tile.
  So the zip build and the Store build make the same call. SDL is told the same: `SdlMetadataFor(packaged)`
  leaves the identifier null when `Claim` answered `Packaged`, so there is no second id in the Store build.
  A `Foreign` or `Failed` answer is logged at Warning (the app then shows as an unknown app).
- **Why one string for both.** SDL hands the identifier to Wayland (`app_id`), X11 (`WM_CLASS`) and macOS,
  and in the SDL releases that have desktop notifications it registers a Windows toast under it. If the
  Windows app id were a different string, a toast (#2507) would group apart from the app's windows.
- **The packaging repeats it**: the macOS bundles' `CFBundleIdentifier` and `CFBundleName`, and the Store
  manifest's display name for the viewer. `DesktopAppIdentityTests` holds them together. A rename is one
  edit there and one in the packaging, never one alone.
- **A development build** run from `bin` gets the shortcut too, the first time when there is none; AppShell
  keeps a shortcut that points at a copy still on disk and repoints one whose target is gone, so an
  installed copy keeps it.

## Desktop alerts

**`DesktopAlerts` (TianWen.UI.Abstractions) is the one place that decides whether a note becomes an operating
system alert (#2507); the sink only shows what it is given.** `tianwen-gui` shows an alert as a Windows toast
through AppShell's `DesktopNotification` (AppShell 1.7), by way of `AppShellDesktopAlert`, the `IDesktopAlert`
sink in TianWen.UI.Gui.

- **What alerts, and when.** Info never alerts. An Error always does (at any level but Off). A Warning alerts
  at `ErrorsAndSafety` (the default) only when its kind is a safety kind (`IsSafetyKind`: an aborted run, a mount
  limit, a run the node stopped because no client was watching it, a run that ended on a fault), and always at `AllWarnings`. Nothing alerts while the window is in front, and a host that supplies no
  window probe gets no alerts at all. The level is per computer (`DesktopAlertSettings`).
- **Grouping.** An alert has a tag (the note's kind; a note with no kind, from an older node, is tagged by its severity and its wording, so a
  different message is never held back as a repeat) and a group (the rig's NodeId, or
  `this-computer`). A repeat of the same tag and group inside `GroupingWindow` (5 minutes, on this client's
  clock, never the note's stamp, because another node's clock may be skewed) is held back and counted; the next
  alert of that tag says how many were held back. The tag and group are also the toast's own, so Windows
  replaces a toast rather than stacking it.
- **Why Urgent is for Error only.** Urgent is the Windows reminder toast that may pass Do Not Disturb. A
  warning must never wake someone through it; only a failure that needs a person now may, so the policy builds
  Urgent for an Error and nothing else, and the sink maps it one to one.
- **The window in front** is a flag the main thread writes each loop iteration from SDL's window flags (input
  focus set, not minimised, not hidden) and the alert consumer reads through `Volatile`. It is never read from
  the SDL window off the main thread.
- **Identity and scheme.** The toast is shown under the app's id (`DesktopAppIdentity.Gui`, the one `Claim`
  gave the process), so it groups with the app's windows. `Program.cs` calls `LaunchScheme.Register` with
  `DesktopAppIdentity.GuiLaunchScheme` (`tianwen-gui`) after `AppIdentity.Claim`, so the click's link
  (`tianwen-gui:notifications` or `tianwen-gui:notifications/<NodeId>`) has a handler that starts this
  executable. `Foreign` (another program owns the scheme) and `Failed` are logged at Warning, and the toast
  still shows, only without a working click. `DesktopNotification.LogOnce` logs what the probe found, once.
- **The click's route.** Windows starts `tianwen-gui <link>`. `Program.cs` reads args[0] with
  `LaunchScheme.TryRead`; with an instance already running it hands the link through `InstanceGate` instead of the
  empty payload, and `PumpInstanceGate` in the running instance resolves each payload with `DesktopAlertLink`.
  With no running instance the link is kept and routed once the window and its views exist (once: a rig the link
  names that has no view context yet falls back to this computer's feed). A payload is untrusted
  input: it is an id to look up, never a command or a path. `DesktopAlertLink.Resolve` matches only a known
  NodeId and otherwise opens this computer's view. The view is selected the way the rig picker selects it
  (`SelectRemoteRigSignal` with `GuiTab.Notifications`, or `SelectLocalContextSignal` for this computer), then
  `sdlWindow.Activate()` brings the window to the front. A click on a rig's toast at a cold start is best effort:
  the rig is found by display name, and discovery may not have found it yet, in which case the Notifications tab of
  this computer opens.
- **Linux and macOS: not yet.** The sink reports `IsAvailable` only on Windows, so off Windows the policy decides
  nothing and nothing is shown. The seam is `IDesktopAlert`; a platform joins by supplying its sink.
- **The live check (the controller's).** Force a session failure with the window behind another window: one toast
  appears. With the window in front: none. Click the toast: the window comes to the front on the Notifications tab
  of that view. Repeat with Do Not Disturb on and the app allowed through it: the Error's toast still shows.

## Where the pieces live, and why

**Three layers, and the arguments are the point:**

| piece | home | why there |
|---|---|---|
| `InstanceGate`, `ForegroundActivation`, `IActivatableWindow`, `WindowActivation` | **SharpAstro.AppShell** | Its PURPOSE is shell plumbing independent of any one toolkit, so the concepts belong here even as platform implementations accumulate. (It also happens to have no in-house dependencies, making it a **sink** -- useful for checking that an edge into it cannot create a cycle, but not the reason it owns them.) |
| `SdlVulkanWindow : IActivatableWindow` | **SdlVulkan.Renderer** (7.23+) | The toolkit owns the translation, so no application writes an adapter. Three members that already existed; what it gains is the rule. |
| the wiring (claim, hand off, pump, re-bind) | each host's `Program.cs` | Policy: what the scope is, whether `--new-window` applies. |

- **Rejected: `IActivatableWindow` in DIR.Lib.** The design argument is good -- it is exactly the
  `CursorKind` shape, a meaning named centrally and mapped by the toolkit. What rules it out is the
  **release cascade**: AppShell would depend on DIR.Lib, and a DIR.Lib minor forces a release of every
  downstream lib whether its code changed or not, permanently, in exchange for a three-member
  interface. Note what is NOT an argument here: "AppShell has few dependencies today" is a fact about
  a repo days old, not a designed property, so do not reach for it.
- **Adopted: the implementation goes upstream, not an adapter downstream.** `SdlVulkanWindow`
  implements the interface in SdlVulkan.Renderer itself, so every SDL app gets correct activation for
  free instead of each one re-adapting. The arrow points at the small library: SdlVulkan.Renderer
  already had AppShell's only dependency (`Microsoft.Extensions.Logging.Abstractions`), so this added
  no transitive dependency at all.
  - **The android leg is referenced deliberately, not by oversight.** The first instinct is to exclude
    it, on the grounds that a named pipe and a `user32` P/Invoke mean nothing there. But the CONCEPT
    does: android has launch modes, `FLAG_ACTIVITY_REORDER_TO_FRONT` and `moveTaskToFront`, and a file
    manager opening a file delivers an Intent to an existing Activity. Only the mechanism differs, so
    excluding that TFM would assert the opposite of what is true. `ForegroundActivation` already
    guards on `OperatingSystem.IsWindows()`, so what ships there is unreachable, not broken; both TFMs
    compile against it.
  - **What was NOT done, and must not be:** stating the rule twice, once in AppShell for
    toolkit-agnostic consumers and once as a dependency-free convenience method on
    `SdlVulkanWindow`. It needs no dependency at all, which is exactly why it is tempting, and it is
    the same two-copies-of-a-rule failure that caused the bug, moved up a layer.
- **The rule is stated once because prose in two places is what broke it.** Both hosts carried the
  same five-line comment asserting restore was harmless, and both were wrong; it was also untestable
  there, observable only by maximising a real window and double-clicking a real file. Behind the
  interface it is three tests over a fake, and the maximised one fails against the shipped behaviour.

## Moved from CLAUDE.md, 2026-11-15

The text `CLAUDE.md` carried until 2026-11-15, where this document did not already hold it; `CLAUDE.md` keeps one line
per rule and points here.

### Desktop Shell: File Types, the Single-Instance Hand-off, and the MSIX Store Lane (from CLAUDE.md)

`tianwen-fits` ships to the Microsoft Store as **Astro Photo Viewer**, which is what makes file
associations worth having and what makes every double-click a fresh AOT process unless the file is
handed to the window already open. Layering, the two CI lanes, the activation bug that shipped and
both MSIX traps: this document; packaging: `packaging/windows/msix/`;
thumbnails: `docs/plans/explorer-thumbnails.md`. Rules that bite:

- **The same DLL serves a property handler for the Explorer detail columns** (`AstroPropertyStore`, its own CLSID,
  #2429). It reads a header only, through the pure `ExplorerFileProperties.Read` in Lib, and no exception crosses
  COM. The Store manifest declares it (`desktop2:DesktopPropertyHandler`, a second `com:Class`); the tarball
  registers it under HKLM only, so unelevated it logs one warning and the rest registers
  (`docs/plans/explorer-thumbnails.md`, "Detail columns").
- **Explorer thumbnails run in the shell's surrogate and get a STREAM.** `Initialize` reads NOTHING
  and `WTSCF_FAST` answers `WTS_E_FASTEXTRACTIONNOTSUPPORTED`, because a read HYDRATES a cloud
  placeholder -- buffering there would download a whole OneDrive folder just to answer "do thumbnails
  exist". Inside a OneDrive sync root the handler is never even asked (one whole-root
  `ThumbnailProvider` under `SyncRootManager` answers for every extension): `docs/known-limitations.md`.
