# TODO -- Inbox (unsorted Slack self-notes)

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Inbox: consolidated from Slack self-notes (2026-06-02, re-swept 2026-08-02)

New, still-actionable TianWen items lifted from the Slack "messages to self" brain-dump (Mar-May 2026),
deduped against the rest of this file. Date in parens is when the note was written. Triage into the
sections above when picked up. Notes that turned out to be already DONE or already tracked elsewhere are
intentionally NOT repeated here.

**Sweep watermark: the Slack self-DM is swept through 2026-09-19.** Earlier passes are folded in below: the
2026-06-02 consolidation below (Mar-May), and a 2026-07-07 pass that filed the ROEBA banding and Hough
star-halo notes **straight into [imaging.md](imaging.md)** with full detail rather than through this file,
which is why they are absent here, a 2026-08-27/28 pass that filed the whole FITS-viewer band straight
into [viewer-prerelease-fixes](../plans/viewer-prerelease-fixes.md) as P11-P21, plus the chart and web-build
notes into [ui.md](ui.md), and a 2026-09-20 pass that filed a Sky Map note into `ui.md` and a Save-dialog
gap into `viewer-prerelease-fixes.md` as P35 (filed as P30, a number already taken; renumbered
2026-09-20). When re-sweeping, read the DM back only to this watermark,
and check `imaging.md`, `viewer-prerelease-fixes.md` and `ui.md` before concluding a note was never
triaged.

