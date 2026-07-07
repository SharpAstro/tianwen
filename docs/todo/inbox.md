# TODO -- Inbox (unsorted Slack self-notes)

Part of the TianWen TODO set. See [TODO.md](../../TODO.md) for the index and the active/high-priority list.

## Inbox: consolidated from Slack self-notes (2026-06-02)

New, still-actionable TianWen items lifted from the Slack "messages to self" brain-dump (Mar-May 2026),
deduped against the rest of this file. Date in parens is when the note was written. Triage into the
sections above when picked up. Notes that turned out to be already DONE or already tracked elsewhere are
intentionally NOT repeated here.

### Sky Map
- [x] **Pan/zoom jank at sub-90deg FOV (worst with SCP in view)** — FIXED 2026-06-11: the overlay Phase A cache (`VkSkyMapTab.RenderObjectOverlay`) was keyed on the exact view matrix below `WideFovThresholdDeg`, so every drag frame re-ran the catalog grid scan (`GatherSkyMapOverlayCandidates`; pole-in-view = full-RA Dec strip, ~16k cell lookups -> 100-240 ms/frame; ~5k cells elsewhere -> 40-90 ms). Fix: key on the unprojected view centre quantized to FOV/8 cells + FOV quantized to ~10% log steps, and widen the gather margin to `max(1deg, 0.15 x FOV)` (RA scaled 1/cos dec) so the cached set covers every view inside a cell; Phase B's per-frame projection culls as before. Measured at the SCP all-layers-on: 8 zoom/time stimuli -> ONE 93 ms frame (the legitimate cell-boundary rebuild) vs 1-3 slow frames per stimulus before.
- [x] Optional follow-up: move overlay Phase A (candidate gather) to a background task (the `TryApplyPendingStarBuild` pattern) so even cell-boundary rebuilds never block a frame — DONE 2026-06-11 (PR #22, `5d501c1`): Phase A gather runs off the render thread.
- [x] Search box + click-to-goto (slew to clicked object) (2026-04-16) — DONE: search panel (`OpenSkyMapSearchSignal` + query-changed incremental results) and click-select (`SkyMapClickSelectSignal`) both open the info panel, whose Goto button slews the connected mount (`SkyMapSlewToObjectSignal`); object labels are click-targets too (PR #24).
- [ ] Compass markers + horizon markers (2026-04-16)
- [x] "N" key jumps the sky to local midnight (2026-04-18) — DONE (branch `feat/top-5-todo`): `SkyMapState.ComputeMidnightOffset` lands the sky on the current observing night's 00:00 (forward to tonight's upcoming midnight when local time >= noon, back to this morning's 00:00 otherwise); pure + unit-tested. Pairs with the time-adjuster item above.
- [x] "Show in planner" action from the sky map (2026-04-18) — DONE: "View in Planner" button in the info panel posts `ViewInPlannerSignal` (button width fixed in PR #24).
- [ ] Compute edge crossings (clip constellation / grid lines at the viewport edge) (2026-04-04)
- [ ] Load Gaia stars from Stellarium `.dat` files (the 3-vector unit-pos pipeline is already DONE; only the loader is missing) (2026-04-04, 2026-05-19)
- [ ] Bake a nebulosity layer into the baked Milky Way background image (2026-04-18)
- [ ] Share more rendering code between the Sky Map and the FITS viewer (2026-04-04)

### Planner / Session GUI
- [ ] Planner: sensor-proximity companion suggestions — when pinning a target, surface catalog neighbours that fit on the same sensor FOV (e.g. pin the Lagoon → suggest the Trifid, ~1.4° away). Needs the active profile's FOV (focal length + sensor size already resolvable from equipment) and an angular-separation scan of the catalog around the pinned target; render as a "nearby: …" hint in the details panel or a pin-adjacent suggestion row. Very nice to have. (2026-07-07)
- [ ] Second planner view: all unique pinned objects plotted over their bounding visibility timespan (2026-04-18) (confirmed not implemented)
- [ ] Indicate a "light" / coverage marker under targets that actually have scheduled exposure time (2026-03-25) (the Tonight tab already goes read-only with Start disabled during a running session; only the per-target coverage marker is missing)
- [ ] Site change should unpin pinned targets when coordinates change, and must NOT invalidate cooler setpoint temps (2026-03-27) (unpin: confirmed not done)
- [ ] Planner input bugs: Ctrl+V paste does nothing, input field too small, Enter does not commit the "Today" date edit (2026-04-07)
- [ ] Replace the Live Session tab icon with a Milky Way image (2026-03-24) (currently the camera-flash emoji)
- [ ] Make the Windows taskbar entry more dynamic (progress / session state) (2026-04-02)

### Equipment / device UX
- [ ] Gate "Connect All" on discovery completion (2026-04-30)
- [ ] Clicking a device class should ensure all devices of that class are visible; vendor text is hard to read (2026-04-23)
- [ ] Better feedback than logging "Expected Camera, got mount" on a type mismatch (2026-04-23)
- [ ] "Hold Shift reveals extra options" pattern (e.g. Shift on discover; Shift = loop instead of single-click preview) (2026-04-16)
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
- [ ] Mention FC.SDK in the skills docs (2026-04-19)
- [ ] Investigate AOT trim warnings: LibUsbDotNet (IL2104 / IL3053), CSharpFITS (IL3053) (2026-04-19)
- [ ] CI: ensure publish does not run while tests are still going; reduce server AOT publish warnings (2026-04-19)
- [ ] App self-update detection (2026-04-26)

### Code quality
- [ ] Move `RGBAColor32Extensions.cs` to a base layer (DIR.Lib) (2026-04-26)
- [ ] Use `Vector2` where we currently pass `PointF`-style pairs (2026-04-10)
- [ ] Document / clarify how `ResilientCall` interacts with collision detection (2026-04-26)
- [ ] Maybe support .NET Standard 2.0 for wider lib reuse (2026-05-02)
