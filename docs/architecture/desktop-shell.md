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
  them removed nothing. The switch lives IN TianWen.Lib (`TianWen.Lib.EmbeddedCatalogs`, set false
  via `RuntimeHostConfigurationOption Trim="true"`), and `EmbeddedCatalogFeatureSwitchTests` pins the
  list against the assembly both ways because the format has no wildcard.
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