### Sky Map
- [x] **Pan/zoom jank at sub-90deg FOV (worst with SCP in view)**: FIXED 2026-06-11: the overlay Phase A cache (`VkSkyMapTab.RenderObjectOverlay`) was keyed on the exact view matrix below `WideFovThresholdDeg`, so every drag frame re-ran the catalog grid scan (`GatherSkyMapOverlayCandidates`; pole-in-view = full-RA Dec strip, ~16k cell lookups -> 100-240 ms/frame; ~5k cells elsewhere -> 40-90 ms). Fix: key on the unprojected view centre quantized to FOV/8 cells + FOV quantized to ~10% log steps, and widen the gather margin to `max(1deg, 0.15 x FOV)` (RA scaled 1/cos dec) so the cached set covers every view inside a cell; Phase B's per-frame projection culls as before. Measured at the SCP all-layers-on: 8 zoom/time stimuli -> ONE 93 ms frame (the legitimate cell-boundary rebuild) vs 1-3 slow frames per stimulus before.
- [x] Optional follow-up: move overlay Phase A (candidate gather) to a background task (the `TryApplyPendingStarBuild` pattern) so even cell-boundary rebuilds never block a frame; DONE 2026-06-11 (PR #22, `5d501c1`): Phase A gather runs off the render thread.
- [x] Search box + click-to-goto (slew to clicked object) (2026-04-16); DONE: search panel (`OpenSkyMapSearchSignal` + query-changed incremental results) and click-select (`SkyMapClickSelectSignal`) both open the info panel, whose Goto button slews the connected mount (`SkyMapSlewToObjectSignal`); object labels are click-targets too (PR #24).
- [ ] Compass markers + horizon markers (2026-04-16)
- [x] "N" key jumps the sky to local midnight (2026-04-18); DONE (branch `feat/top-5-todo`): `SkyMapState.ComputeMidnightOffset` lands the sky on the current observing night's 00:00 (forward to tonight's upcoming midnight when local time >= noon, back to this morning's 00:00 otherwise); pure + unit-tested. Pairs with the time-adjuster item above.
- [x] "Show in planner" action from the sky map (2026-04-18); DONE: "View in Planner" button in the info panel posts `ViewInPlannerSignal` (button width fixed in PR #24).
- [ ] Compute edge crossings (clip constellation / grid lines at the viewport edge) (2026-04-04)
- [ ] Load Gaia stars from Stellarium `.dat` files (the 3-vector unit-pos pipeline is already DONE; only the loader is missing) (2026-04-04, 2026-05-19)
- [ ] Bake a nebulosity layer into the baked Milky Way background image (2026-04-18)
- [ ] Share more rendering code between the Sky Map and the FITS viewer (2026-04-04)

### Planner / Session GUI
- [x] Planner: sensor-proximity companion suggestions: when pinning a target, surface catalog neighbours that fit on the same sensor FOV (e.g. pin the Lagoon → suggest the Trifid, ~1.4° away). (2026-07-07) **DONE (Smart Framing, `d742612f`)**, and it went further than the note asked: rather than a "nearby: …" hint, co-framable targets *collapse into one scheduled observation* at the combined-footprint centroid (`FramingGrouper` + `FramingPlanner`, `TianWen.Lib/Sequencing/`). FOV comes from `OTAData.CameraPixelSizeUm/SensorWidthPx/SensorHeightPx`, auto-captured on first camera connect; neighbour discovery is grid-local via `DeepSkyCoordinateGrid`, not a catalog scan. See CLAUDE.md § Smart Framing and [docs/plans/smart-framing.md](../plans/smart-framing.md).
- [ ] Second planner view: all unique pinned objects plotted over their bounding visibility timespan (2026-04-18) (confirmed not implemented)
- [ ] Indicate a "light" / coverage marker under targets that actually have scheduled exposure time (2026-03-25) (the Tonight tab already goes read-only with Start disabled during a running session; only the per-target coverage marker is missing)
- [ ] Site change should unpin pinned targets when coordinates change, and must NOT invalidate cooler setpoint temps (2026-03-27) (unpin: confirmed not done)
- [ ] Planner input bugs: Ctrl+V paste does nothing, input field too small, Enter does not commit the "Today" date edit (2026-04-07)
- [ ] Replace the Live Session tab icon with a Milky Way image (2026-03-24). Premise has moved: the tab no longer has *one* icon. `VkGuiRenderer.TabChrome` swaps it per mode (📷 idle, 📸 running, 🧭 polar, 🪐 planetary, 💡 flats), so the camera-flash the note objected to is now only the running state. Re-decide what this is actually asking for before doing it: a Milky Way glyph would either replace the idle icon or break the per-mode scheme.
- [ ] Make the Windows taskbar entry more dynamic (progress / session state) (2026-04-02)

### Equipment / device UX
- [x] Gate "Connect All" on discovery completion (2026-04-30). DONE: `EquipmentActions.ConnectAllStatus` computes visibility/enabled/label once for every surface and is `enabled = !isDiscovering && allDiscoverable && anyNotConnected && !anyPending`, showing "Discovering…" while a scan is in flight.
- [ ] Clicking a device class should ensure all devices of that class are visible; vendor text is hard to read (2026-04-23)
- [ ] Better feedback than logging "Expected Camera, got mount" on a type mismatch (2026-04-23)
- [~] "Hold Shift reveals extra options" pattern (2026-04-16). **Discover half DONE**: `EquipmentTab.DeviceList.cs` posts `DiscoverDevicesSignal(IncludeFake: shift)`, which is how fake devices are surfaced on a profile that does not already reference one. **Still open: Shift = loop instead of single-click preview**, and the pattern is not generalised (each site hand-reads the modifier, there is no shared "shifted affordance" convention or any hint in the UI that one exists).
- [ ] Manual device creator UI (host / port fields) (2026-04-20) (overlaps the "Add unseen device" OnStep follow-up above)

### Sequencing / Session
- [ ] Avoid auto-focus when approaching the meridian (2026-05-14)
- [ ] Custom horizon file support (2026-03-17) (overlaps the deferred horizon-mask sub-plan)
- [ ] Configurable parking position (2026-03-17)
- [ ] Memoize pier side / polarity (2026-03-17)
- [ ] Spares: compute from higher-priority list items that conflict with the accepted schedule, prefer same object type (2026-03-23) (refines the existing spare-target fallback)
- [ ] Revisit imaging / guider / polar-align loop tick rate; see if it can be increased in real (non-fake) time (2026-05-01) (pairs with the GCD/6 faster-tick item above)

### Drivers / hardware
- [ ] Canon lens stepper as a special focuser: model manual vs automatic telephoto lenses as a special optical system so we know when auto-focus is usable; test that manual focus works during a session (2026-04-19)

### Stacker (no section exists yet)
- [ ] Support 3rd-party master frames (bias/dark/flat from other tools) (2026-05-19)
- [ ] Auto-pick flats by matching object time + filter (2026-05-19)
- [ ] Download Gaia SP stars (2026-05-19) (same source as the Sky Map Gaia loader)

### Stretch / Astrometry
- [ ] Auto-stretch ("MML") should use the object DB for grounding (object type + shape) (2026-05-07)
- [ ] Debug why so few stars match in Tycho-2 SPCC (2026-05-19)
- [ ] MCP: "best of tonight / this week / this month" tools (2026-05-21) (pairs with the MCP server + generalise-TonightsBest items above)

### Build / infra / docs
- [ ] Shrink git fetch size (~500 MB of `.zip` / `.gz` / `.lzip` data files) (2026-04-19)
- [ ] Create a subset of the emoji font to cut size (2026-03-26) (pairs with fetch-size)
- [x] Mention FC.SDK in the skills docs (2026-04-19). DONE: it is in the library tables of `release-lib`, `check-ci` and `sibling-status`.
- [~] Investigate AOT trim warnings (2026-04-19). **CSharpFITS (IL3053) is gone**; the publish now emits exactly 2 third-party rollups, both from `LibUsbDotNet` (IL2104 + IL3053), for optional Canon-over-USB discovery. That lib ships no AOT annotations and we deliberately do not mask the warning, so this is **accepted, not fixed** (CLAUDE.md § Native-AOT correctness records the expected count). Only reopen if LibUsbDotNet is dropped or annotated upstream.
- [ ] CI: ensure publish does not run while tests are still going; reduce server AOT publish warnings (2026-04-19)
- [ ] App self-update detection (2026-04-26)

### Code quality
- [ ] Move `RGBAColor32Extensions.cs` to a base layer (DIR.Lib) (2026-04-26)
- [ ] Use `Vector2` where we currently pass `PointF`-style pairs (2026-04-10)
- [ ] Document / clarify how `ResilientCall` interacts with collision detection (2026-04-26)
- [ ] Maybe support .NET Standard 2.0 for wider lib reuse (2026-05-02)

## Inbox: Slack self-notes, June-August 2026 (swept 2026-08-02)

The band the 2026-06-02 consolidation did not reach. Most of it had already been closed by the time it was
read back, which is recorded here rather than dropped: a note that silently vanishes reads as never-triaged
the next time the DM is scanned.

### Still open
- [ ] **Narrowband colour calibration** (2026-07-20): filed with the full shape in
  [imaging.md § Colour: narrowband](imaging.md). SPCC is broadband-only today, so an Ha/OIII/SII master has
  no calibration path at all.
- [?] **"sdl: use script language for faster and reliable controlling"** (2026-06-08): probably satisfied
  sideways and never noticed: the inspector grew a `batch` verb, and `list_signals` / `post_signal` are
  source-generated over every `*Signal` type (`SignalDirectoryGenerator`), so the whole app bus is drivable
  by name with no runtime reflection. That is a scripting surface in everything but syntax. **Confirm the
  intent before closing.** If the ask was a persistent, re-runnable script *file* (a scenario you can
  check in and replay), that does not exist.
- [x] **"sky atlas bug: obj selection"** (2026-06-08): **CLOSED 2026-09-20 by the hover highlight.**
  It was too terse to match to a fix with confidence for two sweeps -- two candidates had landed in
  the meantime (`b920c53a`, selection reticles + alt/az live across a date/time scrub; and the
  dark-nebula click resolver honouring the `[D]` toggle) and neither was obviously it. The user's own
  reading, 2026-09-20: *"probably fixed by supporting mouse-over highlighting"*. So the complaint was
  never that selection picked the WRONG object, it was that on a crowded field you could not tell
  WHICH object a click would take without clicking and reading the panel. Shipped as
  `SkyMapState.HoverTarget` + `SkyMapTab.Hover.cs`; see [ui.md](ui.md) § Sky Map. Reopen with a repro
  if the bug you saw was something else.

### Closed by the time it was swept
- [x] Sky atlas: `D` toggles dark nebulae (2026-06-08): shipped; the click resolver honours the layer.
- [x] SDL inspector: press-and-hold for ~2 s (2026-07-19): shipped as the inspector's `press_hold`.
- [x] Console.Lib: a tool to cat markdown to the console (2026-06-18): shipped as `Console.Lib/src/MdCat`.
- [x] ImageMagick HDR reference (2026-07-06): superseded by the shipped Ultra HDR gain-map export
  (`stack --output-format uhdr`), which does per-pixel highlight recovery off the pre-MTF signal.
- [x] Planner sensor proximity (2026-07-07): see the ticked entry above; shipped as Smart Framing.
- [x] ROEBA row/odd-even banding (2026-06-20) and Hough star-halo detection (2026-07-03): both filed into
  [imaging.md](imaging.md) by the 2026-07-07 pass, with more detail than the notes carried.
- [x] xUnit "targets retired ROI-centroid path" (2026-06-09): tracked as the `IncrementalSolverTests`
  rewrite in [astrometry.md](astrometry.md).

### Not TianWen
`pdf-viewer` viewport API and window-chrome tabs, the title-block regression-extraction idea, and the
dotcc WASM demo page. Left in Slack; they belong to other repos.

## Inbox: Slack self-notes, 2026-08-02 -> 2026-08-29 (swept 2026-08-29)

The band since the previous watermark, and the first sweep where **most of it was already filed** --
the FITS-viewer notes went into [viewer-prerelease-fixes](../plans/viewer-prerelease-fixes.md) and the
chart/web ones into [ui.md](ui.md) as they were written, days after being noted. So this section is
mostly a map from note to home. Recorded anyway, for the reason the previous sweep gave: a note that
silently vanishes reads as never-triaged the next time the DM is scanned. Every note in this band is
TianWen; there were no other-repo strays to leave behind.

### Already filed when the sweep ran

**State re-checked 2026-09-20**, because a map into other files goes stale where the files move on and
then reads as more open than it is: five of these rows were out of date by three weeks.

| Note (date written) | Home | State there |
|---|---|---|
| Star profile / colour / name on hover (08-19) | [ui.md](ui.md) FITS Viewer | open |
| `--help` shows no version; AI discovery status + download options (08-21) | P11 | half fixed -- version + status shipped, **download open** |
| Needs more in-depth doco (08-22) | P13 | **FIXED** |
| An empty instance should adopt an opened file (08-22) | P14 | **FIXED** |
| Gain/ISO and offset missing from the right pane (08-22) | P12 | **FIXED** |
| Carry calibration/stretch when stepping between frames of the same type (08-22) | P19 (blink mode) | **FIXED** 2026-09-04 |
| Right-click to copy colour / RA-Dec (08-24) | P17 | **FIXED** |
| Show detected object name; clickable object mode (08-24) | [ui.md](ui.md) FITS Viewer | open, and **explicitly undecided by you** |
| Share link to the web viewer, needs `&t=<capture time>` (08-24) | P20 | **FIXED** 2026-09-04 |
| Show debayered channels via "the new `AsChannel*`" (08-26) | P21 | backlog. Note there is no `AsChannel*` API anywhere; that note resolved to `Channel.AsSpan()` |
| Save as seen on screen, Save-As, iconise Open/Save (08-27) | P18 | **FIXED** |
| Atlas spark lines (08-21) | [atlas-planet-detail.md](../plans/atlas-planet-detail.md) A1 | planned; A1 is literally titled after this note |
| Log / time-compressed graphs (08-22) | [ui.md](ui.md) Charts | open |
| Web build: fake profiles for framing, Milky Way texture, copy-link-to-point (08-24) | [ui.md](ui.md) Charts | Milky Way texture **DONE** 2026-09-17; fake profiles and copy-link still open |

### Closed since the note was written

- [x] **Auto stretch mode, and it should be the default** (08-27). SHIPPED. `StretchMode.Auto` is a UI
  intent resolved by `StretchModeExtensions.ResolveAuto` (moved into TianWen.Lib 2026-09-02 to share
  with the Explorer thumbnail renderer) *before* any `StretchUniforms` is built --
  never a shader mode -- and `ViewerActions.DefaultStretchMode` is `Auto`. It resolves to Linked when a
  colour calibration is active and Unlinked when it is not, which is the behaviour the note asked for.
  Pinned by `ViewerActionsTests.DefaultStretchMode_IsAuto` and `ColorCalibrationToggleTests`.
- [x] **FC.SDK has unpushed work (USB)** (08-20). Pushed: `../FC.SDK` is clean and level with
  `origin/main`.
- [x] **Canon / FC.SDK 3.0.\* re-pin** (08-19). Not a task -- that note is a *record* of shipped work.
  Its findings (`NumX` snapping to a zoom level, `VideoRoi` in sensor px, `CanJogRoi` true only while
  magnified, host-side pan clamping) live in CLAUDE.md and
  [live-planetary-capture.md](../plans/live-planetary-capture.md).
- [x] **Build a deblur model from focus-shifted frames** (08-28, item 4, first half). SHIPPED
  2026-08-29 as `SessionConfiguration.SaveIntermediates` / `FrameType.Focus`; see
  [ai-denoise-deconv.md § 2.1b](../plans/ai-denoise-deconv.md) and [TODO.md](../../TODO.md). **The
  note's premise did not survive measurement**: it proposed mining the pre-N.I.N.A. archive, but a scan
  of all 245,213 indexed files found **zero** auto-focus frames, because N.I.N.A. and TianWen both
  measured the V-curve and threw the pixels away. The ladders have to be captured going forward. The
  *airmass* half of the same note is a different idea and is newly filed below.

### Newly filed by this sweep

- [x] Siril gradient-correction script as a reference (08-18) -> [imaging.md](imaging.md), deferred CLI verbs. **DONE 2026-09-02**, see [background-extraction.md](../plans/background-extraction.md) "Reference review".
- [ ] Audit that every exit path stops / parks / flips (08-28 item 1) -> [sequencing.md](sequencing.md)
- [ ] Atlas as a quasi-goto aid for a slew-less mount, incl. the flip-Dec geometry (08-28 item 2) -> [ui.md](ui.md)
- [ ] Resume an interrupted session, e.g. a mosaic stopped by dew (08-28 item 3) -> [sequencing.md](sequencing.md)
- [ ] Airmass-paired real degradation pairs (08-28 item 4, second half) -> [ai-denoise-deconv.md § 2.1c](../plans/ai-denoise-deconv.md)

## Inbox: Slack self-notes, 2026-08-29 -> 2026-09-08 (swept 2026-09-08)

Eight notes, and the band is unusual in two ways. There is a nine-day gap in the middle (nothing at all
between 2026-08-28 and 2026-09-05), and unlike the previous sweep none of it had been filed in advance,
so this pass did the triage rather than drawing a map. Six notes are TianWen, one is a bare link, one
belongs to another repo. Three of the six already had a root cause in the code by the time they were
filed, and it is recorded where the fix goes rather than here.

### Filed by this sweep

**All six TianWen notes here are now CLOSED** (re-checked 2026-09-20): P23-P26 were all fixed on
2026-09-08, the text-selection defect on the same day in DIR.Lib 8.14 (TianWen is pinned at `10.2.*`,
so it is long since in), and the narrowband Auto-stretch cast by the line-selective veto recorded in
[imaging.md](imaging.md).

| Note (date written) | Home | What it turned out to be |
|---|---|---|
| Viewer: holding Space while blinking start/stops rapidly, and blink should keep the frame in view (09-07) | [viewer-prerelease-fixes](../plans/viewer-prerelease-fixes.md) P23 | Two defects, neither of them in the blink: the host drops SDL's repeat flag, so a toggle runs once per auto-repeat, and `ViewerActions.SelectFile` never scrolls the list |
| Viewer: the A/B divider bar leaves residue in the non-imaging canvas area (09-07) | P24 | P15's class again, but the sweep rect already spans the letterbox, so the suspect is what PAINTS that band, not what declares it |
| Viewer: an auto-crop button for stack artefacts and NaN areas (09-07) | P25 | The rectangle exists on the stacking side (`_autocrop.fits`); what is missing is reaching it for an opened, possibly foreign, master |
| Viewer: the help menu should auto-create an issue with logs attached (09-07) | P26 | The `?` panel already knows the version and the AI status; a prepared issue beats an API call, and a log is never attached unseen |
| Web atlas: F3 with something already selected renders the text unreadable (09-07, screenshot) | [ui.md](ui.md) | Root cause found, and it is not the atlas: `TextInputRenderer` fills the selection OVER the glyphs, so every text field in every host does this |
| Viewer: Auto picks Linked on an HOO master and casts, Unlinked looks right (09-06) | [imaging.md, Colour: narrowband](imaging.md) | `ResolveAuto` cannot see a narrowband frame; it knows only that the frame is colour and that a calibration exists |
| `Ionfreefly01/siril-spectral-extract` (09-05, link with no comment) | same section | Synthesises narrowband layers from OSC by fitting R/G/B weights to a requested passband, with continuum rejection; GPL-3.0-or-later; informs Phase 1-2, not the blocked Phase 4 |

### Not TianWen

The PDF viewer note (09-05: hovering a 3D object makes the wheel zoom the model rather than the page).
Left in Slack, where the repo that owns it can pick it up.

## Field note: astrophoto.app, and what a competitor sources for free (2026-09-15)

Not a Slack sweep. `astrophoto.app` came up while checking domain availability for the viewer
(the Store app is **Astro Photo Viewer**), turned out to be live, and was worth reading because it
answers "where does a free web app get sky conditions and target imagery" with URLs rather than
guesses. Registered 2026-05-11 at Gandi on a one-year term, Cloudflare in front, and built with
Lovable — the markup still carries `twitter:site` `@Lovable` and an OG image on a `lovable.app`
preview bucket. Two tabs, *Sky* and *Targets*. No backend of its own except one asteroid endpoint;
everything below is fetched client-side from the browser, keyless.

| What it shows | Where it comes from |
|---|---|
| Cloud cover, visibility, humidity, dew point, wind, **jet-stream wind** | `api.open-meteo.com/v1/forecast?…&current=cloud_cover,visibility,relative_humidity_2m,wind_speed_10m,dew_point_2m,wind_speed_250hPa&hourly=cloud_cover&forecast_days=2&timezone=auto` |
| Target thumbnail + blurb | `en.wikipedia.org/api/rest_v1/page/summary/{title}` → `thumbnail.source`, `originalimage.source`, `extract` |
| Location | `geocoding-api.open-meteo.com/v1/reverse`, plus `api.bigdatacloud.net/data/reverse-geocode-client` as a second source |
| ISS passes | `celestrak.org/NORAD/elements/gp.php?CATNR=25544&FORMAT=tle` |

Three things fall out of that for TianWen.

- [x] **Moved to [seeing-forecast](../plans/seeing-forecast.md) (2026-09-17).** **`wind_speed_250hPa` is a seeing forecast for free, and we do not ask for it.**
  `OpenMeteoDriver.HourlyParams`/`CurrentParams`
  (`src/TianWen.Lib/Devices/Weather/OpenMeteoDriver.cs:22-23`) request eleven fields; wind at the
  250 hPa level (~10 km, the jet stream) is not among them, and it is the standard cheap proxy for
  seeing — fast jet overhead means a soft night no matter how clear it is. Adding it costs one
  query parameter on a request we already make hourly.
  **Do NOT put it in `IWeatherDriver.StarFWHM`**: that member is contracted as *"seeing measured as
  star FWHM in arcsec"* (`IWeatherDriver.cs:48`) and `OpenMeteoDriver` correctly returns NaN for it.
  A forecast proxy is not a measurement. It belongs as a new field on `HourlyWeatherForecast`
  (`Devices/Weather/HourlyWeatherForecast.cs`) feeding a planner-side indicator, next to cloud cover,
  where the user reads it as "conditions tonight" rather than as an instrument reading. Pairs with
  [site-conditions](../plans/site-conditions.md), which established the driver as the live tier for
  pressure/temperature but only for refraction.

- [x] **Moved to [object-imagery](../plans/object-imagery.md) (2026-09-17)**, which also replaces the title guess below with a verified identity bake. **Wikipedia's REST summary would give planner/sky-map targets an image and a description.**
  We already build the article URL — `PlannerDetails.WikipediaArticleBase`
  (`src/TianWen.UI.Abstractions/PlannerDetails.cs:128`) plus `GetWikipediaUrl`, which resolves the
  MAIN catalogue designation into the slug and is unit-tested (`PlannerDetailsTests`) — so the hard
  half (catalogue name → article title) exists and is only used to open a browser. The same slug
  against `/api/rest_v1/page/summary/` returns a thumbnail, a full-size image URL and an extract in
  one keyless call, which is what the info panel and the planner details pane currently have nothing
  to show. **Attribution is the catch and astrophoto.app gets it wrong**: the summary payload carries
  no licence field, Commons lead images are CC-BY-SA or stricter, and their page renders the image
  with no credit and no link. The response's `content_urls.desktop.page` is the minimum credit to
  render beside it; the Commons file page is the honest one.

- Observation only, no item: **we have no TLE code at all** (nothing matches `celestrak` or `TLE` in
  `src/`). Satellite-trail prediction for a planned exposure is a real astrophotography feature and
  the element source is free, but it is a whole propagator (SGP4), not a fetch.

Domain snapshot taken the same day, since the viewer's name is the reason this came up: `astrophoto.app`
is the one that is **gone**, along with `apv.app`, `photoviewer.app`, `deepsky.app`, `skyview.app`,
`lightframe.app`, `subframe.app` and `stacker.app`. Free: all six of `astrophotoviewer.{com,org,net,dev,app,io}`,
`astroview.app`, `astroviewer.app`, `fitsview.app`, `fitsviewer.app`, and `tianwen.{app,io}` — while
`tianwen.{com,org,net,dev}` are taken. `.app` and `.dev` are HSTS-preloaded, so anything there is
HTTPS-only by construction.

## Inbox: Slack self-notes, 2026-09-08 -> 2026-09-19 (swept 2026-09-20)

Two notes since the previous watermark (the 2026-09-08 sweep-summary message itself excluded). Both
had a confirmable root cause in the code by the time they were read back, so this pass filed them
directly rather than leaving them as bare repro requests.

### Filed by this sweep

| Note (date written) | Home | What it turned out to be |
|---|---|---|
| Sky atlas: label collision isn't 100%, a "show objects with photo" mode, hover recolours the target (09-19) | [ui.md, Sky Map](ui.md) | Two of the three are **DONE (2026-09-20)**: the picture mode shipped as a sub-setting of [O] (key `I`) and the hover highlight shipped as a wash resolved through the click's own resolver -- which also closed the 2026-06-08 "obj selection" note above, and did NOT reopen the 2026-09-10 click-vs-hover choice, that being about which gesture SELECTS. Still open: the label collision gap, which is partly a documented density trade-off (best-effort placement above ~60-100 labels) rather than a plain bug |
| Viewer: Save should land in the folder the file was opened from (09-17, first half) | [viewer-prerelease-fixes.md](../plans/viewer-prerelease-fixes.md) P35 | Confirmed gap: the Save dialog never sets `lpstrInitialDir`, so Windows falls back to Explorer's own last-used folder |

### Held here, mismatched to its own header

- [?] **"on decline should allow user to type address and fill in coords from there"** (09-17, second
  half, written under the same "fits viewer:" line as the Save note above). No geocoding code exists
  anywhere in `src/`, and manual coordinate entry already exists but in the Equipment tab's profile
  panel (`EquipmentTab.ProfilePanel.cs:337,342-343`, `SiteLatitude`/`SiteLongitude`), not the viewer.
  [in-app-sky-atlas.md](../plans/in-app-sky-atlas.md) separately rejected OS geolocation for a related
  problem and points users at that same manual entry instead. Reads like a site-setup idea (a
  location-permission "decline" prompt, falling back to an address lookup) that landed under the wrong
  header rather than something the FITS viewer's Save flow should do. Needs one line confirming what
  "decline" refers to before it gets a home.
