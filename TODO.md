# TODOs

**The backlog is GitHub issues** since 2026-09-24, when this file and `docs/todo/*.md` were migrated
(see CLAUDE.md, "Project Tracking Docs"). This file's sections became labels: High Priority is
[`priority:high`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Apriority%3Ahigh), Next Up is [`priority:next`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Apriority%3Anext), and bench checks
are [`bench`](https://github.com/SharpAstro/tianwen/issues?q=is%3Aissue+is%3Aopen+label%3Abench). What is left here is the DONE archive, kept for the measurements and reasons it
records. Never add an open `- [ ]` here: open an issue.

## High Priority

- [x] **The TUI live preview corrupts the sub it previews** (found and fixed 2026-09-24; found by the
  capture-path sweep, read from the code, not reproduced). `TuiLiveSessionTab.RenderPreview` handed the
  session's OWN frame to `AstroImageDocument.AdoptImageAsync`, which rescales it to [0, 1] in place, while
  that frame was still queued for its FITS write: the sub was likely saved as 0 or 1 ADU. It now previews
  a leased copy through `AstroImageDocument.FromLiveFrameAsync`, pinned by `LiveFrameDocumentTests`:
  [docs/plans/frame-path-allocations.md](docs/plans/frame-path-allocations.md) P1.
- [x] **DIR.Lib 10: a control is declared once, and the engine behaves** (user, 2026-09-15, high
  priority: "the usage site of a text box should not have to write in that double click or Ctrl-A
  while inside the control selects text ... declaring that the textbox is selectable via Ctrl-F via a
  property/fluid build step should be enough"). Reviewed the same day: the engine HAS the pointer rule
  (9.1) and no host calls it; the GUI, the web host and the TUI each carry their own key and press
  router; four sites move focus past `TextInputFocus`; a node cannot declare a shortcut; a popover costs
  five obligations and a dispatcher line; a drag is five flags on `ViewerState`. Plan, the seven
  findings with file:line, the six engine pieces, the five breaking cuts and the phasing:
  [docs/plans/dir-lib-10.md](docs/plans/dir-lib-10.md).
  - [x] **T0, no engine change:** pin DIR.Lib `9.1.*`, replace the three `clicks >= 2 -> SelectAll`
    sites with `TextInputInteraction.HandlePointer` + `CaretIndexAt`, route the TUI's inline editor
    through `TextInputInteraction.HandleKey`. A double-click selects the word on every surface.
  - [x] D1 (DIR.Lib 9.2, additive): `InputRouter`, `OnPress`, `.Shortcut`, `Popover`, `Content.Slider`,
    `.Selectable()`, `.Disabled(reason)`, `Focus` selects its seed, `MeasureLayout`.
  - [x] T1 / T2: tianwen on the router; popovers and sliders as nodes; delete
    `ISelfDispatchingInputWidget`, `OverlayOwnsPointer`, the five drag flags, the tab-shortcut switch.
  - [x] D2 (DIR.Lib 10.0): the five cuts, one wave, `MIGRATION.md`.
  - **Only T3 is left, and it is the next item** (the viewer chrome onto the engine, `viewer-layout-engine`
    P1 to P4). D2 shipped 2026-09-17 (DIR.Lib 10.0, #290); `main` is on DIR.Lib 11.2.

- [x] **Auto-crop: an interior drizzle hole is not a canvas ring** (2026-09-15, issue #250). Both
  halves of the rule the measurement over 79 masters found, closed together.
  - [x] **`LargestCoveredRectangle` border-gates NaN, as it already did zero.** NaN had been exempt on
    the reasoning that it is "unambiguous" -- it is unambiguous about the PIXEL and says nothing about
    WHY, the only question a largest RECTANGLE asks. A drizzle hole is a NaN surrounded by data,
    exactly the shape the zero gate exists for, and it was the COMMON case: 53 of 79 masters, every
    `BayerDrizzle` one, 19 to 324 components. On the Great Orion Nebula master 1,856 such pixels took a
    **99.94-percent-covered frame to 0.528 of its canvas**; it keeps 0.981 now. One implementation
    (`BorderReachableAbsence`) answers for the crop and the fill, so they cannot disagree about where
    the ring ends.
  - [x] **`Image.FillInteriorHolesInPlace` gives the kept holes a number**, per channel, from
    `AdoptImageAsync` and BEFORE the statistics. Keeping a hole is not coping with one: without this
    the gate above only moves the problem, since the render paints a NaN black, a save writes it back
    out, and every statistic has to remember to skip it. **The ring is never filled** -- that would
    invent rather than interpolate, and erase the only evidence the crop works from.
    `AstroImageDocument.InteriorHolesFilled` reports the count, because a viewer that silently invents
    pixels is one you cannot trust a measurement from.
  - Measurements and the per-master table: [docs/plans/viewer-prerelease-fixes.md](docs/plans/viewer-prerelease-fixes.md) P25.

- [x] **A register sweep of viewer stats, presets and the icon bake** (2026-09-15). Eight items,
  six of them real; the two that were not are worth as much as the six.
  - [x] **`StarMaskedLumaStats` measured ONE COLOUR on a mosaic**, which is the one that was a live
    bug rather than the latent trap it was filed as. Without a `cfa`, the masked walk is a fixed grid
    from (0, 0), and **any EVEN `pixelStride` keeps both parities**, so the default 4 never leaves the
    phase it started on. Its own comment claimed "the whole mosaic, as LumaStats was taken", while
    `LumaStats` had walked every photosite at stride 1. Now matches it; the API's XML states the
    even-stride rule so the next caller cannot fall in.
  - [x] **`GetLumaStretchStatsAsync` materialised a full debayer for three scalars** on RGGB -- some
    288 MB allocated and discarded on a 6000x4000 OSC sub -- to reach the Rec. 709 path. It takes the
    stat in place now, as the document already did. **Nothing was paying it**: the document passes the
    mosaic case itself and the test harness guards on `ChannelCount >= 3`, which a mosaic is not. The
    dead `debayerAlgorithm` parameter went with it, a BREAKING change to published API awaiting its
    bump.
  - [x] **The stretch preset cycler stepped off a stale index.** It now reconciles against the
    parameters in hand (`presets.IndexOf`), falling back to the stored index for a hand-tuned stretch
    that matches no preset; three tests, two of them seen FAILING with the fix reverted. And
    `StretchParameters.Presets` is an `ImmutableArray` -- a plain array was `readonly` only in its
    reference, so any caller could rewrite element zero and move `Default` for the whole process.
  - [x] **The CFA SER histogram refresh is guarded**, not fixed: display channel `c` was paired with a
    flat walk of the interleaved mosaic, writing every colour into the slot that MEANS red and leaving
    green and blue stale. It sits the mosaic out until `HistogramDisplay` gains a CFA-aware update
    ([docs/todo/ui.md](docs/todo/ui.md)).
  - [x] **TWIC0001 was a TIMESTAMP, not a stale table.** Re-running `tools/bake-icons.ps1` produced
    BYTE-IDENTICAL output: the guard compares mtimes, and a `git checkout` had written `icons.recipe`
    after `BakedIcons.g.cs`. Nothing to commit, and the false positive recurs on any checkout that
    orders the files that way.
  - [x] **The hd-hip-cross 121.8 ms claim is CORRECT** and the doubt is refuted. Measured in Release:
    `hd-hip-cross-snapshot:applied` -- the pre-baked snapshot's deserialise-and-apply, exactly as
    CLAUDE.md says, NOT the live-compute fallback. 110.8 ms against the documented 121.8, and 350.0 ms
    total against ~343, both inside single-run variance, so neither number was rewritten on one
    sample. The premise had lapsed anyway: the snapshot was re-baked 2026-09-14. (It applies
    `unverified-no-tyc2-lz`, so the hash gate cannot run where the tyc2 `.lz` is stripped.)
  - [x] **The double stats scan at open stays**, deliberately: the two collectors take DIFFERENT
    histograms (pedestal-removed for the curve, the frame's own levels for the display) and merging
    them would be a silent numeric bug. One traversal producing both is the available win, and it
    wants measurement ([docs/todo/ui.md](docs/todo/ui.md)).

- [x] **`Auto` renders an enhanced frame as a flat colour field** (FIXED 2026-09-10). It was the
  mode CHOICE, not the chosen mode: a background-extracted frame now resolves `Auto` to **Linked**,
  because Unlinked exists to neutralise a background that has not been neutralised, and re-doing it on
  channels already within 0.15 percent of each other fits three curves to the noise between them. Two
  real findings on the way: a pedestal DOUBLE-COUNT (the collected median already has it removed, and
  the consumer removed it again -- latent everywhere, silent while MinValue is 0) and the discovery
  that hoisting `WithZeroPedestal` exposed its guard ignoring the pedestal entirely.
  [docs/plans/viewer-prerelease-fixes.md](docs/plans/viewer-prerelease-fixes.md) P30.
  - [x] **A frame flattened in ANOTHER tool carries no provenance** (FIXED 2026-09-13). The flag is
    gone: `AstroImageDocument.ChannelsAlreadyAgree` MEASURES it from the per-channel medians, so an
    Astro Pixel Processor, Siril or GraXpert master gets the same answer our own enhance output does.
    The threshold nobody could defend turned out to be one already measured -- 0.15 percent, from the
    enhanced 10P drizzle master this rule came from; an APP HOO composite sits at 0.1 percent, and an
    uncalibrated OSC sky is percents away. It costs nothing, which is why no fast path guards it:
    `PerChannelStats` is populated at open because the stretch needs it to draw the first frame.
    **The non-photometric veto was narrowed in the same change**, because it fired unconditionally
    while its whole justification is a bogus white balance: with no calibration active there is no fit
    to protect against, and vetoing anyway is what made an already-balanced HOO composite open Unlinked
    and lose the colour it arrived with.

- [x] **Selecting an object in the viewer, with a floating panel of its own** (raised and SHIPPED
  2026-09-10 / 2026-09-11). `FindObjectAt` already answered "which catalogued object is under this
  pixel" and had been wired to right-click ONLY; the viewer had no notion of a selected object, so a
  left click had nowhere to put the answer. **The user chose CLICK over hover, and asked for the
  atlas's OWN panel rather than a second one.** `ViewerState.SelectedObject` is a
  `SkyMapInfoPanelData` -- the same payload the atlas's selection carries, collapsed from a
  short-lived `ViewerObjectSelection` that duplicated four of its fields -- resolved once at the
  click and rendered through the shared `ObjectInfoPanel` (hoisted from the atlas) as a panel
  floating bottom-left of the picture, closed by its own X or by Escape. The ring draws with the
  overlay OFF, the context menu gains the selection's own atlas entry, and Escape clears the
  selection before Escape means quit. Fires on the tap RELEASE, never the press. **Alt/Az and
  rise/transit/set are at the frame's CAPTURE instant, never "now"** -- baked in once at the click
  from `FrameSiteResolver` and the header's `DATE-OBS`, and OMITTED (not shown as dashes) unless both
  the site and the capture time are known, which a NaN altitude on the baked struct is the one
  reliable signal for. The docked info panel's old "Selection" section is gone -- everything it did
  is now the floating panel's job, with room for what it never had. **A real defect fell out of the
  tests: the resolver named types the overlay never draws** (a click on M42's core answered
  "HH 1146"), now gated by the overlay's own two predicates -- which also closes the same hole in the
  right-click menu.
  [docs/plans/in-app-sky-atlas.md](docs/plans/in-app-sky-atlas.md), "Deferred from the 2026-09-10
  sitting".

- [x] **The sky atlas says what a click would take, and can narrow itself to objects it has a photo
  of** (raised and SHIPPED 2026-09-20). Two halves of one complaint, from the user's notes 2026-09-19
  plus the long-unmatched 2026-06-08 self-note *"sky atlas bug: obj selection"* -- which this turns
  out to be, the user's own reading being *"probably fixed by supporting mouse-over highlighting"*.
  **It does NOT reopen the click-over-hover choice above**: that settled which gesture SELECTS, and
  the click still does; this is the question it left open, that on a field of overlapping markers
  nothing told you which object the press would land on, so finding out meant clicking and reading
  the panel.
  - **Hover draws a translucent wash under that object** (`SkyMapState.HoverTarget`,
    `SkyMapTab.Hover.cs`), and the load-bearing property is that the wash and the click come from
    **ONE resolver**: `SelectObjectByClick` was split into `TryResolveHit` plus a panel build, and
    `ResolveHoverAtScreenPoint` is the second consumer. Two hit tests written the same way is exactly
    how a wash over one object and a panel about another happens, which is worse than no wash. Ctrl
    is deliberately not honoured by the hover -- the modifier is read at the press and a hover has
    none, so guessing would be wrong precisely when the user is holding Ctrl to pick a star out of a
    nebula.
  - **One `FillEllipse` and nothing else.** All three renderers implement it natively (Vulkan and
    WebGL as one distance-field quad), so no instance stream, no cache key, no shader, no sibling
    release. Drawn FIRST of the annotation layers, which is what makes a pointer resting over the
    search modal or the layer palette harmless without any of them claiming the pointer: the sky
    behind resolves, and the wash is painted under the panel covering it.
  - **At most one resolve per painted frame, and the bound is now a checked-in benchmark**
    (`SkyMapHoverResolveBenchmarks`, Release, win-arm64). The first figures here came from a
    throwaway Debug probe and were wrong in two ways at once, which is why the benchmark exists.
    Before and after the fixes below:
    | FOV | over an object | over bare star field, before | after 2026-09-20 | after 2026-09-21 |
    |---|---|---|---|---|
    | 1 deg | ~10 us, 288 B -> 9 us, 0 B | **389 us, 225 KB** | **166 us, 27.6 KB** | **157 us, 0 B** |
    | 10 deg | ~10 us, 288 B -> 9 us, 0 B | **357 us, 225 KB** | **139 us, 27.6 KB** | **134 us, 0 B** |
    | 60 deg | ~10 us, 288 B -> 9 us, 0 B | 1.7 us, 288 B | 1.8 us, 288 B | 1.7 us, 0 B |
    | 170 deg | ~10 us, 288 B -> 9 us, 0 B | 1.6 us, 288 B | 1.7 us, 288 B | 1.6 us, 0 B |
    **The two paths differ by 16x** (44x before), because the DSO pass runs first and the star pass
    NEVER RUNS when it matches -- so resting on a catalogued object is cheap at any zoom and only
    bare sky pays. The old single number blended them.
    **The gap is that short-circuit and nothing else**, which took a second correction to establish.
    This entry used to say the star pass "falls off a cliff as `EffectiveMagnitudeLimit` tightens",
    which was read off the code rather than measured, and is wrong twice over.
    `SkyMapHoverResolveCostProbe` (`TIANWEN_HOVER_PROBE=1 DOTNET_TieredCompilation=0`) attributes it:
    - The nine index cells derive from the unprojected pointer, so they are **identical at every
      zoom** -- 22 deep-sky entries against 1094 composite ones, whatever the FOV.
    - What decides whether the pass is paid is the DSO pass's hit test, floored at a **fixed 20
      SCREEN pixels**, whose footprint in sky runs 0.020 deg at 1 degree FOV to 4.200 deg at 170. The
      nearest deep-sky-grid entry to the benchmark's Aquila pointing is HD 183919 at 0.409 deg, so
      the pass misses at 1 and 10 and matches at 60 and 170 -- which is exactly where the cliff is.
    - The magnitude limit is the minor term and runs the **opposite** way: 1 degree is dearer than
      10 over identical cells and identical lookups, because zoomed IN the limit admits MORE stars to
      the projection.
    - What the pass SPENT before the fix, per resolve on bare sky at 10 degrees (389 us / 225 KB,
      probe whole within 7% of the benchmark):
      | term | time | bytes | each |
      |---|---|---|---|
      | `TryLookupByIndex` x1094 | **320 us** | **197 KB** | 293 ns, 180 B |
      | of which `ConstellationBoundary.TryFindConstellation` x1072 | 174 us | 120 KB | 162 ns, 112 B |
      | of which `TryGetTycho2Star` x1094 (the binary search) | 111 us | 78 KB | 102 ns, 71 B |
      | 9 composite cell lookups (`GetStarsInCell`, 6572 read / 1072 kept = 6x) | 53 us | ~28 KB | |
      | magnitude gate + projection + hit test | ~16 us (~45 at 1 deg) | 0 | |
      The constellation term precessed every candidate J2000 -> B1875 through
      `CoordinateUtils.PrecessRadians`, which built its two vectors and its 3x3 matrix as heap
      arrays, to fill `CelestialObject.Constellation`, which the hit test never reads. The binary
      search's bytes were `CatalogIndex.ToCatalogAndValue` decoding base91 through a string and a
      byte array -- the mirror of the string round trip `Tyc2CatalogIndex` removed from the ENCODE
      side.
    - **Fixed 2026-09-20, three changes, two of them in `TianWen.Lib`:** `PrecessRadians` is scalars
      throughout and `ToCatalogAndValue` decodes into stack buffers (a span `Base91.DecodeBytes` and
      a span `EnumValueToAbbreviation` that the string forms now call, so they cannot drift), which
      every catalogue lookup in the program inherits -- `TryLookupByIndex` on a Tycho-2 star went
      from 293 ns / 180 B to 230 ns / 0 B. And the star pass reads a Tycho-2 candidate through
      `TryGetTycho2Star` (the 17-byte entry as a struct, 76 ns / 0 B) and looks up only the WINNER
      in full, which takes the constellation out of the resolve altogether.
      `Tycho2LiteLookupParityTests` walks every cell of the composite grid to pin that the two
      lookups read the same star (type, position, magnitude at Half width).
    - **Fixed 2026-09-21, the last 27.6 KB and the 288 B:** the nine cell lookups built a `List` of
      each cell's Tycho-2 stars (grown by doubling), a wrapper, a compiler iterator and a boxed array
      enumerator per cell. `IRaDecIndex.EnumerateCell` returns `RaDecCell`, a struct whose enumerator
      scans the same GSC regions with the same cell-box test as the caller advances; both resolver
      passes, the plate solver's region sweep and `CatalogStarCounter` walk cells through it, the
      indexer's own composite collection runs the same scan (its `CopyTo` used to throw, so
      `[.. grid[ra, dec]]` was a runtime error), and `CelestialObjectDB.CoordinateGrid` is cached
      instead of built per read. Time moved 5 percent (166 to 157 us at 1 degree); the point is the
      bytes, 1.7 MB/s of Gen0 at 60 fps to none, and one thing less to reason about on a per-frame
      path. `RaDecIndexBenchmarks` over 1000 random cells: composite enumerate 3,505 us / 208 KB to
      3,279 us / 0 B, deep-sky-only 21.3 us / 32 KB to 11.1 us / 0 B (the boxed enumerator was half
      of that one). `RaDecCellEnumerationTests` holds struct and indexer equal for every cell of the
      sky against every catalogue star bucketed into its box independently, asserts the nine-cell
      walk at 0 B after showing the indexer's walk allocates, and found two facts about the blob
      nothing had pinned: 254 identifiers in it twice (Supplement 1 re-listing main-catalogue stars)
      and one unaddressable star (component 4 in a two-bit field), both in
      `docs/known-limitations.md` and `docs/todo/astrometry.md`, both pinned at their exact counts
      so a re-bake moves them on purpose. The parity test had skipped them with a silent `continue`;
      it now counts and pins its skips too.
    - **Two earlier versions of this entry were wrong.** The first named `GetStarsInCell` as the
      cost and the allocation (it stopped measuring one level too early). The second put
      `TryLookupByIndex` at "~905 ns, three to four times the cell lookups" and explained the
      probe's 3x excess over the benchmark as Stopwatch overhead: both were JIT tiering. A test host
      tiers a method up only after a quiet period, so a loop timed early ran Tier-0 code -- the same
      lookup loop read 992 us first and 290 us re-timed last in one run. **Run the probe with
      `DOTNET_TieredCompilation=0`** (it prints a warning otherwise); the parts then sum to the whole.

    The Debug figure (2.048 ms at 1 degree) was also about 5x pessimistic. **The louder cost turned
    out to be ALLOCATION**: 225 KB per resolve on bare sky zoomed in, roughly 13 MB/s at 60 fps and
    28 MB/s if it ran per MOVE at a 125 Hz mouse -- measured, not inferred, 197 of it in
    `TryLookupByIndex` (120 KB the precession, 78 KB the base91 decode), and gone with the fixes
    above. The click has always paid the same, once per press, where nobody can see it. Every
    resolve asks for the frame, even one that landed on the same object: the budget is released by a
    PAINT, so a resolve that scheduled none would be the last one until something else repainted.
  - **"Only with photo" is a SUB-SETTING of [O], not a layer** (key `I`, indented under "Objects" in
    the palette, unavailable while [O] is off): it draws nothing, it only narrows. It goes through
    **one predicate asked by three callers** -- `OverlayEngine.PassesLayerFilter`, for the desktop's
    background gather, the browser / offline primitive path AND the click gate, the last of which
    must agree or a filtered-out object stays selectable through apparently-empty sky. The first two
    were already two hand-maintained copies of the [O]/[D] rule. It does NOT reach the dark-nebula
    layer -- [D] is its own layer that [O] does not govern, so a filter presented as [O]'s
    sub-setting reaching it contradicted the control's own shape, and did so invisibly (with [O] off
    and [D] on the row read as off while the filter emptied the layer). Pinned by
    `TheFilterDoesNotReachTheDarkNebulaLayer`; the entry below and three code comments said the
    opposite until 2026-09-20. A pinned target survives it as it survives every other filter there.
  - **It is in BOTH gather cache keys** (`PrimOverlayKey`, `OverlayGatherKey`), because it strips the
    CACHED candidate list: without that, switching it on keeps serving the list gathered before it
    and switching it off never brings the objects back.
  - Pinned by `SkyMapHoverAndPictureTests` (21), including a CPU-surface render test that the wash
    reaches the pixels, and four of them were seen to FAIL with each rule removed in turn.
    [docs/todo/ui.md](docs/todo/ui.md) § Sky Map.

- [x] **Declutter the docked info strip** (SHIPPED 2026-09-11). Statistics roll up to their heading by
  default and, open, are a five-row TABLE at measured column stops instead of thirteen space-padded
  lines that a proportional face could not align; the white balance is gone from the strip and lives
  in a popover under a mark-only toolbar button (three colour discs), lit while the effective white
  balance is not neutral. The popover is a menu in everything but its contents (backdrop, Escape via
  the claimant, `OverlayOwnsPointer`). [docs/plans/viewer-prerelease-fixes.md](docs/plans/viewer-prerelease-fixes.md) P33.
  - [x] **The selection ring traces an extended object's own ellipse** (DONE 2026-09-11). The atlas
    had traced the object's shape since it shipped; the viewer always drew the two-circle fallback.
    `ImageRendererBase.TryDrawSelectionShape` now rings the object with its OWN projected outline --
    true axis ratio, true position angle -- built from the inputs the `[O]` overlay already uses for
    the same object (`OverlayEngine.ChooseMarkerKind`, the arcmin-to-pixel conversion off
    `WCS.PixelScaleArcsec`, `OverlayEngine.ComputeScreenPA`), so the ring sits concentric with the
    outline the overlay draws underneath rather than merely near it. Still a PAIR, so the selection
    reads the same as it always did, and the outer ring is a UNIFORM scale of the inner so an edge-on
    galaxy does not round off. A star keeps the circle even when the catalogue hands it a
    cross-linked shape (Antares inside rho Oph), by asking the same classifier the overlay asks. The
    two sizing constants moved to `OverlayEngine` beside the pinned-halo geometry, for the reason
    that block gives: a selection that changes size depending on which host drew it is not one
    marker. Pinned by `TheRingTracesAnExtendedObjectsOwnEllipse` (checked against the catalogue's own
    axes, not a literal) and `AStarKeepsTheCircularRingEvenCarryingAShape`; three sabotage runs, each
    failing exactly its one test.
  - [x] **A click on the panel's BLANK area used to fall through** (FIXED 2026-09-11, in both hosts
    together). Only the buttons and the close X registered a clickable region, so a press anywhere
    else on the panel reached the picture (or the sky map) underneath it -- re-selecting whatever was
    there, or clearing the selection outright. Each panel's background/border fill now carries ONE
    no-op `Clickable` covering the whole panel, registered before the buttons so they still win where
    they overlap it. Verified by sabotage in both hosts: removing the `Clickable` reproduces exactly
    the reported behaviour (the viewer's selection reads back `null` after a tap on the panel body)
    and fails only that one test.
  - [x] **Review pass on the three commits** (2026-09-11). The panel's WIDTH and HEIGHT were still
    literals in `SkyMapTab.Search.cs` (and a third copy in its test) while `ObjectInfoPanel` computed
    its own for the viewer -- the drift the hoist existed to close; all three ask `DesignWidth` /
    `DesignHeight` now, which shortens the atlas panel by the ~57 design units of dead space the
    literals carried (bottom edge unmoved, content unmoved). The floating panel also now honours
    `HideChrome` like the rest of the chrome, neither panel can climb out of the top of its host rect,
    and the atlas click test names the region it expects instead of asserting merely non-null. Three
    new/changed behaviours, each sabotage-verified on its own.
  - [x] **Constellation-figure stars now reach the overlay at wider fields** (2026-09-10):
    `OverlayEngine.FigureStarMagCutoff = 5.0` is a FLOOR under the field-of-view tiers for a star a
    figure line runs through, resolved through the cross-references since the figure set is keyed by
    HIP. A floor rather than a waiver (a thousand figure stars would flood a wide view) and rather
    than an assignment (which would DARKEN a 100 percent view). **Still open, as the user left it:
    whether it should apply only to figure stars inside the FRAME** -- one line, and it wants an
    eyeball at a wide zoom, along with whether 5.0 is the number.

- [x] **Finalise never stopped tracking; it only checked** (SHIPPED 2026-08-29). The step logged
  "Finalise: stopping tracking..." and then read `IsTracking` -- `SetTrackingAsync(false)` was called
  nowhere in the shutdown path, the only such call in `Session` being the sky-flat routine's.
  **It was invisible on any mount that can park**, because ASCOM's `Park` stops tracking by definition
  and `SkywatcherMountDriverBase.ParkAsync` halts both axes on its way home, so the motor did come to
  rest and the shutdown report's "Tracking stopped" line happened to be true by the end. On a mount
  with `CanPark == false` -- the iOptron SkyGuider Pro is the one in the shipped device list -- nothing
  in the whole finaliser ever stopped the motor, so a completed session left it tracking until the
  battery died or the payload reached the tripod.
  Now it commands the stop when the mount has a tracking switch and then reports **what the mount
  says**, so a tracker that cannot be commanded is never credited with a stop it did not make; that
  case logs a warning naming the mount and telling the operator to stop it at the hand controller.
  **The test needs `CanPark = false` to mean anything** (`FakeMountDriver.CanPark` became settable for
  it): with park available the fake's own `ParkAsync` clears the flag and the test passes with the bug
  still in place. Seen to fail against the original expression before being committed.
  Still open from the same review: **there are no hour-angle or altitude safety limits anywhere** --
  grep finds no `HALimit` / `LimitReached` / `SafetyLimit` -- so a failed flip has no backstop that
  stops a mount tracking into the pier. That is a feature, not this fix, and wants its own entry.

- [x] **Keep the measurement frames, and stamp each light with how well it was guided**
  (SHIPPED 2026-08-29). Two changes from one conversation about where paired blurry/sharp training
  data could come from.
  **`SessionConfiguration.SaveIntermediates`** (default OFF) keeps the frames a session takes to
  MEASURE something and would otherwise release unseen, under
  `Intermediates/<date>/<filter>/<frame type>/`: `FrameType.Focus` for every auto-focus V-curve rung
  plus the verification exposure at best focus (one folder per run), and `FrameType.Scout` for the
  FOV-obstruction probe frames, kept whatever the star count because a zero-star scout is the
  interesting one. It replaces `SaveAutoFocusFrames` and the never-read `SaveScoutFrames`, which had
  been documented as a real feature in six places including as a *precedent to follow*.
  - **Why an AF run is worth keeping at all:** it already sweeps 9 positions across 200 steps and then
    exposes once more at the fitted best focus, so each run is a labelled defocus ladder of ONE field,
    minutes apart, under one sky at one temperature, with `FOCUSPOS` on every frame and the in-focus
    anchor at the end. **The archive cannot supply this and never could** -- per-sub FWHM there runs
    p05 1.96 / p50 2.10 / p95 2.55 px with an intra-session p90/p10 ratio whose median is **1.04**, and
    a scan of all 245,213 indexed files found **zero auto-focus frames**, because N.I.N.A. and TianWen
    both measured the V-curve and threw the pixels away. Costs no exposure time. **Those FWHM figures
    are from the 2026-08-15 store, i.e. the detector BEFORE the deblending work.** Measured on 12
    sessions: the current detector finds 4% more matched stars and reads them 2% wider (+0.039 px on a
    ~2.2 px median), so the spread conclusion survives easily (1.04 against the 1.5 a training set would
    need) but the digits want a `--force-psf`.
    [docs/plans/ai-denoise-deconv.md](docs/plans/ai-denoise-deconv.md) 2.1b carries the measurements.
  - **Each kind gets its OWN `FrameType` rather than one `Intermediate`**, because path is cosmetic in
    this codebase and headers are truth: collapse them and the only way to tell an AF rung from a
    scout is the folder. A scout needs the card most -- it is in focus and points where the lights
    point, differing only in exposure, so nothing in the pixels would stop a scan ingesting it.
  - **A run is a DIRECTORY, not a filename convention.** The first cut encoded the run key in the
    name; writing the test found that the timestamp format contains underscores (`:` being illegal in
    a path), so splitting on `_` silently yields an *hour*-granularity key that merges two runs of one
    evening.
  **Per-light guiding statistics** (`ImageMeta.Guiding` / `GuidingStats`; `GUIDERMS` / `GUIRMSRA` /
  `GUIRMSDE` / `GUIDEPK` / `GUIDEN`, arcsec) reduce `Session.GuideSamples` over the frame's OWN
  exposure window. A survey of 41 N.I.N.A.- and SharpCap-authored archive headers found **zero**
  guiding keywords, so there was no convention to match and these are ours.
  - **Never a rolling session RMS** -- that answers "how is the rig doing tonight", a different
    question, and is actively misleading stamped on a sub taken during the other hour.
  - **Settling and dither samples inside the window are INCLUDED.** A live guiding display excludes
    them because a dither is a commanded move rather than an error; that reasoning inverts for a sub.
    If the guider had not settled while the shutter was open the frame IS smeared, and filtering would
    make the worst frames of the night report the cleanest numbers.
  - **Null is not zero.** An unguided rig writes no cards at all; `GUIDERMS = 0` would claim perfect
    guiding. `GUIDEPK` is carried because RMS is worst at describing the failure that actually ruins a
    sub -- one gust -- and averaging is exactly what erases it.
  - The session stamps `ICameraDriver.GuideStats` just before `GetImageAsync`, since the statistic is
    only complete once the shutter closes and that call is the one place an `ImageMeta` is built. A
    guiding concept on a camera interface is a layering compromise, taken because that is the
    established seam for session-stamped header facts (`Telescope`, `Filter`, `FocusPosition`,
    `Target`) and the alternative buys a tidier interface at the cost of a second way to get a card
    into a header.
  Pinned by `SessionAutoFocusFrameCaptureTests`, `GuideStatisticsTests` and end-to-end cases in
  `SessionScoutAndProbeTests` / `SessionImagingTests`; the last was SEEN to fail with the stamp
  removed, because a wiring no-op here is silent (empty windows, absent cards, perfectly good frames).

- [x] **Star detection: two tight stars are reported as two** (SHIPPED 2026-08-28). Detection used to
  report one star per blob and put it at the blob's centre of MASS, which on a pair is exactly where
  no star is. It now offers such a measurement to a deblender (`Image.StarDeblend.cs`) that fits a
  several-component model of a COMMON point-spread function to the aperture's pixels and reports the
  components. Measured on `RGGB_frame_bx0_by0_top_down`: the closest accepted pair falls **5.10 px ->
  2.57 px**, pairs closer than the wider star's suppression radius go **0 -> 18**, counts 2,983 ->
  3,065 at SNR 10 and 2,724 -> 2,769 at SNR 30, with HFD p50 unmoved (2.40 -> 2.39) and p95 TIGHTER
  (3.28 -> 3.14). Against planted ground truth (`StarPairDeblendGroundTruthTests`), 4 px pairs go from
  3 of 6 components recovered to **6 of 6** and 6 px pairs from **0 of 6** -- the merged blob was
  refused outright -- to 6 of 6, with components landing within 0.05 px of the planted position.
  **The three things that made it work, after two radius attempts failed:**
  - **It is a SHAPE test, not a distance test.** Two maxima are two objects when the dip between them
    falls below 85% of the fainter one. A radius asserts where a companion may be; a saddle asks
    whether one is there. It also disqualifies a saturated flat top for free (its saddle equals its
    peak), which every previous attempt needed a special case for.
  - **The fit is expectation-maximisation over a shared-width Gaussian mixture**, not
    Levenberg-Marquardt: no Jacobian, no matrix inverse, no line search, cannot diverge, fixed cost.
    The width is shared deliberately -- give each component its own and a bright one swells until it
    absorbs its neighbour.
  - **A phantom is structurally impossible this time**, which is what the reverted attempt could not
    say: the deblender never re-runs `AnalyseStar` from a new pixel (that is what fed a shoulder pixel
    a 29x29 box containing both stars and produced a midpoint phantom). It only ever REPLACES one
    accepted measurement with components inside its own aperture, or declines and leaves it alone.
  **Two things it deliberately does not do.** Below about 2*sigma separation (~2.6 px on that frame)
  two Gaussians have only ONE maximum, so no peak-based method resolves them and none is claimed; the
  synthetic curve reports that band rather than pinning it. And a deblend never COSTS a detection: if
  the components all fall below the SNR floor the merged measurement is reported instead (without that
  fallback the 28-star fixture went 89 -> 88, and it has no pair closer than 11.8 px to deblend).
  **Still open, unchanged by this:** 463 of 3,065 stars (15.1%) have above-threshold pixels reaching
  outside their `1.5 * HFD` mask, because HFD is a FLUX radius and understates a saturated star's
  footprint. That is the DUPLICATE half of the one-radius-two-jobs problem, not the merge half;
  duplicate pairs are pinned at 0, so it is currently costing nothing.
  Provenance note: the mask plus HFD scheme is the ASTAP method (LGPL-3.0). The deblender is not
  ASTAP's -- ASTAP does not deblend -- but it sits inside that method, so a rewrite of the surrounding
  code still needs the same licence care the original import did.
- [x] Own AI denoise/deconv training dataset (**P0 SHIPPED 2026-07-12**); `tianwen dataset build` runs end-to-end: single archive scan -> discover sessions + archive-wide header-matched calibration (`CalibrationResolver`, dark/bias libraries shared across sessions, masters build-once via fingerprinted `MasterCache`) -> star-count-led quality gate (`SessionFrameAnalyzer`; on a fast refractor star count is the discriminator, HFD *inverts* under transparency loss) -> register + integrate the session master **unnormalised** (`SessionRegistrar`, reuses the stacker's quad-match + Float16Staged integrator) -> structure-biased 256px cells -> **zero-skew** fp16 N2N tiles + JSONL manifest (`DatasetTileExporter`; every frame through the *same* `ChunkedNafnetRunner.ApplyInputStretch` inference pre-stretch) -> PSF/noise field-radius report (`DatasetPsfNoiseReport`) -> pinned **by-session** split (`DatasetSplitWriter`) -> in-run parity gate (`VerifyParityAsync`, maxDiff 0). License-clean (N2N sub-pairs + synthetic-PSF degradation, **no** RC-Astro outputs anywhere in the ML loop). `DatasetBuildRunner` (in `TianWen.AI.Imaging`) orchestrates; 22 tests, all validated on the synthetic RGGB fixture. **Real-archive run DONE 2026-07-15**: `D:\Astro-Dataset\2025-2026` holds 45 sessions / 4,958 subs / 121,500 tiles + 5 pinned test sessions. Two follow-ups before P1 trains on it: those tiles predate the master-flat pedestal fix (2026-08-03), and § 2.3b of the plan records the root order plus the two session groups (BAD LIGHT EXAMPLES, QHY294PROC) that no header gate excludes. Then P1 (NAFNet-32 N2N training on RunPod).** **Blocker for narrowband archives CLEARED 2026-08-02:** `SessionDiscovery.GroupSessions` keyed on `(SessionDir, Instrument, Target)` with **no filter**, so a mono Ha+OIII night collapsed into one session; the MAD star-count gate rejected the OIII frames as a left tail (they legitimately detect far fewer stars) and `SessionRegistrar` stacked both filters into one meaningless master, silently. The key now carries the filter. The obvious fix was insufficient: `Filter.FromName` is anchored, so `Ha 3nm` / `Antlia ALP-T` all canonicalise to one `Filter.Unknown`, and keying on the canonical name (what `MasterGroupKey` compares on) would have re-merged the lines; the key falls back to raw header text. That also disproved the plan's assumption that narrowband dispatch is a free `Bandpass` bit test, since `FILTCLAS` is TianWen-written and a N.I.N.A. frame's bandpass comes from the same anchored parse. See [docs/known-limitations.md](docs/known-limitations.md). The same sweep is also how [narrowband-colour](docs/plans/narrowband-colour.md) gets validated (and how we could **measure** our own dual-band crosstalk coefficients instead of sourcing a published table). See `docs/plans/ai-denoise-deconv.md`.
- [x] MiniViewer: optional lightweight mode that skips storing UnstretchedImage, for live preview where we never re-stretch, just keep stats + GPU texture. Saves ~140MB per displayed frame
- [x] Cache altitude chart as texture, only re-render the mouse follower overlay on hover, not the entire chart. Currently 20% GPU on mouse hover due to full chart redraw per frame
- [x] TianWen.Hosting remote API: ASP.NET Core Minimal API + WebSocket for headless Raspi operation. Multi-OTA native routes (`/api/v1/ota/{index}/camera/info`) with ninaAPI v2 compatibility shim (`/v2/api/*` → OTA[0]) so Touch N Stars works for single-scope setups. All 4 phases complete: read-only state, control, ninaAPI shim (equipment info/control, sequence, images, WebSocket, device lifecycle, guider graph, move-axis), profile CRUD + pending target queue. `tianwen-server` headless executable published as AOT binary for all platforms
- [x] Catalog cold-start Phase 2 (pre-bake init state) -- **CLOSED 2026-09-03; 2A, 2B and both halves of 2C are in, and nothing actionable remains**; see `docs/plans/catalog-binary-format.md` § Phase 2. **2A SHIPPED 2026-05-05:** `hd_hip_cross.bin.gz` snapshot (~350 ms saved). **2B SHIPPED 2026-05-05:** `simbad_merge.bin.gz` snapshot (~180 ms saved). **2C: the Tycho-2 bulk-load half is DONE** (measured 2026-08-31 at **0.3 ms** of init -- `ExpandTycho2` plus the background task closed it; `tyc2.bin` already carries a per-GSC-region offset table). **2C's BFS half was SUPERSEDED, not skipped** (`a7c7f9a2`, 2026-08-09): the plan offered pooled frontier buffers (~0 B/call, walk unchanged) or transitive closures pre-computed at init (dict hit, +~50 ms of init), and `_crossIndexClosures` -- a lazy `ConcurrentDictionary` memo on `TryGetCrossIndices` -- delivers the second option's outcome without its init cost, because the closure is a fixed function of an append-only-then-frozen table. Found by measuring rather than by this plan: the sky map's full-sky overlay gather allocated 78.1 MB a pass and `GC.GetAllocatedBytesForCurrentThread` deltas put **53.89 MB of it in `TryGetCrossIndices` alone**; memoising it (plus a `RaDecIndex` cell-merge cache and dropping a duplicate ask per object) took the gather to **22.98 MB and 105.7 -> 84.8 ms, Gen0/1000 8833 -> 1833**. The early-out for a missing row shipped with it and answers only a quarter of calls -- 113k of the 151k objects a sweep visits DO have a row -- so the cache, not the early-out, is what did the work. Cost stated: up to ~20 MB resident once a session has swept the whole sky, against 54 MB of churn per pass; revisit if this ever runs somewhere small. **`ReadTycho2CrossRefArrays` FIXED 2026-08-31:** it still lzip-decompressed `hip_to_tyc`/`hd_to_tyc` (274.7 ms) because only `tyc2.bin` had been given the build-time expansion; `ExpandTycho2CrossRef` now expands both into `obj/` (LFS-neutral, the committed `.lz` untouched as fallback). **Init 587 -> 343 ms, the blocking join phase 269.9 -> 0.0 ms.** The per-record base91 string round trip is ALSO fixed (`CatalogUtils.Tyc2CatalogIndex`): it allocated a string AND a boxed enum per star, **33.7 MB of Gen0 garbage and 11 collections -> 3.84 MB and 4**, where 3.84 MB is exactly the output arrays. Note `AbbreviationToEnumMember<T>`'s `Enum.ToObject` boxing affects every other catalog-parse caller too and is deliberately untouched. Largest remaining item is `hd-hip-cross` at 121.8 ms, and that IS the 2A fast path -- deserialise-and-apply, not the 330 ms recompute the snapshot replaced -- so it is the phase's intended end state rather than an outstanding item. Phase 2 as written targeted 280-400 ms and init went 729 -> 343 ms.

## Flaky CI Tests

- [x] **`ViewerControllerTests.SwitchingTheCropOffAndOnAgainRestoresItWithoutScanning` -- starved, not
  broken** (red on the push that landed 8.1, run 34929797564, `test-unit (ubuntu-latest)` only,
  2026-09-15; fixed the same day). `state.DisplayCrop` was still null when `WaitForCropAsync` gave up at
  5 s, on a 64 x 64 ring whose scan takes microseconds ONCE IT RUNS; the same commit passed the arm and
  debug legs, the PR run before and both pushes after. The scan is a `Task.Run`, four collections were
  in flight with pool work of their own, and the task had not been scheduled inside the budget. Two
  fixes, each half: `ViewerControllerTests` is in a `[Collection("Viewer")]` whose definition sets
  `DisableParallelization = true`, so nothing else runs beside it and its `Task.Run` meets an idle pool
  (`ViewerCollection.cs`; a plain collection only serialises the classes INSIDE it); and the three wait
  helpers bound a STALL at 30 s rather than the work at 5, with `WaitForCropAsync` leaving as soon as
  the scan is APPLIED (`ViewerController.IsCropScanPending`), so a scan that answers "nothing to crop"
  fails at the assertion with its real message instead of burning the bound. Same class of failure as
  the fake-time pump below: a wall-clock budget measuring the runner.

- [x] **`SessionImagingTests.GivenCloudsRollingInWhenStarCountDropsThenConditionDetected` -- NOT flaky;
  the pump's budget was measuring the CI runner** (red on `3f870333`, run 33687279158, 2026-09-02;
  fixed 2026-09-03). It failed `imagingTask.IsCompleted` after spending its whole 4-hour fake-time
  budget in ~4 s of wall clock on a 30-minute observation. `PumpUntilCompletedAsync` paced on
  `WaiterCount`, which is **global**: a fake guider's capture loop and a fake camera sit parked in
  `SleepAsync` more or less permanently, so "is anyone waiting?" answered yes whether or not the loop
  being driven had caught up. Meanwhile the imaging loop's tick is a `PeriodicTimer`, which registers
  no waiter **and coalesces** -- a tick firing while its continuation is still queued is dropped, not
  queued behind the last one. So every advance the loop did not observe was budget spent for nothing,
  and how many of those there are is a property of the thread pool, not of the session.
  **Measured, one machine, one test, nothing but scheduling changing: 30 minutes of observation cost
  33-50 minutes of budget** (idle 37-50 min, under 16-thread CPU load 33 min -- load made it *better*,
  because it slows the pump too; the full functional suite 41 min). CI needed ~8x and was never
  reproduced here, so the mechanism is measured but the CI red itself is not a red-to-green repro.
  **The budget now bounds a STALL, not the run**: `PumpUntilCompletedAsync` takes
  `progress: () => ctx.Session.ImagingLoopTicks` (new `Session.ImagingLoopTicks` seam) and resets the
  budget whenever the loop moves, so a starved runner merely takes longer while a loop that has
  genuinely stopped still trips it; a real hang stays bounded by `[Fact(Timeout)]`. It now **throws**
  with the counters instead of returning quietly into a downstream `IsCompleted.ShouldBeTrue` that
  cannot tell a stalled loop from a starved one. All 11 session-loop call sites pass the probe; the
  scout site keeps the run-bounding fallback deliberately. Pinned by `FakeTimePumpTests`, where the
  **no-probe case is the old pump kept green as the shape of this failure** -- the two tests differ
  only in whether the probe is supplied, and the probe case was seen to FAIL against the old
  semantics before being committed.
- [x] **The same clouds test detected no condition at all, and finding out why turned up three more
  bugs** (2026-09-03). It asserted only that it had SET `CloudCoverage`; measured, it produced **0
  "Condition deterioration detected" events** over 59-60 frames and never entered the recovery path.
  Four separate causes, each found by measuring the one before it:
  - **The cloud window was one pump iteration.** Clouds go in once the baseline exists (pump 9-39
    depending on the runner) and were cleared on `iteration > 10`, already true by then. Now keyed on
    `Session.ConditionDeteriorationCount > 0`, so the sky clears once the loop has actually noticed.
  - **`CloudCoverage` was not monotonic in obscuration.** The opacity ramp divided by `1 - threshold`,
    which IS the coverage, so the ramp FLATTENED as coverage rose. Measured on the imaging path
    against a 41-star clear baseline: 0.5 -> 19 stars, then 0.6 -> 25 and 0.7/0.8/0.9/0.95 all -> 26,
    with a cliff to 0 only at exactly 1.0 (a different branch, which renders no stars at all). The
    test's 0.8 sat on that 0.634 plateau, comfortably above the 0.6 gate. Fixed with a fixed edge
    softness.
  - **The glow carried no shot noise**, and the cloud is applied to an image whose noise is already
    baked in, so a uniform multiply-plus-constant scaled signal and noise together and left SNR --
    and the star count -- untouched. Only PATCHY cloud ever cost a detection. Fixed; uniform overcast
    at 0.9 went 38 -> 28 stars on its own.
  - **Extinction was capped at 90%**, i.e. 2.5 magnitudes, so the brightest stars punched through any
    overcast -- exactly what two guider tests' comments described before reaching for a hard 1.0 to
    get a starless frame. Replaced with Beer-Lambert (`exp(-6 * opacity)`). Final curve: 0.0 -> 41,
    0.3 -> 25, 0.5 -> 13, 0.8 -> 0. Pinned by
    `ConditionDeteriorationTests.StarCount_IsMonotonicallyNonIncreasing_InCloudCoverage`, seen to fail
    against the old model (`0.60->41, 0.80->41, 0.95->41`) before being committed.
- [x] **`fetchImagesSuccessAll` was a constant `true` on every single-OTA rig** (found 2026-09-03 by
  the repaired clouds test, fixed with it). `BitVector32`'s `int` indexer is a bit **MASK**, not an
  index, so `imageFetchSuccess[0]` is mask 0: it reads false forever and writes nothing. The vector
  was also seeded `new BitVector32(scopes)` (data = the OTA count, not zero), and
  `BitVectorExtensions.AllSet(n)` masked on `n - 1`, which is **zero for one OTA** -- and
  `(Data & 0) == 0` is unconditionally true. So the per-frame gate never worked, and the whole
  metrics block (focus-drift trend AND condition deterioration) ran on EVERY 5 s tick against
  whatever `_lastFrameMetrics` last held, instead of once per 30 s frame. Under cloud that is a
  pause/recover thrash: **297 deteriorations and 297 recoveries over 322 ticks, 4 frames written**,
  against 1/1 and 59 frames after the fix. Masks are now `1 << i` and `AllSet` uses
  `(1 << bitCount) - 1`.
- [x] **Dithering was expressed in ticks and only ever fired because that gate was broken** (same
  sweep). `tickCount % ditherEveryNTicks == 0` against a frames-to-ticks conversion: with the gate
  fixed, this block runs only on ticks where a frame completed, and those land on one phase mod
  `subExposure/tick` (1, 7, 13, ... for a 30 s sub on a 5 s tick), so a modulus keyed to multiples of
  6 coincides with them only if the phase happens to be 0 -- otherwise it never dithers at all, which
  is how `GivenDitherEveryNthFrame...DitheringTriggered` went red the moment the gate started
  working. Now counts the frames the block actually sees, which is what `DitherEveryNthFrame` says.
- [x] `SessionImagingTests.GivenHighAltitudeTarget...HighUtilization`: fixed: cooperative time pump (`ExternalTimePump + Advance`)
- [x] `SessionImagingTests.GivenDitherEveryNth...DitheringTriggered`: fixed: same root cause (SleepAsync pump race)
- [x] `SessionImagingTests.GivenFocusDrift...AutoRefocusTriggered`: fixed: same root cause
- [x] `SessionPhaseTests.AbortDuringCooling_StopsRampAndWarmsBack`: fixed: removed wall-clock CancellationTokenSource timeouts
- [x] `SessionObservationLoopTests.GivenAcrossMeridianTargetWhenHACrossesDeadbandThenFlipAndContinueImaging` -- fixed: root cause was `PlateSolveAndSyncCoreAsync` (`Session.Focus.cs`) being the only `StartExposureAsync` call site with no Idle/abort precondition. During a meridian flip (`PerformMeridianFlipAsync` -> `CenterOnTargetAsync` -> 5s plate-solve frame) the prior science sub-exposure could still be `Exposing` under the two-thread time-pump interleaving, so the driver rejected the solve frame with `InvalidOperationException: camera state being Exposing` (which also surfaced as `TotalFramesWritten=0` when it aborted the flip). Now aborts any in-progress exposure and lets `Download->Idle` settle before the solve exposure, mirroring the condition-recovery / obstruction-scout guards. Verified 20/20 green.
- [x] `SessionFilterTests.GivenSingleFilterPlanWhenImagingThenFramesCapturedWithoutFilterSwitch`: fixed: the LAST hand-rolled pump of eleven sites, migrated to `PumpUntilCompletedAsync`. Same root cause as the three above. This file had never picked up either half of the convention: `ExternalTimePump` was never set, so `SleepAsync` took its auto-advance branch and the test loop AND the session loop both called `_fake.Advance` concurrently (precisely the race that flag exists to prevent), while 30 x `Advance(30s)` outran a 3-minute, 6-tick observation window on roughly 100 ms of real budget per iteration. Measured on an idle box, n=8 per arm: the old pump captured 4 frames of the 6-tick window on all 8 runs, the shared pump 5 on 7 of 8; the old pump dropped to 3 when the box was busier, which is the load-sensitivity the mechanism predicts. **NOT reproduced on demand** (12/12 green under CPU load, 6/6 full-functional-suite runs), and the original assertion message was never captured, so the red-to-green transition is unproven and this rides on CI to confirm. Both async tests also gained the `[Fact(Timeout = 120_000)]` that every other session test carries: `PumpUntilCompletedAsync` waits indefinitely for the loop to re-park, so a genuine hang had no bound, including in this file's sibling test which already used the helper correctly but carried a bare `[Fact]`.

## Next Up

- [x] **`Lanczos3Value`'s weights were 72 percent of the default warp kernel, and five of every six
  sines were algebraically redundant.** DONE 2026-09-15, `Image.Lanczos3Weights`: **1.78x at 1024 sq
  and 1.80x at 2048**, and ten times nearer the window than the form it replaced. Twelve times the
  whole `[y, x]` pass bought on this box, which is the argument for pricing a loop's shares before
  optimising the visible one. `docs/architecture/image-pipeline.md`, "How a plane is READ".

- [x] **SkyWatcher driver: `RaToSteps`/`DecToSteps` only ever produce the Normal-state axis solution.**
  **DONE 2026-08-30 -- `SkyToSteps(ra, dec, PointingState)`**: a goto chooses the solution from
  `DestinationSideOfPierAsync` once and keeps it for refinement passes, a sync keeps the half the Dec
  encoder is in, `StepsToRa` reads the half off the Dec encoder, home boundary inclusive (Normal). Six
  `FakeSkywatcherMountDriverTests` cases, five seen to fail first; an unflipped fake now really flips on
  a re-slew. Hardware validation is queued in
  [docs/todo/hardware-validation.md](docs/todo/hardware-validation.md) items 1-3; `SetSideOfPierAsync`
  became the forced flip later the same day. Original finding: there was no through-the-pole branch (GSServer chooses one from the destination's hour angle), so a
  goto or sync to an EASTERN target lands the encoder model in `Normal` -- counterweight-UP in the
  driver's own convention (home = HA 6 h, counterweight down) -- and a session "flip" re-slews to
  identical encoder targets, i.e. moves nothing. Found 2026-08-30 while fixing the mount-limit
  pointing-state bug: the limit reads the driver's state, so on this driver it fires right after a slew
  to an eastern target and never on the west-tracking case. The port is GSS `origin/master`
  `Axes.RaDecToAxesXy`'s `if (axes[0] > 180) { X += 180; Y = 180 - Dec }` branch (Dec sign mirrored
  south FIRST), chosen per goto from the target's hour angle; `SyncRaDecAsync` picks the solution
  nearest the CURRENT encoder half (a sync says where the mount IS, it must not teleport the model
  across the pier). GSS itself never flips while tracking -- the flip IS the
  next goto landing on the other solution -- which matches how `Session` already re-slews.
  [docs/plans/mount-safety-limits.md](docs/plans/mount-safety-limits.md), "the pointing state".
- [x] **A COMPUTED pointing state must not feed the mount limit as if measured** (LX200 base, SGP,
  `FakeMountDriver`). **DONE 2026-08-30**: `IMountDriver.PointingStateSource` (None/Computed/Measured,
  default Computed) + `MountLimits.TrustedPointingState`; the flip gate keeps the computed answer. `MeadeLX200ProtocolMountDriverBase.CalculateSideOfPierAsync` and the fake derive
  pier side from HA (`>= 0 -> Normal`), SGP answers a constant `Normal`: the state a mount WOULD be in
  if its firmware always flipped. West of the meridian that reads as post-flip, so the meridian limit
  can never fire on those drivers -- wrong on any LX200-protocol mount that tracks past the meridian
  until the next goto. OnStep (`:Gm#`), SkyWatcher (Dec encoder), ASCOM/Alpaca report the mechanical
  state and are fine. Decide: computed answers reach `MountLimits.Evaluate` as `Unknown` (HA
  approximation, fires past the meridian), or split "measured vs computed" on `IMountDriver` -- the
  flip gate wants the computed one and has `DestinationSideOfPierAsync`. Same plan doc, same section.
- [x] **LAN.Lib 2.0 pin bump** (DONE 2026-09-06; the pin itself landed with `7d98d12a`). The discovery
  port moved 52821 -> 38821 (out of Windows' dynamic range, where Hyper-V/WSL exclusions killed
  tianwen-gui at DI resolution with WSAEACCES 10013 on 2026-08-30) and a failed bind now degrades to
  announce-only with `ILanTransport.Degradation` instead of throwing. All three halves are done: the pin
  is `2.0.*`, `TianWen.UI.Gui/Program.cs` logs the degradation once (the server gets it free from
  `LanDiscoveryHostedService`; the GUI drives the lifecycle by hand, so it owed the same line), and
  `docs/plans/remote-profile.md` carries 38821 plus a note on why it moved and what it costs.
  **Read BEFORE `StartAsync`, not after** as this entry used to say: the bind is in `UdpLanTransport`'s
  constructor, so the degradation is known the moment `LanDiscovery` is resolved.
  Verified by holding UDP 38821 with an `ExclusiveAddressUse` socket and starting the GUI: it logged
  `LAN discovery cannot listen on UDP 38821 (AccessDenied, 10013)` -- the same error code as the original
  incident -- and **went on starting** rather than dying at DI resolution, which is the whole point.
  A control run with the port free logs nothing. Old and new nodes on one LAN do not see each other --
  update every node together.
- [x] **Mount safety limits: P3's GUI half, P4, P5, and the P1 editor UI**
  ([docs/plans/mount-safety-limits.md](docs/plans/mount-safety-limits.md)).
  **ALL DONE 2026-08-30** -- editor (`PanelSection.MountLimits` + "Meridian Flip" config group with the
  clamp caveat), GUI watcher (`Program.cs`), P4 surfacing (telemetry -> wire -> mirror -> Home board ->
  feeds), P5 (`MountLimitKind.DriverEnforced`), plus: only a MEASURED pointing state drives the limit
  (`IMountDriver.PointingStateSource`), the watcher matches profiles by `Uri.DeviceKey`, SkyWatcher
  `SetSideOfPierAsync` is the forced flip. Still open (plan doc, "What is still open"): hardware
  validation of the SkyWatcher axis-solution port, the tier label in the editor, OnStep's axis angle, a
  limits editor row in the TUI equipment tab (the TUI has the config group + caveat, the watcher and the
  feed hook, not the editor), a session-less verdict surface on the server. Verified live in the GUI
  2026-08-30 (plan doc, "Live verification"): the watcher's verdict now reaches the Home card and the
  feed with no session (`MountLimitWatcher.VerdictFor`), and the start-up wedge that had blocked the
  live check was a profile scan probing every COM port (fixed in `DeviceDiscovery`, plus bounded serial
  writes/closes and per-port give-up in `SerialProbeService`).
  Original entry follows.
  **P0 + P1 + P2 shipped 2026-08-29, so a configured limit now actually stops a mount during a run.**
  The config persists on `ProfileData.MountLimits` (nullable = never configured = disabled) and is
  projected onto `Setup` by `SessionFactory`; enforcement is in `PollDeviceStatesAsync` (the poll,
  NOT the imaging tick -- it is what every slew wait and focus routine already calls) and routes to
  the new `ImageLoopNextAction.LimitReached`. Altitude comes from the new geometric
  `SiteContext.AltitudeDegrees`, unrefracted on purpose. 6 session tests + 9 altitude tests, two
  sabotages verified.
  **What remains, in rough order of value.**
  (a) **The P1 editor UI** -- the config persists and enforces, but nothing lets a user set it
  except editing profile JSON. Design notes from the 2026-08-29 session are in the plan doc ("P1
  editor UI: design notes"): a `PanelSection.MountLimits` after `Site` modelled on `BuildSite`, and --
  the user's ask -- the FLIP settings get their first UI in the same editor and are validated against
  the limit (a flip deadline the limit would clamp is flagged, both in minutes).
  (b) **P3, the half GSServer gets for free and we do not:** enforcement with **no session
  running**, which needs a watcher respecting the hub lease -- observe while a run owns the mount,
  act when nothing does. This is also what would protect the rig during a manual 2am slew, the case
  P1's placement on the profile was chosen for.
  (c) **P4 surfacing** (notification feed, a `LimitAlarm` on `ISessionTelemetry` for the Home
  board's rig card, warn threshold as a countdown beside `MeridianFlipUtc`) -- `Session` already
  exposes `MountLimitVerdict` for this, and nothing reads it yet.
  (d) ~~P1b axis modelling~~ **DONE 2026-08-30 for SkyWatcher**: `IMountDriver.GetAxisAngleAsync`
  (degrees from the counterweight-down home, hemisphere-corrected, null elsewhere), `Evaluate` prefers it
  (`|angle| - 90` = counterweight above horizontal, no clock/site/sync), `MountLimitVerdict.Basis` labels
  the tier. OnStep exposes raw steps but was not modelled.
  (e) **P5**: observe a driver-enforced limit rather than duplicating it, so a GSS-managed rig does
  not read as a malfunction.
- [x] RC-Astro enhancer integration: drive RC-Astro StarX/NoiseX/BlurXTerminator (encrypted ONNX, so via the `rc-astro` `--json` CLI, not in-proc ORT), preferred over the SETI Astro ONNX enhancers when the CLI is installed + the product is licensed. **Phase 1+2 SHIPPED:** `RcAstroCli` + NDJSON parser + FITS round-trip base, `RcAstroStarRemover`/`RcAstroDenoiser` (noise-adaptive `--dn`)/`RcAstroNonStellarDeconvolver`, deferred license-gated selector (`DeferredEnhancer` proxy, no subprocess at DI build/resolve), wired into `TianWen.Cli`. 13 tests. **Phase 3 SHIPPED (PR #59, 2026-06-30):** immutable threaded `EnhanceOptions`/`EnhanceTuning` (no mutable singleton) + shared `EnhanceOptions.TryParse`; CLI flags (`--ai-backend`/`--bxt-sharpen`/`--nxt-denoise`/`--nxt-iterations`) on `image sharpen` + `stack --enhance` (3a); per-step `EnhanceProgress` -> CLI printer (3b); interactive Enhance action in `tianwen-fits` (3c); `tianwen-server` `POST /api/v1/image/enhance` single-flight endpoint + `ENHANCE-PROGRESS`/`-COMPLETED` WS, presence-gated 503 when no pipeline (3d). Job-id/queue model deferred as a nice-to-have (`docs/plans/server-enhance-job-model.md`). See `docs/plans/rc-astro-enhancers.md`.
- [x] QHYCCD device support: native camera, filter wheel (camera-cable + standalone serial QHYCFW3), and QFOC focuser (Standard + High Precision) drivers. JSON-over-serial protocol for QFOC with typed records and AOT-safe `QfocJsonContext`. Three-phase discovery in `QHYDeviceSource`: cameras → serial probe → camera-cable CFW check
- [x] Weather overlay in planner: hourly forecast from Open-Meteo (free, no API key) with layered color emoji (rain/snow/thunder/fog/cloud/sun/moon), file-cached with 1h TTL + offline fallback. Weather as full device type (IWeatherDriver) with equipment/profile integration
- [x] Planner: show Moon phase + position; altitude curve on the chart with phase emoji (hemisphere-aware). Uses Meeus lunar ephemeris via VSOP87a pipeline
- [x] Moon penalty in target scoring: penalise targets within ~30° of a bright Moon (illumination × proximity factor). Compute angular separation per target in ObservationScheduler.ScoreTarget. **Shipped** (branch `feat/moon-avoidance`): per-bin `MoonGrid` (illumination × quadratic proximity, Moon-below-horizon gate); radius is an optional param (default 30, ON) on Schedule/TonightsBest/ScoreTarget. See `docs/plans/moon-avoidance.md`
- [x] Guider graph: connect dots with lines (Bresenham or anti-aliased) instead of scatter dots; users expect smooth curves like PHD2
- [x] Guider graph: scrolling window (last N samples) with dynamic Y scale and grid lines at integer arcsec
- [x] Guider graph: reuse the existing LiveSessionTab guide graph widget; the guider tab should show a larger version of the same graph, not a separate implementation. Extract shared graph rendering
- [x] DIR.Lib: add `FillEllipse`/`FillCircle`/`DrawEllipse`/`DrawCircle`/`DrawLine` primitives to `PixelWidgetBase`; `DrawLine` and `DrawEllipse` on abstract `Renderer` with CPU-optimized overrides on `RgbaImageRenderer` (midpoint ellipse, scanline quad, Span.Fill); GPU-optimized overrides on `VkRenderer` (rotated quad via FlatPipeline, ring shader via EllipsePipeline). Benchmarks in `DIR.Lib.Benchmarks`
- [x] Guider graph: show applied correction pulses (RA/Dec duration bars) alongside error; log-scaled bars (blue RA / orange Dec) extending up/down from zero line
- [x] Sky map: Stellarium-style time adjuster; step the observation instant relative to now (e.g. press `+1h` / `+1d` and it becomes Thursday 23:04 etc.), not a pick-a-date. Stores an offset from wall clock (minutes, hours, days, weeks) so the user can scrub forward and back. **Shipped** (branch `feat/top-5-todo`): `SkyMapState.TimeOffset` stacks on the base instant in the single `viewingTime` derivation in `SkyMapTab.Render`, so it drives everything downstream automatically:
    - sky color (feeds `SkyMapState.GetSunAltitudeDegCached` with the adjusted instant)
    - LST so stars / crosshair / horizon rotate correctly
    - planet positions via `VSOP87a.Reduce`
    - horizon fill and below-horizon label dimming
  Keys (in `SkyMapTab.HandleKey`): Up/Down = +-1h (Shift = +-10m), Left/Right = -+1d, PageUp/PageDown = +-1w, `N` = jump to the current night's midnight, `0` = reset offset, `T` = full reset (clears planner date too). The arrows now step the sky-map-scoped `TimeOffset` instead of mutating `PlannerState.PlanningDate`, so scrubbing is purely visual and never triggers a planner recompute. HUD strip shows the scrubbed site-local instant in blue with a compact signed offset chip (`SkyMapState.FormatOffset`, e.g. `(+2d 03h)`); the global status-bar wall clock stays the live anchor. Verified live (inspector): Up x3 -> sky rotated 3h + `(+3h)` chip + no planner recompute; `N` from afternoon -> next-day midnight + night palette; `0` -> back to live grey `HH:mm:ss`.
- [x] Guider graph: show dither events (markers/shading); yellow dashed vertical lines at dither events, dim yellow settling shading
- [x] Guider tab: keep looping guide camera frames during centering/slewing; call `LoopAsync` when not guiding so the guide camera feed stays live. Currently the guide loop stops during centering and the tab shows "Waiting for guider"
- [x] Guider tab: show calibration frames; render guide camera during calibration phase with star position and profile. Remaining: star movement vectors, step count, and calibration progress overlay
- [x] Guider: adaptive image-ready polling; sleep until near the expected end of exposure (N − `ImageReadyPollInterval`), then poll every 10ms, and in the final ~10ms poll every 1ms. Avoids wasting CPU on long sleeps while minimising latency at exposure end. Applies to `BuiltInGuiderDriver.CaptureGuideFrameAsync` and any other image-ready poll loop. **DONE (2026-06-22):** shared `ICameraDriver.WaitForImageReadyAsync` extension (`CameraDriverExtensions.cs`) drives a pure `NextImageReadyPollDelay(remaining, leadMargin)` cadence; one long sleep to `leadMargin` (= `External.ImageReadyPollInterval`, 50ms) before predicted end, then 10ms coarse, then 1ms in the final ~10ms (and on overrun); always strictly positive (no busy-spin). Routed `BuiltInGuiderDriver.CaptureGuideFrameAsync` (no timeout, guide-loop token bounds it) AND `MainCameraCaptureSource` (polar-align, keeps its exposure+5s budget) through it. `NextImageReadyPollDelay` unit-tested across all regimes (13 cases); 31 guide-loop/coupling/polar integration tests green; 0-warning build. The session main capture loop is tick-scheduled (not a naive fixed-interval `GetImageReadyAsync` poll), so it's intentionally out of scope.
- [x] Fake camera: apply mount tracking drift as pixel offset to star positions; DONE (PR #15 + PR #19): guide cam self-resolves the coupled mount from `IDeviceHub`, snapshots J2000 pointing per exposure and renders the deviation as pixel offset (`MountDriftPixels`); ST-4 forwards to the coupled mount so corrections physically move the encoders; `GuiderCalibration` converges end-to-end
- [x] Guider tab: guide camera image + crosshair (done). Remaining: star close-up + 1D intensity profile
  - [x] Add to `IDeviceDependentGuider`: `Image? LastGuideFrame`, `(float,float)? GuideStarPosition`, `float? GuideStarSNR`, `float? GuideStarHFD`
  - [x] Surface on `ISession` via `LiveSessionState.PollSession`
  - [x] `BuiltInGuiderDriver`: expose from `GuideLoop`'s `GuiderCentroidTracker`
  - [x] `FakeGuider`: generate synthetic guide frames with star field
  - [x] GUI: guide camera Canvas + crosshair overlay + SNR + frame counter
  - [x] GUI: star profile panel with 1D H/V intensity cross-sections + Gaussian fits + FWHM
- [x] Live session: show dither state; guider header shows `[Settling 0.42px]` with live distance, `[Paused (Slewing)]` during slews, correction arrows `[Guiding →142ms ↑38ms]`
- [x] Equipment tab: fully data-driven profile panel; replace hardcoded `RenderProfileSlot` calls (mount, guider, guider cam/foc) with a declarative slot model that includes special sections (site editing, focal length input, device settings) as metadata. Goal: single loop over all slots with pluggable section renderers. **DONE (2026-06-17, branch `feature/layout`, commit `31dc4e3`):** `EquipmentContent.GetProfilePanelSections(ProfileData)` emits an ordered surface-neutral `PanelSection` list (header / slots / site / guide-FL / device-settings / telemetry / per-OTA loop with FW-gated filter table / Add-OTA); `EquipmentTab.RenderProfilePanel` now just walks the list and dispatches each section via `RenderSection`. Chose the pragmatic **section-driver** over a full single-`LayoutNode` tree (the panel is mostly interactive + variable-height, so the tree's static-arrange model added little for high cost, see [docs/plans/layout-engine.md](docs/plans/layout-engine.md) Phase 2D). The section list is surface-neutral, so a future TUI panel can consume it with its own dispatch.
- [x] Store device secrets (API keys) in the OS credential store, not the profile URI; DONE (branch `feat/planner-weather-skymap`, commit `cd24b68`). The OpenWeatherMap `apiKey` used to live in `?apiKey=` on the device URI, so switching weather providers / re-discovery silently wiped it (replaced by the keyless discovered URI). New `ICredentialStore` keyed `{deviceId}/{settingKey}`: `WindowsCredentialStore` (Credential Manager via `LibraryImport`, visible in Control Panel) + `FileCredentialStore` (owner-only 0600 file fallback for Linux/macOS; libsecret/Keychain can drop in later behind the same interface). `OpenWeatherMapDevice`/`Driver` read from the store; masked `DeviceSettingDescriptor` edits route to the store (`AppSignalHandler.OnCommit`) and re-fetch weather; a leftover `?apiKey=` is ignored. Keyed per-device → shared across profiles (enter once). No migration (strip a stale `?apiKey=` by hand). **Follow-ups:** per-profile override (needs an active-profile-id provider at driver creation); equipment settings display reads the store so a stored masked value shows as set instead of `(empty)`; TUI parity check.
- [x] Fake camera: shift/change star field during slews. **DONE, and well past what the note asked.** `FakeCameraDriver` projects the field through the coupled mount's **true** pointing, not a fixed seed: the main camera stamps a per-exposure (true − believed) J2000 delta, the guide camera rides a live pointing snapshot at its own configurable sensor offset, and polar-misalignment drift, worm PE and guide pulses all move the projection centre (PE never appears in encoder reads, so it is visible only in the pixels, which is the point). The remaining determinism is the *seeing* draw, deliberately seeded so a coupled scenario replays identically. See the `FakeCameraDriver` header comments and the fake-misalignment-drift / fake-camera-mount-PE-sync work.
- [x] Fake camera: scale synthetic background noise with exposure duration in `SyntheticStarFieldRenderer`; long subs (≥60s) have unrealistically clean backgrounds, causing per-channel stretch to produce degenerate parameters. Real cameras accumulate sky glow + dark current + read noise over time. DONE (2026-06-02): sky background scales by exposure on all paths (`skyLevel = skyBackground * exposureSeconds`, `SyntheticStarFieldRenderer.cs:132,270,836`); star flux scales too, read noise stays fixed (correct).

- [x] Fake filter wheels should have pre-installed filters (realistic filter sets per device ID)
- [x] Planner: full rescan when site coordinates change significantly (>1°) instead of fast-path recompute; currently changing lat from -37 to 50 keeps southern-hemisphere targets with 0° altitude. DONE (2026-06-02): `AppSignalHandler.cs:489-510` runs full `ComputeTonightsBestAsync` when |Δlat| or |Δlon| > 1°, else fast-path `RecomputeForDate`.
- [x] Pinned items in planner should persist to disk; auto-save/load via `PlannerPersistence` keyed by profile+date, stored under `{OutputFolder}/Planner/{profileId}/{date}.json`
- [x] HFD drift detection via linear regression over last N frames (NINA uses `AutofocusAfterHFRIncreaseTrigger` with configurable `SampleSize` and `Amount` threshold); more robust than single-frame ratio comparison, reduces false refocus triggers. **DONE (2026-07-03):** the inline regression extracted into pure `FocusDriftDetector.EstimateTrendHfd` with its filtered-fit bug fixed (divisor was the window length, not the included-sample count, every skipped low-star/non-comparable sample biased slope + intercept); `FocusDriftSampleSize` (window, default 30) + `FocusDriftMinSamples` (default 5) on `SessionConfiguration` (`FocusDriftThreshold` = the Amount analogue); history cleared on drift-triggered refocus + target change (refocus-oscillation guard); `CircularBuffer<T>` rewritten lock-free (ImmutableArray + CAS `Snapshot`, `Session.GuideSamples` render-thread poll is now a free reference read instead of a 300-item lock-and-copy per frame). Pinned by `FocusDriftDetectorTests` + `CircularBufferTests`.
- [x] Flat-frame acquisition automation: **Phases 1-3 SHIPPED**. Phase 1 (panel/calibrator): pure `FlatExposureSolver` + `Session.TakeFlatsAsync` (per-OTA/filter; close cover → calibrator on → auto-expose → write `FrameType.Flat` → off), opt-in `TakeFlatsOnSessionEnd`. Phase 2 (twilight sky-flats, dawn + dusk): pure `SkyFlatExposureSolver` (re-metered per frame, Capture/Adjust/Wait/Stop) + `Session.TakeSkyFlatsAsync`, opens covers, solar-altitude window gate (`VSOP87a`), anti-solar zenith slew (`BeginSlewToZenithAsync`, tracking off so stars average out), `FlatSource` dispatch at the end-of-session hook (dawn) + a new session-start hook (dusk, cooled first; cloud-insurance for a fogged dawn). Phase 3 (on-demand + manual panel): `ISession.RunFlatsOnlyAsync` (connect-only-flat-devices → cool → capture → finalise, no wait-for-dark/focus/guider) behind CLI `tianwen flats` + `POST /api/v1/session/flats` (shared `FlatRunParsing`). A manual hand-switched panel is a **device** (`ManualCoverDevice`/`ManualCoverDriver`, a degenerate `ICoverDriver` mirroring the manual filter wheel: cover `NotPresent`, calibrator `Ready`-on-demand) assigned to the OTA cover slot and captured through the **same** calibrator path; no `ManualPanel` source, no session branching; registered via `AddDeviceType` so it round-trips through `TryGetDeviceFromUri`. Frames land under `Flats/<date>/<filter>/Flat/`; `MasterFrameBuilder` consumes by FITS headers. 37 tests. **Deferred:** a GUI `LiveSessionMode.Flats` mode on the Live Session tab (like PolarAlign/Planetary; assign 💡 Manual Light Panel + source dropdown + interactive prompt). See [docs/plans/flat-frame-automation.md](docs/plans/flat-frame-automation.md).
- [x] TUI Sixel preview in live session tab: **DONE** (verified 2026-07-29): `TuiLiveSessionTab.RenderPreview` watches `LiveState.LastCapturedImages`, adopts a new frame via async `AstroImageDocument.AdoptImageAsync` on a background `Task` polled by the render loop (the lock-free Task-handoff pattern, the ownership-transfer semantics the item warned about are honoured, the tab never touches the `Image` after adoption), and the Sixel raster draws from `PaintHost` at the arranged pixel size with a text fallback on non-Sixel terminals. With Console.Lib 4.8's buffered rendering the blit also declares its cell region (`BeginRawOutput`/`MarkRawRegion`), so the diff breaks around the picture.
- [x] **Guide-cam image stream over the hosted API** (**SHIPPED 2026-08-05**); `GET /api/v1/preview/guider` serves the live guide frame through the same `PreviewEncoder` and the same `X-Frame-Number` contract as the per-OTA previews, and `RemoteSessionMirror` fills `LastGuideFrame` / `GuideStarPosition` / `GuideStarSNR`, so the Guider tab renders a remote rig through the code that renders a local one. Its own route rather than an OTA index: one guider serves the whole rig, its frames arrive at guiding cadence rather than per sub, and it is wanted precisely while the science cameras are mid-exposure with nothing new to show. **The recorded blocker was already stale**; `ISessionTelemetry.LastGuideFrame` existed and `Session` already forwarded it. **The real hazard was sharper, and its primitive was unsound:** `GuideLoop` does `LastFrame?.Release(); LastFrame = frame;` every exposure, so a request encoding a JPEG across an await reads a buffer the camera has taken back (a valid JPEG of a flat grey rectangle, hence `GuidePreviewTests` modelling recycling as a clobber and asserting the star survives). `ChannelBuffer.AddRef` could not be used for it: it checked liveness and then incremented as two steps, so a borrower could resurrect a released buffer. Now `TryAddRef` (CAS, never resurrects) + `Image.TryLease` (all-or-nothing over the planes, distinct instance with its own one-shot `Release`, `false` when the race is lost). The change token also needed a **new** counter: `_guideFrameCount` counts frames the loop *corrected on* and sits past the star-lost `continue`, so it freezes during an outage while the camera keeps publishing; exactly when an operator wants to look. Both drivers funnel every publish site through one setter so the increment cannot be forgotten. Guide frames are a **separate opt-in** (`PreviewOptions.IncludeGuider`, default off) from the OTA thumbnails: the home dashboard shows science previews and never a guide frame. **Deferred:** the star-profile arrays + calibration overlay stay local-only; a per-poll array pair for an often-invisible panel, and it cannot be derived client-side (cross-sections from a stretched lossy preview give a confidently wrong FWHM rather than none), so it wants its own opt-in fetch like the frame got. See [docs/plans/remote-profile.md](docs/plans/remote-profile.md) § Deferred.
- [x] **Multi-rig dashboard / home screen** (**SHIPPED 2026-07-28**); `GuiTab.Home` is the landing tab (house icon, Ctrl+H, first in `TabOrder`), with one card per rig: local node plus every bound remote one, title = the rig and subtitle = the profile it runs. `HomeBoard.BuildCards` is the pure projection (unit-pinned) and `HomeTab<TSurface>` renders a per-frame snapshot published on `GuiAppState.HomeCards`; the tab never reaches into `RemoteRigRegistry`, mirroring how bound rigs already reach the equipment picker. All four invariants hold as designed: previews stay off, the board is read-only w.r.t. hardware (a card click posts the same `SelectRemoteRigSignal`/`SelectLocalContextSignal` the picker does), cards are built in the PRE-gate part of `PollPreviewTelemetry` and the board is **not** added to its `ActiveTab` gate, and the card section is content-sized (`WrapH` + trailing `Spacer`). Three prerequisites turned out to be missing and shipped with it: **`SessionPromptEventArgs.RaisedUtc` / `PendingPromptDto.RaisedUtc`** so the prompt badge ages from the node's own instant rather than from when a client noticed (an unknown age stays unknown, never filled in); **`GET /api/v1/session/profile`** so a node can report which profile it runs at all (`ActiveProfileId` had no way out of the node, and `/profiles` lists what exists without saying which is live), cached per connection and refreshed every 2 min; and **per-mirror poll backoff** (doubling to a 30 s cap, derived from a consecutive-failure count so one answer resets it, and a 404 counts as an answer). **Follow-ups shipped 2026-07-29** (branch `home-screen-dashboard`, PR #120): the card grew per-target progress (`target 2/3 · frame 23/100`, via `ScheduledObservation.PlannedFrameCount` on the wire so a mirror answers identically to a local session), cooling (worst camera from setpoint, freshness-gated), median HFD, guide RMS, last notification, and a meridian-flip countdown crossing as an INSTANT (`ISessionTelemetry.MeridianFlipUtc`); the board picks its shape (`Auto | Cards | Table` header selector: Auto swaps to a one-row-per-rig table when the cards' actual height doesn't fit, and the header says why); and the **TUI home board landed** (`TuiHomeTab` renders the *same* `HomeBoardLayout` tree via `CellMeasureContext.PixelAuthored`; the first tree genuinely shared across surface kinds, made livable by Console.Lib 4.8's diffing cell buffer: one cell per clock tick). **Next:** multi-night progress beside the cards, now its own plan, [docs/plans/multi-night-progress.md](docs/plans/multi-night-progress.md). See [docs/plans/remote-profile.md](docs/plans/remote-profile.md) § Deferred for the design record.

## More (full backlog by area)

The bulk of the backlog, the done-archive, and the unsorted inbox live under `docs/todo/`:

- [Sequencing & Polar Alignment](docs/todo/sequencing.md)
- [Devices & Drivers](docs/todo/drivers.md)
- [Imaging, Stretch & Colour](docs/todo/imaging.md)
- [Astrometry](docs/todo/astrometry.md)
- [UI & Rendering](docs/todo/ui.md)
- [Guider](docs/todo/guider.md)
- [Infrastructure, Quality & Testing](docs/todo/infra.md)
- [Inbox (unsorted Slack self-notes)](docs/todo/inbox.md): swept through **2026-09-08**; re-read the DM only back to that watermark, and check `imaging.md`, `ui.md` and `docs/plans/viewer-prerelease-fixes.md` too (several passes filed notes straight there rather than through the inbox)

Root-cause notes for limitations/bugs: [docs/known-limitations.md](docs/known-limitations.md).
