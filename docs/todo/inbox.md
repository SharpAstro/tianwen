# TODO -- Inbox (unsorted Slack self-notes)

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Inbox: consolidated from Slack self-notes (2026-06-02, re-swept 2026-08-02)

New, still-actionable TianWen items lifted from the Slack "messages to self" brain-dump (Mar-May 2026),
deduped against the rest of this file. Date in parens is when the note was written. Triage into the
sections above when picked up. Notes that turned out to be already DONE or already tracked elsewhere are
intentionally NOT repeated here.

**Sweep watermark: the Slack self-DM is swept through 2026-08-29.** Three earlier passes are folded in: the
2026-06-02 consolidation below (Mar-May), and a 2026-07-07 pass that filed the ROEBA banding and Hough
star-halo notes **straight into [imaging.md](imaging.md)** with full detail rather than through this file,
which is why they are absent here, and a 2026-08-27/28 pass that filed the whole FITS-viewer band straight
into [viewer-prerelease-fixes](../plans/viewer-prerelease-fixes.md) as P11-P21, plus the chart and web-build
notes into [ui.md](ui.md). When re-sweeping, read the DM back only to this watermark, and check
`imaging.md`, `viewer-prerelease-fixes.md` and `ui.md` before concluding a note was never triaged.

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
- [?] **"sky atlas bug: obj selection"** (2026-06-08): too terse to match to a fix with confidence. Two
  candidates landed later: `b920c53a` (2026-07-11, selection reticles + alt/az stayed live across a
  date/time scrub) and the dark-nebula click resolver now honouring the `[D]` layer toggle
  (`SkyMapSearchActionsTests.DarkNebulaClickRespectsLayerToggleAndPinning`). If neither is the bug you
  saw, it is still open and needs a repro.

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

| Note (date written) | Home | State there |
|---|---|---|
| Star profile / colour / name on hover (08-19) | [ui.md](ui.md) FITS Viewer | open |
| `--help` shows no version; AI discovery status + download options (08-21) | P11 | half fixed -- version + status shipped, **download open** |
| Needs more in-depth doco (08-22) | P13 | next release |
| An empty instance should adopt an opened file (08-22) | P14 | **FIXED** |
| Gain/ISO and offset missing from the right pane (08-22) | P12 | **FIXED** |
| Carry calibration/stretch when stepping between frames of the same type (08-22) | P19 (blink mode) | next release |
| Right-click to copy colour / RA-Dec (08-24) | P17 | **FIXED** |
| Show detected object name; clickable object mode (08-24) | [ui.md](ui.md) FITS Viewer | open, and **explicitly undecided by you** |
| Share link to the web viewer, needs `&t=<capture time>` (08-24) | P20 | backlog (needs the web side) |
| Show debayered channels via "the new `AsChannel*`" (08-26) | P21 | backlog. Note there is no `AsChannel*` API anywhere; that note resolved to `Channel.AsSpan()` |
| Save as seen on screen, Save-As, iconise Open/Save (08-27) | P18 | next release |
| Atlas spark lines (08-21) | [atlas-planet-detail.md](../plans/atlas-planet-detail.md) A1 | planned; A1 is literally titled after this note |
| Log / time-compressed graphs (08-22) | [ui.md](ui.md) Charts | open |
| Web build: fake profiles for framing, Milky Way texture, copy-link-to-point (08-24) | [ui.md](ui.md) Charts | open (the copy-link half is the other end of P20) |

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

- [ ] Siril gradient-correction script as a reference (08-18) -> [imaging.md](imaging.md), deferred CLI verbs
- [ ] Audit that every exit path stops / parks / flips (08-28 item 1) -> [sequencing.md](sequencing.md)
- [ ] Atlas as a quasi-goto aid for a slew-less mount, incl. the flip-Dec geometry (08-28 item 2) -> [ui.md](ui.md)
- [ ] Resume an interrupted session, e.g. a mosaic stopped by dew (08-28 item 3) -> [sequencing.md](sequencing.md)
- [ ] Airmass-paired real degradation pairs (08-28 item 4, second half) -> [ai-denoise-deconv.md § 2.1c](../plans/ai-denoise-deconv.md)

