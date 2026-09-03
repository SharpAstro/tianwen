# Remote Profile: mirror a rig's tianwen-server in the GUI "as if local"

**Status: P1-P5 DONE (2026-07-27).** Trigger: several rigs have a mini PC on the
same (or reachable) LAN running the capture stack; the main computer should show everything that
is happening on a rig **as if the GUI ran on that machine directly**. The existing
`tianwen-server` (headless REST + WebSocket) is the node-side runtime -- this plan adds discovery
and a remote-consumption path; it is additive, not a replacement (ninaAPI clients keep working).

Surveys this plan builds on: `src/TianWen.Hosting` endpoint/event inventory, the chess app's
`Chess.Net` LAN discovery (`../../sebgod/chess`), and the GUI's session-state coupling
(`LiveSessionState.PollSession`, `AppSignalHandler`).

## Key insight: the profile lives ON the node

A profile's device URIs are node-local (they name drivers and ports on the machine the devices
are plugged into). A "remote profile" on the main computer is therefore **not a synced profile**
-- it is a **binding record**: `{ nodeId, remoteProfileId, alias, lastAddress }` stored locally
(`AppData/RemoteProfiles/<id>.json`), pointing at a profile that lives on the rig's
`tianwen-server`. The GUI binds to (node, profile-on-node); everything the node does -- including
a session the node started by itself -- is mirrored, and control flows back over the same API.

Two consumption modes fall out of one mechanism:
- **Mirror mode**: attach to whatever the node is doing (node may be running autonomously).
- **Drive mode**: fetch the node's profile, plan locally, push targets, start/stop the session.

**The session ALWAYS runs on the node it belongs to.** "Remote" never means "my computer runs the
session against the rig's hardware over the wire" -- that would forfeit the whole point of a rig mini
PC (it must keep imaging when the laptop sleeps) and pay network latency on every driver call all
night. The client owns exactly one session (its own, local) and may *observe* any number of remote
ones.

### View context is an overlay, not a rebind

Selecting a remote rig **changes what you are looking at**; it does not touch what this node owns. The
local session keeps running underneath with its devices connected -- it is merely hidden from view --
and its notifications/warnings still bubble up. Consequences:

- **The single-session invariant is per NODE**, not per client: the local node owns at most one
  session, each rig owns its own.
- **`ProfileSwitchGate` scopes to LOCAL profile rebinds only.** Switching to another *local* profile
  while local hardware is connected or a local run is active stays refused (it re-points this node's
  equipment). Selecting a *remote* rig is **never** gated, and neither is coming back to local. Already
  true by construction: `SwitchProfileSignal` (gated) only ever carries a local profile id, while
  `SelectRemoteRigSignal` is a separate, ungated signal. Note the gate reads *local* hub/session state
  regardless of which context is on screen -- rebinding the local profile is refused even while you are
  watching a rig.
- **Two UI requirements fall out of "warnings still bubble up"**: notification entries need a **source
  tag** (local vs which rig) so a warning from the session running underneath is distinguishable from
  the rig you are watching, and the chrome needs a **persistent local-session indicator** while a
  remote context is on screen -- otherwise you forget your own mount is tracking.
- **Implementation cost (shapes P3):** `LiveSessionState` used to be a singleton (one instance injected
  into every tab + `AppSignalHandler`). Under the overlay model it is **one per view context**, with the
  tabs rendering whichever is active -- done in Part 3 item 2, on top of the `ISessionTelemetry`
  extraction (item 1) that makes a local `Session` and a `RemoteSessionMirror` interchangeable per context.
  Per-profile planner pins (`AppData/Planner`) likewise need keying by binding id for a remote context
  -- **done in P4**: a rig's plan lives under `Planner/rigs/{bindingId}/{profileId}/`, because a profile
  id is not unique across machines (copy a rig's profile to a second rig and both contexts share an id,
  merging their pins). Local paths are deliberately unchanged, so no existing pin is orphaned.

The planner and sky map need no server surface at all: `GET /api/v1/profiles/{id}` already
returns the full equipment profile (device URIs as strings, focal lengths, site), the planner is
pure given profile + catalog DB + time, and pins go back as `PendingTarget`s at session start
(`POST /api/v1/session/targets` + `/session/start?profileId=`). All planner/sky-map UI works
unchanged.

## Part 1 -- `LAN.Lib`: peer-discovery sibling extracted from chess

**Status (2026-07-24): sibling published + wired.** `../LAN.Lib` is on GitHub (SharpAstro org) and
NuGet, currently at **1.1.51** (`LAN.Lib` `1.1.*` pinned in `src/Directory.Packages.props`; joined
the `UseLocalSiblings` auto-detect in `src/Directory.Build.props` + `TianWen.Server.csproj`'s
conditional ItemGroups, same pattern as SER.Lib/Lzip.Lib/QHYCCD.SDK). `TianWen.Server`'s
`Program.cs` calls `AddLanDiscovery` (service `tianwen-server`, `StableNodeIdPath` under
`%LOCALAPPDATA%\TianWen\lan-node-id.txt`) and logs the announced node id at startup (smoke-tested:
`TianWen Server starting on port 18881 (LAN node 019f93...)`). `IPeerTable.NodeId` (own stable
identity, LAN.Lib 1.1.31) was added for this, and `LanDiscoveryOptions.Listen` (announce-only,
[PR #2](https://github.com/SharpAstro/LAN.Lib/pull/2)) shipped in 1.1.51 for the one-way-discovery
invariant below. **Version note:** CI composes the package version as
`VERSION_PREFIX(1.1.$run_number) + VERSION_REV($run_attempt)`, so the patch segment is a
concatenation, not an increment -- run 3 attempt 1 is `1.1.31`, run 5 attempt 1 is `1.1.51`. The
csproj `VersionPrefix` (1.1.0) only sets the assembly version; the `.yml` is authoritative for the
package.

**GUI side DONE, and reshaped from the original "read-only list" plan** -- a design pivot mid-P1:
rather than a discovery-only list bolted onto the rarely-seen no-profile screen, LAN.Lib is wired
into a general-purpose **profile-switcher dropdown** on the Equipment tab's profile-panel header
(`"Profile: {name} ▾"`, `EquipmentTab.ProfilePanel.cs`'s `PanelSection.ProfileHeader` case +
`OpenProfileDropdown`), reusing DIR.Lib's existing `DropdownMenuState`/`RenderDropdownMenu` (the
same primitive behind the filter-name dropdown). This closed a real, independently-discovered gap:
the GUI had **no runtime way to switch between local profiles at all** (`Program.cs` only
auto-selects when exactly one profile exists; `EquipmentTabState.AllProfiles` existed but was never
populated). Now: `AllProfiles` is populated in the `DiscoverDevicesSignal` handler
(`AppSignalHandler.Equipment.cs`, free -- `dm.DiscoverAsync` already covers `DeviceType.Profile`);
the dropdown lists local profiles first (`SwitchProfileSignal(Guid ProfileId)`, mirrors the TUI's
existing bare-assignment switch precedent in `TuiEquipmentTab.SwitchToSelectedProfile`) then
discovered `tianwen-server` rigs suffixed "(remote)" (`SelectRemoteRigSignal(string DisplayName)`);
each dropdown entry's action is captured directly in its own closure (not resolved later by index),
so the list can't drift out of sync with a peer-table change between open and click. Per user
decision, a remote entry is clickable today but posts a stub notice ("Remote rig binding for 'X'
isn't implemented yet") since actual binding is P4. The no-profile screen also gained a plain
picker (`RenderNoProfile`) for the case of >=1 local profile existing with none auto-selected --
the same `SwitchProfileSignal`, no dropdown needed there (full-screen state, not an overlay
trigger). GUI announces symmetrically too (`service: "tianwen-gui"`, `ServicePort: 0` -- it serves
no inbound channel), driven manually via `Program.cs` since the GUI runs a bare `ServiceCollection`,
not a generic `Host` (`AddLanDiscovery`'s `LanDiscoveryHostedService` never starts on its own there).
Live-verified end-to-end via the SDL inspector: 3 test `tianwen-server` instances discovered and
listed (disambiguated "SEB-SURFACE #1/#2/#3 (remote)" per `LanPeer.ResolveLabels`), keyboard
Down-Down-Enter selected a remote entry and the stub notice landed in `appState.Notifications`.
14 Equipment-area unit tests green, full solution builds clean (0 warnings).

### Two invariants that fell out of P1 (both enforced, not documented-only)

**1. Profile switching is gated on idle equipment (`ProfileSwitchGate`, `TianWen.Lib/Devices/`).**
Every host assumes a **single profile context**: connected drivers, camera telemetry buffers,
filter-edit state, planner site/timezone and the live-session preview all key off the active
profile. Swapping it underneath connected hardware orphans those drivers -- they stay connected in
the `IDeviceHub` while no UI surface references their URIs any more, so the user can no longer
disconnect them, warm a cooler down, or close a cover: the equipment is stranded until the process
exits. So a switch is refused while a run owns the equipment (`SessionActive`) or any device is
connected (`DevicesConnected`); the connected-devices check is the load-bearing one and a running
session is strictly a subset of it, but the two are reported separately because "stop the session"
is a different instruction than "disconnect the mount". One pure evaluator serves every surface, so
the wording can't drift:
- **GUI** -- gated in the `SwitchProfileSignal` handler (not in the dropdown), so no poster can
  bypass it. Refusal raises a centred modal (`EquipmentTabState.ProfileSwitchBlocked` ->
  `RenderProfileSwitchBlocked`, geometry mirroring `LiveSessionTab.RenderSessionPrompt`) with [OK] /
  Esc, plus a Warning notification. The dropdown still *lists* every profile (browsing is
  harmless) -- picking a blocked one is what explains itself.
- **TUI** -- same gate in `TuiEquipmentTab.SwitchToSelectedProfile`; no modal, so the refusal lands
  on the status line + notification history.
- **Hosted API** -- `PUT /api/v1/session/profile` returns **409** with `Describe()` as the body
  (it previously re-pointed the active profile with no check at all).
- **CLI** -- needs no gate: `ProfileSelector` resolves the profile once at startup (`--active <name>`,
  interactive picker, or first-run wizard) before anything is connected. Already supported.

The message wraps via `Layout.Builder.WrapH` over one `Text` node per word -- the layout engine's
own measurement, so no hand-rolled break-at-width loop and no line breaks baked into the prose.

**2. Discovery is one-way: the GUI discovers rigs; a rig discovers nothing.** `TianWen.Server`
announces with **`Listen = false`** (a new LAN.Lib option, the mirror of the existing `Announce`),
so `LanDiscovery` never subscribes to the transport and its peer table stays empty *by
construction* rather than by a convention the server has to honour. A server that also kept a peer
table would invite exactly the design we don't want -- a rig binding another rig's profile, or two
rigs mirroring each other -- and makes "who owns the equipment" impossible to reason about. The
server is only ever a bind **target**. (Structurally reinforced: `TianWen.Server` doesn't reference
`TianWen.UI.Abstractions`, where the remote-profile consumption path lives.) `IPeerTable.NodeId`
still resolves on an announce-only node, so the startup node-id log is unaffected.

New sibling repo `../LAN.Lib` (package id `LAN.Lib`, namespace `LAN.Lib`), extracted from
`Chess.Net` and generalised. The chess code is already well-factored for this: TimeProvider-driven
beacon + self-expiring peer table, a plain-ASCII AOT-clean wire format, and a transport seam with
an in-memory fake for tests.

**Extracted (and generalised):**
- `ILanTransport` / `UdpLanTransport` -- UDP broadcast only (`ReuseAddress` + `EnableBroadcast`,
  the Alpaca-learned socket details). The TCP listener/`ILanConnection` half stays in chess
  (game-invite channel, nothing to do with discovery).
- `LanDiscovery` -- symmetric beacon (1 s) + peer table (5 s expiry), `TimeProvider`-driven.
- `LanProtocol` -- magic + version + space-separated URL-encoded tokens. The ANNOUNCE verb gains
  a **service token + key=value property bag**: every SharpAstro app shares ONE broadcast domain
  (default port 52821, chess's) and consumers filter by service name. Chess migrates to the lib
  separately (its protocol version is chess's own concern; the lib's magic is new).
- `LanIdentity` -- persisted display name + per-process peer id (echo filter only, never
  persisted: the chess two-instances-one-machine bug).

**Added for tianwen (not in chess):**
- **Stable `nodeId`**: the server mints a GUID once into its AppData and announces it in every
  beacon. Binding records key on it; the per-process peer id stays purely the echo filter.
  (Address is refreshed from discovery each sighting; `lastAddress` is the offline fallback.)
- **DI surface** (the deliverable that makes this a sibling, not a copy):

```csharp
services.AddLanDiscovery(o =>
{
    o.ServiceName = "tianwen-server";   // announced + filtered on
    o.ServicePort = 1888;               // the HTTP API port, announced
    o.NodeName = ...;                   // display name (default: machine name)
    o.StableNodeIdPath = ...;           // server side; mint-once GUID
    o.Announce = true;                  // false = listen-only (a pure monitor client)
});
// registers: ILanTransport, LanDiscovery (BackgroundService beacon+prune),
// IPeerTable (live peers, service-filtered, Changed event for the UI poll)
```

- Fake transport + FakeTimeProvider tests ported from chess's suite; AOT-clean (no reflection,
  ASCII codec, source-gen nothing).

**Coexistence:** Alpaca discovery (UDP 32227) is a one-shot broadcast *query*, different port;
OnStep mDNS is separate; `ReuseAddress` means several LAN.Lib consumers on one host share 52821.

## Part 2 -- `tianwen-server` surface additions (native v1)

**Status: items 1-6 + 8 DONE (2026-07-26).** Item 7 (node announce) landed with P1; item 9 is P5 and
is expected to be served by the Alpaca device plane rather than a bespoke hub API (see the candidate
section below). Notes on what the implementation decided, where it differs from the sketch, and what
it found:

- **2.1 preview** -- `GET /api/v1/preview/{otaIndex}?quality=&scale=`, `X-Frame-Number` header, plus a
  shared `PreviewEncoder`. **The sketch's "modelled on the nina `prepared-image`" turned out to be the
  wrong model**: that encoder multiplied every sample by `1/MaxValue` and called it an auto-stretch, so
  a linear sub (background at a couple of percent of full well) encoded to a near-black frame with a
  few lit pixels. The encoder now goes through the shared `StretchSolver` + `Image.RenderStretchedRgba`
  -- the same pipeline the GPU viewer and the CPU/TUI renderer already agree on -- and the **nina
  endpoint was switched onto it too**, so both surfaces render like the local viewer instead of being a
  third rendering of the same frame. Downsampling box-averages rather than point-samples (nearest
  neighbour at 1:8 simply misses single-pixel stars). The session-owned `Image` is only ever read:
  `DebayerAsync(normalizeToUnit: false)` returns a new image for a CFA frame and `this` untouched
  otherwise, so the pinned camera buffer is never consumed. Pinned by `PreviewEncoderTests`
  (encode -> **decode back** -> assert real pixels), which also caught a genuine bug on the way in:
  `scale=0` clamped to zero and produced a 1x1 preview, so a non-positive/NaN scale now means "full
  resolution".
- **2.2 prompt bridging** -- `PROMPT-REQUESTED` broadcast + `POST /session/prompt/respond {proceed}`,
  and the prompt **also rides on `/session/state`**. That addition is load-bearing, not belt-and-braces:
  polling is the authoritative channel, so a prompt that were only ever pushed would be unanswerable by
  a client that attached after it fired or whose socket dropped.
  **This forced a root-cause fix to the prompt policy itself, not just containment of the new
  subscriber.** A session answers a prompt itself only while *nothing* is subscribed, so
  `EventBroadcaster` subscribing would have wedged an unattended night at "switch on the flat panel" --
  and because that await sits inside `RunAsync`'s try, whose finally parks the mount / warms the
  cameras / closes the covers, the rig would have sat exposed at dawn (a hang there is not an exception;
  it simply never returns). The first attempt bounded it with a 2 min auto-*proceed*, which was the
  wrong fix: proceeding asserts a **physical** act nobody performed. Flats survive that lie only because
  `FlatExposureSolver` fails the metering and skips the OTA; the planned dark-frame prompt has no such
  backstop, so light-leaked darks would be written as valid calibration and subtracted from every light.
  Final design:
  - `SessionConfiguration.UnattendedPromptResponse`, defaulting to **`Decline`** -- skip the gated step.
    Missing calibration is recoverable, silently wrong calibration is not.
  - **Operator-invoked** flat runs (`tianwen flats`, `POST /session/flats`) opt into `Proceed`, which is
    what keeps the "switch the panel on, walk back inside" workflow working. A scheduled end-of-session
    block never does.
  - The policy travels **on the prompt** (`SessionPromptEventArgs.DefaultIfUnanswerable`) rather than
    being read back off the session, so a handler cannot disagree with the session about the same
    question -- which is how one safe default becomes two.
  - `EventBroadcaster` with an observer attached now **holds indefinitely, with no timer**: an attached
    client that ignores `PROMPT-REQUESTED` is a client bug, and answering after an arbitrary interval
    fabricates a decision rather than fixing it. The only bound is **liveness** -- if the last observer
    disconnects while a prompt is outstanding, the poll loop resolves it.
  - Prompts carry **`RequiresPhysicalPresence`** (true for the manual panel), which crosses the wire on
    `PendingPromptDto`. A remote observer is not at the observatory, so offering a bare "Continue" invites
    them to assert a fact they cannot verify -- the same fabrication as auto-proceeding, performed by a
    human. Answering stays permitted (they may be on the phone with someone at the scope); the flag is
    what lets a UI stop presenting it as a neutral one-click default, and the node records the
    notification at **Error** severity saying it needs someone at the rig.
- **2.3 broadcasts** -- `GUIDER-STATE-CHANGED` (the event already existed on the session, it simply was
  not subscribed) and a per-sample `GUIDE-STEP` from a timestamp-watermark diff in the broadcaster poll.
- **2.4 structured devices** -- `GET /devices/structured` -> `DeviceDto[]` with URI + type + live
  `Connected` from `IDeviceHub.IsConnected`.
- **2.5 notification ring** -- `EventBroadcaster` doubles as the node's notification recorder (it is
  already watching every session event), writing into a 200-entry lock-free `CircularBuffer` on
  `IHostedSession`, served by `GET /session/notifications` and pushed as `NOTIFICATION`.
  `CircularBuffer<T>` was made public for this -- deliberately narrower than granting `TianWen.Hosting`
  `InternalsVisibleTo` over all of TianWen.Lib to reach one ring.
- **2.6 telemetry depth** -- `CoolingSamples` / `FocusHistory` (with full V-curve) / `ActiveFocusSamples`
  / `ExposureLog` on the state DTO, and cooler temp/setpoint/power per OTA taken from the newest cooling
  sample rather than a second driver poll (one source, so the number cannot disagree with the ramp chart
  drawn beside it). **Site went on `ProfileDetailDto`, not `MountStateDto`, and alt/az + twilight are not
  on the wire at all**: site is a property of the profile rather than of a run, and everything a client
  wants from it (horizon coordinates for the current pointing, tonight's astro-dark window) is a pure
  function of (site, time, RA/Dec) that the client computes exactly as the local GUI already does.
  Shipping derived values would have created a second source needing its own consistency rules.
- **Mirror payoff** -- `RemoteSessionMirror`'s empty stubs for those four collections are now filled from
  the snapshot, and `ExposureLog` **stopped being event-sourced**: FRAME-WRITTEN only ever covered frames
  written after the mirror attached, so a client joining mid-night showed an empty frame list beside a
  non-zero frame count. The snapshot carries the whole run, polling is authoritative, and the worst case
  is lagging one frame by one poll -- so the local ring was deleted rather than reconciled.
- **Two standing-rule violations fixed in passing**: `EventBroadcaster` used `Task.Delay` in its poll
  loop (must be `ITimeProvider.SleepAsync`, or a fake clock hangs), and `HostedSession` locked on a plain
  `object` (must be `System.Threading.Lock`).

The existing surface is ~80% of what a mirror needs: `/session/state` already carries phase,
activity, failure reason, mount pointing/tracking/pier, full guider RMS + sample ring, per-OTA
camera/focuser/HFD state, schedule + timeline; control endpoints exist for mount/camera/focuser/
filter wheel/guider; profiles and devices are listable; WS pushes 6 event types. Additions, in
dependency order (each: new `Api/*Endpoints.cs` + one `Map*Api()` line + DTOs registered in
`HostingJsonContext`; WS payloads stay primitives/arrays -- the `Dictionary<string,object?>` AOT
constraint):

1. **Preview frames** -- `GET /api/v1/preview/{otaIndex}?quality=&scale=` modelled on the nina
   `prepared-image` (StbImageWriteSharp JPEG of `ISession.LastCapturedImages`) but per-OTA and
   with a frame counter header (`X-Frame-Number` from the camera state) so the client fetches
   only on change. Binary WS push is a later refinement; 1-2 fps poll is fine on LAN.
2. **Prompt bridging** -- broadcast `PROMPT-REQUESTED` (message, otaName) from
   `ISession.PromptRequested` in `EventBroadcaster`, plus `POST /session/prompt/respond
   {proceed}` calling `SessionPromptEventArgs.Respond`. Without this a remote GUI can never
   answer the manual-flat-panel prompt (headless auto-proceed hides it).
3. **Missing event broadcasts** -- `GUIDER-STATE-CHANGED` (LostLock etc.), plus a per-guide-step
   `GUIDE-STEP` push (new `ISession` event or poll-diff in the broadcaster) so the client stops
   re-pulling the whole 5-minute `RecentSteps` ring every second; slim the `/state` guider to
   stats-only once steps stream.
4. **Structured devices** -- `GET /devices/structured` -> `DeviceDto[] {uri, displayName,
   deviceType, connected}` (today's `/devices` returns display strings, no URIs/state).
5. **Notification ring** -- in-memory ring on `IHostedSession` + `GET /notifications` +
   `NOTIFICATION` WS event, so the GUI's notification feed has a hosted counterpart (today only
   `CurrentActivity` + `FailureReason` exist remotely).
6. **Telemetry depth** (as needed by the mirror's fidelity tier): cooler temp/power in
   `OtaCameraStateDto` (nina v2 already polls it), `CoolingSamples`, twilight times
   (the GUI's astroDark strip), alt/az + site on `MountStateDto`, `FocusHistory` /
   `ActiveFocusSamples` (V-curve), `ExposureLog` backfill (FRAME-WRITTEN covers only new frames).
7. **Node announce** -- `AddLanDiscovery` in `TianWen.Server` (service `tianwen-server`, port
   from config, stable nodeId). Log the announced nodeId at startup.
8. **Schedule-fidelity target DTO** -- the drive mode must NOT go through `PendingTarget`
   (name/RA/Dec/duration/gain only; `/session/start` stamps `Start = now`). The GUI's
   `ScheduledObservation` carries filter plans, altitude-optimised `Start`, `AcrossMeridian`,
   framing groups. Add `POST /session/schedule` taking a `ScheduledObservationDto[]` that
   deserializes back to the domain type (or accept the GUI-computed schedule verbatim at
   `/session/start`), so a remotely-driven night keeps the scheduler's slot times.
9. **Hub-level device API (out-of-session)** -- every existing device endpoint is session-scoped
   (404 "No active session"), but the GUI's preview mode, equipment connect/cool/jog, and
   sky-map slew all happen *out of session*. New session-independent group
   (`/api/v1/hub/...`): connect/disconnect/warm, per-device state, cooler setpoint/off +
   telemetry, focuser move, filter change, mount slew/park/tracking, **preview capture**
   (one exposure from an OTA camera, returned as encoded image). This is the largest single
   server addition; the Alpaca backend proves the remote-driver pattern in-repo, but the
   transport here is the native v1 JSON + imagebytes-style framing, not the Alpaca schema.

Explicitly **not** in Part 2 (deferred): hosted polar-alignment / planetary / preview modes (all
GUI-driven today), profile *editing* endpoints, auth/TLS (LAN-trust stands; see Security).

### Candidate: serve item 2.9 as an ASCOM **Alpaca server** instead of a bespoke hub API

Idea (user, 2026-07-26): rather than inventing `/api/v1/hub/...` plus `RemoteDeviceHub` and a set of
driver proxies, have `tianwen-server` **expose its devices over Alpaca**. The client side then needs
*no new code at all*: `AddAlpaca()` is already a fully functional, simulator-tested device source for
exactly the six types a rig exposes (camera, telescope, focuser, filter wheel, switch,
cover-calibrator), and `IDeviceHub` gets real drivers. **This is P5 by reuse**, and `ImageBytes` comes
free because it is part of the camera spec we already decode byte-exactly (see the preview-frame
note in 2.1 -- same pixel wire format for both paths).

**It is a device plane, not a session plane.** Alpaca is a *device* protocol; a session is an
orchestration layer above devices, so the two do not overlap and there is nothing awkward to
reconcile. Alpaca has no vocabulary for session lifecycle, schedule, phase, prompts, notifications,
autofocus/V-curve, flats, or meridian-flip state -- and ASCOM has **no Guider device type at all**
(Camera, CoverCalibrator, Dome, FilterWheel, Focuser, ObservingConditions, Rotator, SafetyMonitor,
Switch, Telescope). So native v1 remains the session plane by necessity. Division of labour:
- **in-session** remote preview + telemetry -> native v1 (2.1, 2.6)
- **out-of-session** remote equipment control + preview capture -> the Alpaca device plane (2.9)

**The one hard problem is ownership**, and it is the same invariant family as `ProfileSwitchGate`: a
rig's `Session` holds borrowed drivers, so an Alpaca client issuing `startexposure` / `slewtocoordinates`
on the same camera creates two masters. Alpaca cannot express "a session owns this" natively, but it
*can* return an error number, so the facade must be session-aware even though the protocol is not.

**Resolved (2026-07-27), and the first proposal was wrong.** That proposal was *"session running ->
device endpoints are read-only"*, which would have bricked the plane exactly when it matters: every
standard Alpaca client PUTs `Connected = true` before reading anything, so a blanket read-only mode
makes a running rig **unreadable**. It was also the wrong layer -- the question "may I touch this
device?" is not an Alpaca question at all. The same gap was already live **locally**: five UI call
sites each guarded on `LiveSessionState.IsRunning`, which is false during a flat run (the very reason
`HasActiveRun` exists), and `GetDisconnectSafetyAsync` returns `Safe` for anything that is not a
camera -- so the mount could be disconnected out from under a session with no warning, after which
`ResilientCall` silently reconnected it.

Ownership therefore lives on `IDeviceHub` as a **lease** (see CLAUDE.md, "Device Ownership"), which is
the one thing the GUI, TUI, hosted API and this plane all share. The Alpaca facade needs no bespoke
policy: it asks `DeviceOwnershipGate` and returns `0x40B` with the gate's own wording. The rules that
actually shipped: **reads always allowed** (watching a rig must cost it nothing); **`Connected = true`
always allowed** (it is the client preamble); **`Connected = false` refused while a run owns the
device** -- the invariant this section named, now enforced by the hub itself and not merely by the
facade remembering to check; **actuation refused** while leased. Escalation is explicit: stop the run.

**Scoping.** Two very different bars: (a) *our own* `AlpacaClient` + the six drivers work -- a known,
enumerable endpoint subset, and it yields a free round-trip test (our client against our server, with
`AlpacaSimulatorTests` as the conformance oracle in reverse); (b) full ASCOM conformance so
N.I.N.A./SharpCap can drive the rig -- a much bigger bar, worth treating as a separate later feature.
Aim at (a). AOT caveat: the Alpaca envelope is generic over the value type, so each `T` needs
registering in a `JsonSerializerContext` (same discipline as the no-`ResponseEnvelope<object>` rule);
ImageBytes sidesteps JSON entirely. Discovery coexists cleanly -- Alpaca's one-shot UDP 32227 query
finds *devices*, LAN.Lib's 52821 beacon finds *nodes with sessions* (what a binding record needs).

**Rejected along the way:** an "Alpaca-direct" mode where the client runs the session against remote
hardware. It reads as a cheap third consumption mode but violates the session-runs-on-the-node rule
above.

## Part 3 -- GUI remote session source

The seams already exist by accident: `LiveSessionState.PollSession` reads ~25 telemetry
properties off `ISession` (~95% telemetry surface: 37 getters, 6 events, 2 run methods), and
every equipment/preview action resolves drivers through `IDeviceHub`
(`hub.TryGetConnectedDriver<T>(uri)`). The GUI has zero HTTP consumption today -- both seams are
plain interfaces, so remote implementations slot in without tab changes.

1. **Extract `ISessionTelemetry`** -- **DONE (2026-07-26)**, `TianWen.Lib/Sequencing/ISessionTelemetry.cs`.
   30 get-only properties + the 6 events; `ISession : ISessionTelemetry, IAsyncDisposable` keeps only
   what cannot cross a wire (`RunAsync`, `RunFlatsOnlyAsync`, `Setup`).
   `LiveSessionState.ActiveSession` is now `ISessionTelemetry?`, so a local `Session` and a future
   `RemoteSessionMirror` are interchangeable everywhere the UI reads.
   <br>**The one non-mechanical part was `Setup`.** The UI turned out to reach into the live driver
   graph for exactly three *display* facts -- OTA count, per-OTA camera name, and the mount name --
   plus two per-OTA capability checks (`ota.Focuser is not null`, `ota.FilterWheel is not null`)
   gating the focus/filter rows. Rather than drag `Setup` into telemetry (drivers cannot cross a
   wire), those became `ImmutableArray<TelescopeDisplayInfo> TelescopeDisplays`
   (`CameraName` + `HasFocuser` + `HasFilterWheel`, built once per session -- the device set is fixed
   for its lifetime) and `string MountDisplayName`. **Rule going forward:** a consumer that reaches
   for `Setup` to render something should add the display-level fact to `TelescopeDisplayInfo`
   instead of widening telemetry back to the driver graph.
   <br>Also removed a dead placeholder in `LiveSessionActions`' Cooling status that interpolated a
   `Setup` driver value into a string then discarded it via `is var _`.
   <br>Verified: 3404 unit + 311 functional tests green, 0 warnings, and a live fake-device session
   (`TIANWEN_NOW`-anchored, inspector-driven) rendering the OTA column header, focuser row, absent
   filter row, mount section and cooling status identically through the new path.
1. **Per-view-context `LiveSessionState`** -- **DONE (2026-07-26)**,
   `TianWen.UI.Abstractions/ViewContexts.cs`. `ViewContext` (Local | Remote + `NodeId` + `DisplayName`)
   owns a `LiveSessionState`; `ViewContexts` holds the set (`Local`, `Active`, `All`) plus `Activate`,
   `GetOrAddRemote`, `PollAll`, `AnyNeedsRedraw`/`ClearNeedsRedraw`. One context exists today, so the
   GUI and TUI behave identically -- the deliverable is that every consumer is now **classified**, which
   is the part that is expensive to retrofit once a second context exists.
   <br>**The three-way split, and why each site chose what it chose:**
   - **`Active`** -- anything that renders or reads "what is on screen": the Live Session / Guider tabs,
     the sidebar's live-session icon, the window title, the sky-map mount reticle.
   - **`Local`** -- anything that owns or acts on THIS node's hardware, so watching a rig can never
     redirect or unlock it: the whole of `AppSignalHandler` (preview telemetry poll, `EnsureSessionIdle`,
     the focuser-jog prologue, the session/flats/polar starts, and `ProfileSwitchGate`), the Equipment
     tab and its sidebar lock, `SessionTabState.IsSessionRunning` (which freezes the local schedule +
     planner date nav), the sky-map schedule highlight (its targets come from the local plan), the
     redraw *cadence*, and every quit path -- closing the client must abort the local session and must
     NOT stop a rig.
   - **`All`** -- telemetry polling and the redraw flag, so an off-screen local session keeps its phase,
     frame counts and mount pointing current and still earns frames for its notifications.
   <br>Capturing `Local` in a subscribe lambda stays valid because the local context is created once and
   never replaced (`AppSignalHandler`'s per-area aliases rely on this); `Active` must be resolved per
   use, which is why the TUI tabs became a `LiveState` property over `contexts.Active` rather than a
   constructor-captured field. New guard `AppSignalHandler.EnsureLocalContext` refuses the three
   run-starting signals while a remote context is on screen (per-action guards for the device handlers
   come with the remote Equipment/Preview surfaces in P5). `IGuiChrome.LiveSessionState` became
   `IGuiChrome.ViewContexts`. The DEBUG inspector snapshot gained `viewContext`, `viewContextCount` and
   `localSessionRunning` so an agent can tell the overlay apart from the thing underneath it.
   <br>One deliberate behaviour change: the TUI now polls every context each loop instead of only when
   the Live/Guider tab is active, matching what the GUI already did unconditionally
   (`VkGuiRenderer.RenderContent`) and costing nothing when idle (`PollSession` early-returns).
   <br>Verified: 3412 unit (8 new `ViewContextsTests`, incl. an overlay test that activates a second
   context and asserts the local session's run state survives) + 311 functional green, 0 warnings, plus
   the same live fake-device session as P3.1 -- identical rendering, Equipment tab + planner date nav
   correctly locked during the run and released after, clean `Finalising -> Aborted`, exit 0.
2. **`TianWen.Hosting.Contracts` + `TianWen.RemoteClient`** -- **DONE (2026-07-26)**.
   <br>**Contracts split:** the native-v1 DTOs, `ResponseEnvelope<T>`, the three profile request types
   (previously declared inside endpoint files) and `HostingJsonContext` (now `public`) moved to
   `TianWen.Hosting.Contracts`, referenced by both Hosting and the client, so server and client
   serialize the same contract through the same generated metadata. **Namespaces were deliberately
   kept** (`TianWen.Hosting.Dto` / `.Api` now span two assemblies) so no endpoint using-directive had to
   change. The ninaAPI shim's `NinaApiJsonContext` + its DTOs stay in Hosting -- no client of ours
   speaks PascalCase single-OTA.
   <br>**`TianWenNodeClient`:** transport only, no state. Wraps an `HttpClient` (caller sets
   `BaseAddress`, so it composes with `IHttpClientFactory` and tests against a scripted
   `HttpMessageHandler`), always passes an explicit `JsonTypeInfo`, and returns `NodeResult<T>` --
   payload or the server's own error text. Failure is data, not an exception, because "no session"
   (404) and "already running" (409) are normal states of a rig. **404 is distinguished from a
   transport failure** (`IsNotFound`), which is what lets a UI say "idle" instead of "offline". A
   gate 409 surfaces `ProfileSwitchGate.Describe()` verbatim rather than a reinvented message.
   <br>**`TianWenEventStream`:** `ClientWebSocket` to `/api/v1/events` with a capped-backoff reconnect
   loop driven by `ITimeProvider.SleepAsync`. The stream is a **latency optimisation, never the source
   of truth** -- which is exactly why it needs no replay, sequence numbers or resync handshake.
   <br>**Two things the split immediately paid for.** (1) A latent contract bug: several DTO properties
   were `required` *and* nullable, while the context serializes `WhenWritingNull` -- so the server's own
   output was undeserializable (a healthy session with `FailureReason = null` threw "missing required
   properties" on read). Invisible for as long as nothing ever read a response. Fixed by dropping
   `required` from the 18 nullable wire properties, with the rule written into `SessionStateDto`'s doc.
   (2) The mandatory AOT-publish smoke test found a **pre-existing** 500: NaN is not valid JSON, and
   because serialization runs while the response streams, one unguarded value takes down the whole
   endpoint as a bodiless 500. It surfaced on `/v2/api/equipment/camera/info`, but the audit showed it
   was **not** nina-only -- native v1's `OtaCameraStateDto.FocuserTemperature` is NaN by default with no
   focuser fitted, so `/api/v1/session/state` (the endpoint this whole mirror depends on) would have
   500'd on an ordinary single-OTA session. **Fixed here**, since API coverage is otherwise thin, and
   fixed as a **policy** rather than a hardcoded coercion: `JsonNumber.WireAllowsNonFinite` is *derived*
   from `HostingJsonContext`'s `NumberHandling`, and `ForWire` substitutes only while the contract is
   strict -- so flipping the contract makes all ~30 call sites preserve NaN with no edit (verified by
   doing it). Pinned by `HostingWireNumberTests`: the policy value, that both contexts agree on it, and
   all-NaN sources through the real projections and real contexts; verified to fail when a guard is
   removed, and re-checked against the published binary.
3. **`RemoteSessionMirror : ISessionTelemetry`** -- **DONE (2026-07-26)**, tier 1 + part of tier 2.
   Polls `/session/state` (2 Hz active / 0.5 Hz idle) and swaps in the whole immutable DTO by a single
   reference write, so a render-thread reader always sees one internally consistent snapshot with no
   lock and no torn mix of two polls. **Polling is authoritative; events only notify** -- so a missed
   event costs a moment of staleness, never a wrong screen.
   <br>Faithful today: phase, activity, failure reason, counters, mount pointing + display name, per-OTA
   camera/focus/filter state **including the display facts** (`CameraName`/`HasFocuser`/`HasFilterWheel`
   added to `OtaCameraStateDto` from P3.1's `TelescopeDisplays`, plus `MountDisplayName` on
   `SessionStateDto` -- without them a client cannot label an OTA column or decide whether to draw the
   focus/filter rows), guide stats + sample ring, schedule, phase timeline.
   `PhaseChanged` and `GuiderStateChanged` are **derived from consecutive polls** (the node has no
   guider broadcast yet, and a phase change must survive a dropped frame); `FrameWritten` +
   `PlateSolveCompleted` come from the WS stream, which also **event-sources** `ExposureLog` and
   `PlateSolveHistory` (the state DTO carries neither history). Fields with no wire representation
   return empty rather than guessing, each documented with which Part 2 item unblocks it;
   `ScoutCompleted` / `PromptRequested` use explicit accessors so subscriptions are retained rather
   than silently dropped.
   <br>**Completed 2026-07-27.** Preview JPEGs are fetched per OTA and decoded into
   `LastCapturedImages`, opt-in via `RemoteSessionMirror.Previews` (off by default -- a multi-rig
   dashboard wants phase and counters, not N JPEG streams) and skipped entirely when the node reports
   the `X-Frame-Number` the mirror already holds, so an unchanged frame costs headers only. Prompts are
   raised from the **snapshot**, not the broadcast -- a client that attached after `PROMPT-REQUESTED`
   fired, or whose socket dropped while a prompt stood, would otherwise never learn there was a
   question and the node would hold the run open forever; `Respond` POSTs back on its own token so a
   "Cancel" still lands while the view is being torn down, and with no local handler the mirror stays
   silent rather than inventing a second answer to a question about hardware it cannot see. Lifecycle
   is driven by `StartAsync` (pushes the `ScheduledObservationDto[]` schedule, then starts -- and
   **does not start** if the push failed, since running the node's stale schedule looks like success
   and images the wrong thing all night) / `StartFlatsAsync` / `AbortAsync`, declared on the mirror
   rather than on `ISessionTelemetry`, which a local `Session` also implements and must stay read-only.
   `Image.TryDecodeRaster` became public for the decode (same "bytes off a wire, no temp file" need as
   the Canon EVF path). 19 new `RemoteSessionMirrorDriveTests`, with preview frames produced by the
   **real** server-side `PreviewEncoder` and decoded by the real client path; the change-token test
   counts body reads rather than requests, since the GET is issued either way.
   <br>Verified: 18 `RemoteSessionMirrorTests` against a scripted `HttpMessageHandler`, including a
   **round-trip through the real server-side projection** (`SessionStateDto.FromSession` over a
   substituted telemetry, serialized and read back) -- the test that would have caught the `required`
   bug. Full suites 3448 unit + 311 functional green; AOT publish clean apart from the 2 known
   LibUsbDotNet rollups, with the published binary smoke-tested (GET, complex-body POST, the
   formerly-`object` endpoint, and the nina shim).
4. **`RemoteDeviceHub : IDeviceHub`** -- **SUPERSEDED, never built (2026-07-27).** The idea was driver
   proxies over a bespoke `/api/v1/hub/...`, on the observation that "the Alpaca backend already IS an
   in-repo remote-driver protocol". P5 took that observation to its conclusion and made the NODE speak
   Alpaca instead, so the client reaches a rig's devices through the existing `AddAlpaca()` -- no proxy
   classes, no second remote-driver protocol to keep in step with the first, and ImageBytes for free.
   The line of code that would have been `RemoteDeviceHub` is a base address.
5. **Fidelity tiers** (honest about what doesn't round-trip): tier 1 = everything in
   `SessionStateDto` (full Live Session tab, ~70% of `PollSession` unchanged); tier 2 =
   preview images, prompts, notifications, guider per-step stream, out-of-session preview
   capture + equipment control (Part 2 items + the P5 Alpaca device plane); tier 3 (local-only for
   now) = guide-cam image (`LastGuideFrame`), `CalibrationOverlay`, guide-star visuals,
   polar-alignment and planetary modes (in-proc orchestrators streaming pixels; server-side
   PA is its own big item). Tier-3 fields stay empty in remote mode and the tabs already
   handle empty.
6. **Binding UX** -- the profile picker gains a "Remote rigs" section fed by `IPeerTable`
   (service `tianwen-server`): discovered node -> `GET /profiles` -> pick one (or "monitor
   whatever runs") -> binding record written. A bound-but-offline node shows as offline with
   `lastAddress`. Equipment tab in remote mode lists the node's devices (structured endpoint)
   with connect state from the hub API.

## Phasing

| Phase | Deliverable | Depends on |
|-------|-------------|------------|
| P1 | DONE -- `LAN.Lib` sibling published to NuGet; server + GUI both announce/discover; GUI profile-switcher dropdown (local profiles + discovered rigs, reshaped from the original read-only-list plan) | new repo + `UseLocalSiblings` extension |
| P2 | **DONE (2026-07-26)** -- preview endpoint (+ a shared stretch-correct encoder the nina shim now uses too), prompt bridging (with the headless auto-proceed guarantee preserved), `GUIDER-STATE-CHANGED` + `GUIDE-STEP`, structured devices, notification ring, telemetry depth, schedule-fidelity target DTO (2.1-2.6, 2.8). Item 2.7 landed with P1; 2.9 is P5 | -- |
| P3 | **DONE (2026-07-27)** -- `ISessionTelemetry` extraction, per-view-context `LiveSessionState`, `TianWen.Hosting.Contracts` split, `TianWen.RemoteClient` (`TianWenNodeClient` + `TianWenEventStream`) and `RemoteSessionMirror`, with the deep-telemetry arrays filled from the snapshot and the client-side remainder landed: preview JPEGs into `LastCapturedImages` (opt-in, `X-Frame-Number`-gated), the prompt round-trip (raised from the snapshot so a late-attaching client can still unblock the rig), and start / flats / abort / `POST /session/schedule` driven through the mirror | P2 (done) |
| P4 | **DONE (2026-07-27)** -- `RemoteRigBinding` (keyed on the LAN.Lib stable node id, never the address or name) persisted one-file-per-rig under `AppData/RemoteProfiles`; `RemoteRigConnection` resolves the address per connect (peer table, then the stored hint) and hands its `RemoteSessionMirror` to a `ViewContext` so the tabs render a rig unchanged; the profile picker binds on first select, lists bound-but-undiscovered rigs as offline, and offers the way back to local; the `RequiresPhysicalPresence` warning is finally rendered | P1, P3 |
| P5 | **DONE (2026-07-27)** -- served as an **ASCOM Alpaca device plane** (the candidate below, not the bespoke hub API): `MapAlpacaApi` exposes telescope / camera / focuser / filterwheel / covercalibrator over `/api/v1/{type}/{n}/{member}` plus the management API, with the camera's `imagearray` as binary ImageBytes. Device numbers come from the ACTIVE PROFILE in profile order (discovery order varies between scans; a number that moved would point a client at different hardware). Ownership is the P0 lease: actuation and a remote `Connected=false` answer `0x40B` with the gate's own wording, while reads and `Connected=true` always pass. Client side needed no new code -- pinned by 14 round-trip tests driving our own `AlpacaClient` against our own server | P3 (the Alpaca route needed only P1) |
| Deferred | Hosted polar-alignment/planetary modes, profile editing over the API, guide-cam image stream, auth/TLS, WAN relay, full ASCOM conformance -- each written up in [Deferred](#deferred-out-of-scope-for-this-plan) below | -- |

P1 and P2 are independent and can run in parallel. P3 is the headline ("as if local" session
mirror). P4 is the "remote profile" UX proper -- notably late in the sequence because the mirror
(P3) is what makes it useful, and because the binding UX is thin once the mirror exists. P5 was
sized here as the largest single chunk (out-of-session device control) -- **that estimate was
wrong by an order of magnitude**, because serving Alpaca instead of a bespoke hub API deleted the
entire client half. It is still what turns a session *monitor* into full remote *operation*.

## Deferred (out of scope for this plan)

Everything here is a scope boundary the plan drew on purpose, not leftover work from P1-P5. Each
entry says what it is, why it was held back, and what it would actually take -- so picking one up
starts from a shape rather than a name. Three are flagged **likely next** (user, 2026-07-27):
hosted polar alignment and the guide-cam stream. (The multi-rig dashboard was the third; it shipped
2026-07-28 and its section is kept below as the design record.)

**The P0 device lease changes the economics of the first two.** "Exactly one run owns the rig" now
exists as a concept in `TianWen.Lib` rather than as a GUI flag, so a hosted run of *any* kind can
claim the hardware and be refused coherently by every other plane. Before the lease, a hosted
polar-align would have had to invent its own mutual exclusion against sessions and flat runs.

### Hosted polar alignment (likely next)

`PolarAlignmentSession` runs *outside* `Session.RunAsync` against a manually-connected mount and is
driven entirely by the GUI today (`LiveSessionMode.PolarAlign`). Hosting it needs: a lifecycle
surface on `IHostedSession` (start / abort / state) alongside the session and the flat run; a DTO
for the phase plus the two-frame solve result (az/alt error, refine-loop residual); and nothing at
all for the imagery, because the P2 preview endpoint already carries frames and the reticle/rings
are client-side drawing on top.

The design question worth settling **first** is whether the node grows a *run kind* (session /
flats / polar-align) with one "what is this node doing" state, or a parallel endpoint group per
mode. Lean strongly toward the run kind: the lease already models exactly one run holding the rig,
`LiveSessionState.HasActiveRun` already asks that question, and a second parallel notion of
"running" is precisely the mistake the P0 work just finished undoing.

### Guide-cam image stream -- **SHIPPED (2026-08-05)**

**What landed:** `GET /api/v1/preview/guider` serving the live guide frame through the same
`PreviewEncoder` and the same `X-Frame-Number` contract as the per-OTA previews, plus
`RemoteSessionMirror` filling `LastGuideFrame` / `GuideStarPosition` / `GuideStarSNR` -- so the
Guider tab renders a remote rig through the code that renders a local one, with no knowledge that
it is remote. Its own route rather than an OTA index, because there is one guider for the whole
rig, its frames arrive at guiding cadence rather than per sub, and it is wanted precisely while the
science cameras are mid-exposure with nothing new to show; an OTA index would also collide with the
real profile-ordered numbering.

**The blocker recorded below was already stale when it was picked up.** `ISessionTelemetry.LastGuideFrame`
existed and `Session` already forwarded it from the guider driver, and `PreviewEncoder.EncodeJpegAsync`
already took any `Image`. The plumbing was in place.

**The real hazard was sharper than the note, and the primitive for it was unsound.** `GuideLoop`
does `LastFrame?.Release(); LastFrame = frame;` on every exposure, so at guiding cadence a request
that holds the reference across an await is encoding a buffer the camera has already taken back.
The failure is silent: a perfectly valid JPEG of a flat grey rectangle. The codebase had exactly
the right mechanism, `ChannelBuffer.AddRef`, except it checked liveness and then incremented as two
separate steps -- so a borrower could pass the check while the last holder took the count to zero,
and resurrect a recycled buffer. Nothing called `AddRef`, so the bug was latent until something
borrowed a live frame. Now:

- **`ChannelBuffer.TryAddRef`** -- CAS loop, only increments from a positive count, so a zero
  refcount stays terminal and the loser of the race learns it lost. `AddRef` delegates and keeps
  throwing. Reverting to the old shape fails the race test in 64 ms.
- **`Image.TryLease`** -- all-or-nothing over the planes, handing back a distinct `Image` whose own
  one-shot `Release` returns exactly the refs taken. Losing the race answers `false`, because for a
  poller "no frame right now" is the honest answer, not an error.

**The change token needed a new counter, and the obvious one was a trap.** `GuideLoop._guideFrameCount`
counts frames the loop *corrected on* and is incremented past the star-lost `continue`, so during an
outage it stands still while the camera keeps publishing -- a poller keyed on it would stop
refreshing at exactly the moment an operator wants to look at the guide camera and see the cloud.
The new count sits where the frame is published; both drivers had several publish sites each, so the
increment is funnelled through one setter rather than sprinkled, and the fake counts too since an
unattended end-to-end run drives this path.

**Guide frames are a separate opt-in** (`PreviewOptions.IncludeGuider`, default off) from the OTA
thumbnails, because the screens that want them differ: the home dashboard draws science previews and
never a guide frame, so bundling them made every dashboard poll pay a request and a decode for a
picture nothing shows.

**Still deferred:** the star-profile arrays and the calibration overlay. Both are per-poll array
payloads feeding panels that are often not visible, and neither can be derived on the client side --
cross-sections taken from the stretched, lossy preview would produce a confidently wrong FWHM rather
than no FWHM. They want their own opt-in fetch, the way the frame itself now has one.

### Multi-rig dashboard -- **SHIPPED (2026-07-28)**

**What landed:** `GuiTab.Home` as the landing tab, `HomeBoard.BuildCards` (the pure card projection) and
`HomeTab<TSurface>`, on top of three prerequisites that turned out to be missing and are now in place:
`SessionPromptEventArgs.RaisedUtc` / `PendingPromptDto.RaisedUtc` so a prompt's *age* is the node's own
truth rather than "when this client noticed"; `GET /api/v1/session/profile` so a node can say which
profile it runs at all (`ActiveProfileId` had no way out of the node, and `/profiles` lists what exists
without saying which is live); and per-mirror poll backoff for a rig that is not answering. Everything
below stands as the design record. **Not** part of it: multi-night progress (see "Leave room").

**Landed after** (2026-07-29): a **TUI home board** (`TuiHomeTab`) rendering the *same*
`HomeBoardLayout` tree with the same palette and the same `BuildCards` data -- the first tree genuinely
shared across surface kinds, which only became possible once a design unit resolved per axis (DIR.Lib
6.23) and the cell context could be told which convention a tree was authored in
(`CellMeasureContext.PixelAuthored`). The original text here said a TUI equivalent was out of scope
because "the TUI keeps its own tab table"; that is no longer true.

Also landed after: the **card's session detail**, below.

P4 binds many rigs but shows one at a time -- deliberately, because the overlay model says
selecting a rig changes what you *look at*. A dashboard is N mirrors polled at once behind a
compact per-rig card (rig name, the profile it is running, phase, target, frames, guide RMS, last
notification, and an outstanding-prompt badge).

**Card session detail -- SHIPPED (2026-07-29).** The first cut carried a status line, frames, RMS and
the prompt badge, and "last notification" was on the list above but never landed -- the status line
mirrors `CurrentActivity`, which is a different thing and is overwritten by every sub-step. Added, with
the shape reasons that matter for anyone touching it:

- **Progress is per TARGET** (`target 2/3 · frame 23/100`), not per session. A session total answers "has
  it been busy"; a board is scanned for "is this one nearly done". The denominator needed
  `ObservationDto.PlannedFrameCount` on the wire, and the mirror rebuilds its observations carrying that
  total (a single passthrough `FilterExposure`) so `ScheduledObservation.PlannedFrameCount` answers the
  same question locally and remotely -- one path, rather than a local branch and a wire branch. Frames
  done is counted **backwards** from the log tail, so it costs O(this target's frames) on a path that
  runs per card per frame. The denominator scales with the OTA count, because each OTA works the same
  plan in parallel and the log counts all of them.
- **Cooling is the row that justifies the screen during setup** (user, 2026-07-29): cooling several rigs
  in parallel is dead time, and the question is which are *ready*. Reported for the camera **furthest**
  from its setpoint -- a rig is ready when its last camera is. "Settled" is gated on the session's own
  `Phase is not Cooling` **and** the arithmetic: the ramp's real completion test is cooler power plus a
  consecutive-sample count (`CameraCoolingState`), which is not on the wire, so the 1 °C tolerance is
  documented as a *display* threshold and the phase outranks it. Never report finished early.
- **Time to meridian flip** is an **instant** on the wire (`SessionStateDto.MeridianFlipUtc`), never a
  remaining duration -- the same rule as `RaisedUtc`. A duration is only true when computed, so on a rig
  polled every 30 s a stored countdown moves in 30 s steps and reads as broken. The card subtracts at
  render time, which is why `HomeBoardLayout.Build`/`Card` take a `now`. Computed by the *session*
  (`MeridianFlipDecision.TimeUntilFlip`, stamped from the same HA read the flip decision uses, so the
  countdown and the flip can never disagree) because the answer needs the flip config, the pier side and
  the destination side -- none of which a remote observer has.
- **The card is content-sized against one shared box.** Every row past the first three is conditional, the
  card's height is the sum of the rows it built, and `Body` sizes every card to the tallest. `CardHeight`
  survives only as a floor. A constant would clip: the full row set is ~215 units against the old 132, and
  a clipped row is invisible in a build and looks like a missing feature on screen. One *shared* height
  rather than per-card is what keeps it a board -- per-card heights show up as ragged rows, and an idle
  rig's card would resize the moment its rig started a run.
- **Collapse is two levels, not a priority list** (`RigCardDetail`, chosen from the resolved COLUMN width,
  not the window's): Compact drops the note line and the HFD figure. Guide RMS stays -- a rig guiding
  badly is something you act on.

**Board shape -- SHIPPED (2026-07-29).** With the card grown, a small window could no longer hold it, so the
board gained a second shape and a header selector (`HomeBoardView`: `Auto` / `Cards` / `Table`, user,
2026-07-29). Three candidates were considered for the cramped case and the reasoning is the durable part:

- **A stack of overlapping cards, auto-shuffling the interesting one forward.** Rejected: it hides rigs
  behind other rigs, and the prompt badge is the one thing on this screen that must never be hidden. It is
  what the board exists to answer, two rigs can be waiting at once so only one could be at the front, and
  what you could see would depend on animation timing rather than on state.
- **Half cards** (a third, denser `RigCardDetail`). Rejected as redundant once the table existed: a card
  that keeps halving still pays a card's worth of chrome to say less than a row does.
- **A table, one row per rig.** Taken. Four rigs is five rows against twenty for cards.

The shared tree is not a casualty of this -- it is what makes it cheap. Both shapes are `Layout.Node`
projections over the same `RigCard` data in `HomeBoardLayout`, so the table renders on the GPU tab and the
TUI with no per-surface code, exactly as the cards do. A shared tree is a description language, not a fixed
shape.

Two behaviours worth keeping: **Auto names what it did** in the header ("table (window too small for
cards)"), because a screen that silently becomes a different thing reads as a glitch while a labelled one is
the nudge to enlarge the window; and **an explicit `Cards` is never overridden**, because second-guessing a
choice the user just made is worse than an overflowing board.

`Build` now takes the viewport rather than a pre-resolved column count -- both hosts had been running the
same `ColumnsFor` -> `ColumnWidth` -> `DetailFor` arithmetic, and each new input had to be threaded through
both again.

Two card details are already settled. **Title is the rig, subtitle is the profile it runs** -- which
makes the local node just another card rather than a special case, and puts the one field that
distinguishes two similar rigs (which optical train is on which pier) on every card. And **an offline
card can state its age**: `RemoteRigBinding.LastSeenUtc` plus the live
`RemoteSessionMirror.LastContactUtc` landed ahead of the dashboard, so "offline, last seen 6 h ago"
is available now (`RemoteRigActions.DescribeLastSeen`).

The **prompt badge is arguably the whole justification.** A prompt blocks a rig *indefinitely* -- the
node holds the run open with no timer, bounded only by observer liveness -- and today it is visible
only on the rig you happen to have selected. A board that shows "waiting 40 min for someone" is worth
more than phase, frame count and guide RMS combined.

**It is the home screen, not a multi-rig monitor.** Always the landing tab, listing everything you can
look at -- local and remote -- with live status. That framing is what makes the always-on tab correct
rather than conditional chrome, and what stops a single-scope user's one-card board being dead UI: a
home screen with one entry is still a home screen. The cost is one click per launch for a single-rig
user, paid for by seeing whether anything is *waiting on you* before diving in.

**Opening a view never actuates hardware.** The word "connect" covers two different acts here and only
one is safe to do on the user's behalf: a *remote* connect starts a read-only HTTP mirror (no lease, no
touch on the rig's hardware), while a *local* connect opens drivers and powers a mount. The local card
needs neither -- it reads `LiveSessionState`, which is populated whether or not any driver is
connected, so "this scope, idle, nothing connected" is an accurate free card. Concrete guard: **do not
add the dashboard to `PollPreviewTelemetry`'s `ActiveTab` gate**; it only polls already-connected
drivers, but keeping the board off it makes "the home screen does zero device I/O" a property that can
be stated rather than argued.

**Two prerequisites that are behaviour, not UI -- both now DONE.** (1) The rig path had no
`HttpClient.Timeout`, so it ran on the 100 s default and a rig that went dark read as reachable for
over a minute (mildly wrong for one rig, glaring on a board of six). `NodeTimeouts` now gives each
request its own budget (state poll 5 s, preview 30 s, control 10 s) behind a 60 s `HttpClient`
backstop, so a dark rig is reported unreachable in about five seconds. Budget expiry and caller
cancellation both surface as `OperationCanceledException` meaning opposite things (the rig is dark; the
caller went away), so a `catch ... when (...)` filter must test the ORIGINAL caller token, never a
linked one, or a dark rig reads as a cancelled poll. (2) A mirror existed only after
a rig was *selected*, so the board needed a connect-all path; because the board is always the landing
tab, that is effectively at startup -- which is what promoted the timeout fix from cleanup to
prerequisite, since an off rig would otherwise have shown as connecting for 100 s on every launch.
`RemoteRigActions.ConnectAllAsync` is that sweep: idempotent (so it can be re-run when a rig comes
online, picking up only what is still missing), best-effort per rig (one unreachable rig or one
unwritable binding file must not stop the others), previews left off, and deliberately **without**
touching the view context -- connecting every bound rig must not change which one is on screen, or the
sweep would silently become a selection.

**Tab identity: `GuiTab.Home`, icon `\U0001F3E0` (house), `Ctrl+H`** (user, 2026-07-27). The icon has
to stay **neutral between local and remote**, which rules out the family that first suggests itself --
satellite / antenna / globe all read "remote", and that quietly contradicts the settled rule that the
local node is *just another card*. A network icon would turn the landing screen into a
remote-monitoring tab and make a single-scope user's one-card board look like a feature they do not
use. The icon also names the **screen**, not the rig list, because multi-night progress is landing
beside the cards. House says both. `Ctrl+H` is free (E/P/S/L/M/G/N are taken) and agrees with the name.

Two practical notes for whoever wires it. Every existing sidebar icon is a **bare codepoint with no
variation selector** (🔭 Equipment, 📅 Planner, 🌌 Sky Map, 🚀 Session Setup, 📷/📸/🧭/🪐 Live Session,
🎯 Guider, 🔔 Notifications) -- keep that property, since the VS16 emoji are the ones that render
inconsistently through the bundled emoji font. `U+1F3E0` qualifies. And icons are written in source as
`\U0001F3E0` escapes, not literal glyphs, so the edit needs a tool that can match escape sequences (see
`reference_edit_unicode_escape_gotcha`).

Adding a tab touches six places, and the last one is the easily-missed one: the `GuiTab` enum;
`GuiAppState.TabOrder` (**first**, since it is the landing tab); `VkGuiRenderer.TabChrome` (icon +
tooltip); the `GuiEventHandlerBase` `Ctrl+<letter>` map; the two `VkGuiRenderer` switches (tab instance
+ render); and `GuiTabNavigationTests.TabOrder_IsTheSidebarLayoutOrder`, which pins the exact order and
will go red by design.

**Leave room -- do not fill the viewport** (user, 2026-07-27). The rig cards are one section of a home
screen, not the whole of it: multi-night progress per target ("M31, 4.2 h of 12 h, over 3 nights", the
Vaonis smart-scope feature) is the intended neighbour, so the home screen answers both axes -- what is
happening *now* per rig, and what is accumulating *over time*. Tracked as the display half of
[`docs/todo/sequencing.md`](../todo/sequencing.md) "Multi-night scheduling"; **not part of the dashboard
change.** The layout consequence is concrete and worth getting right first time: build the rig section
**content-sized** with a trailing `Spacer` absorbing the slack -- NOT Star-sized to fill. (As shipped it
is a `Grid(columns).WithAutoRows()`, not the `WrapH` sketched here: fixed-width cards in a flow leave
ragged space at the right edge and do not line up as a board. The content-sized property is the same.) A Star-sized section has to be reworked to add a second one, and every card
silently resizes when it is.

`RemoteRigRegistry` already holds multiple connections and each `RemoteSessionMirror` polls
independently, so the real work is the parts that only appear at N: a compact card widget, the
connect-all path above, and **per-mirror backoff for a rig that is not answering**. Be precise about
what that last one is and is not: there is no shared tick, so one offline node structurally *cannot*
stall the others -- each mirror owns its own `Task.Run(PollLoopAsync)`. What is missing is that a dark
rig is retried at the full live cadence forever, which six dark rigs turn into steady pointless
traffic. Backoff is the fix; "an offline node stalls the board" was never the failure mode. **As
shipped:** the interval doubles per unanswered poll, capped at 30 s (matching `TianWenEventStream`'s
reconnect ceiling), derived from a consecutive-failure count so one answer resets it with no separate
recovery step -- and a **404 counts as an answer**, since an idle rig is a healthy one and backing off
on it would make the rig slower to notice the moment it starts a run. Two invariants to set deliberately at the start rather than discover: **previews stay
off** on the dashboard (P3 made them opt-in per mirror for exactly this reason -- N mirrors each
pulling JPEGs is the failure mode), and **the dashboard is read-only by construction** -- driving
still means selecting a rig, which keeps the overlay model intact instead of quietly inventing a
second way to command hardware.

### Hosted planetary mode

The one item where "poll a JPEG" genuinely does not work: planetary is a high-rate video stream
feeding a CPU-heavy rolling stack. The realistic shape is not "mirror the planetary tab" but "the
node runs the pipeline and you steer it" -- capture + `RollingWindowStacker` stay on the node
(where the camera is, which is the entire point), and only the stacked master preview streams back
at a low rate, with exposure/gain/ROI and the six wavelet layers sent *up* as parameters. That is a
genuine new feature rather than a wire-up, which is why it sits below the other three.

### Profile editing over the API

`POST /api/v1/profiles` accepts only `Name` today (found while writing the Alpaca round-trip
tests). Real editing means sending device assignments, OTA specs and site -- and immediately raises
last-writer-wins against the rig's own GUI, on top of `ProfileSwitchGate` already refusing a switch
while anything is connected or running.

Held back on principle, not effort: the overlay model says you *look at* a rig, you do not
reconfigure it. Editing is the one operation that mutates the remote node's identity rather than
its activity. If it lands, it wants gating in the same family as the lease.

### Auth / TLS

Unchanged posture, stated once here so it is not mistaken for an omission: the server binds
`0.0.0.0` plain HTTP with no auth, LAN-trust, and **every mutating endpoint was already
unauthenticated before this plan** -- discovery makes nodes findable, not more privileged.
Hardening is a separate plan that applies to the server as a whole.

One thing to be explicit about: **the device lease is coordination, not a security boundary.** It
stops a second *well-behaved* client from stealing a rig mid-night; it does not stop anyone on the
LAN who wants to. Do not let "devices are leased now" read as "the plane is protected".

### WAN relay

Discovery is UDP broadcast, so it is LAN-only by construction. Across the internet the zero-code
answer today is a VPN (Tailscale/WireGuard) -- bind, route, done -- which is exactly why this is
deferred: it is an ops choice, not a missing feature. A real relay/rendezvous service would make
auth a **prerequisite** rather than a companion item.

### Full ASCOM conformance (N.I.N.A. / SharpCap)

P5's scope is the members our own `AlpacaClient` calls, pinned by round-trip tests. Third-party
clients call more, and ConformU is the arbiter. Two known gaps beyond breadth: Alpaca has **no
Guider device type at all**, and the three deliberate `NotImplemented` members
(`readoutmodes`/`readoutmode`, filterwheel `focusoffsets`) would need honest answers rather than
the current "TianWen does not model this that way". A conformance grind, not a design problem.

## Testing

- `LAN.Lib`: chess's fake-transport + FakeTimeProvider tests ported (beacon cadence, expiry,
  echo filter, bye, foreign-datagram tolerance); stable-nodeId mint/reload test.
- Server additions: existing hosting test patterns; AOT publish + smoke test per the Hosting
  invariant (RDG + source-gen contexts -- publish, not just build).
- Mirror: unit tests against a scripted `HttpMessageHandler` + in-memory WS; a `FakeTimeProvider`
  poll loop mirroring the session-test pump pattern.
- E2E: `tianwen-server` with a fake-device profile (server already registers fakes,
  `IncludeFake:true`) + GUI attached to it -- the existing unattended-GUI harness drives this;
  the inspector's `describe_ui` asserts remote mode renders the session.

## Networking (ports that must be open)

Two distinct ports, both plain unencrypted -- no TLS/auth on either (see Security below):

- **UDP 52821** -- `LanProtocol.DiscoveryPort`, the shared LAN.Lib broadcast domain. Every
  SharpAstro app on the LAN (this feature's `tianwen-server`/`tianwen-gui`, plus chess) sends
  *and* receives on it, so it must be open both directions on every node that discovers or is
  discovered -- a listen-only consumer (`Announce = false`) still needs inbound UDP 52821 to
  receive others' beacons. `UdpLanTransport` sets `ReuseAddress` so multiple local apps can share
  the port on one host.
- **TCP, the configured `tianwen-server` `--port`** (default **1888**) -- the actual native v1
  HTTP + WebSocket API a remote GUI/client connects to for control and mirroring. Inbound-only on
  the server node; nothing else needs it open.

**Windows Firewall is the practical gotcha on a headless rig.** A first `dotnet run`/interactive
launch of an app that binds `0.0.0.0` normally trips the "Windows Defender Firewall has blocked
some features" prompt -- but a mini PC running `tianwen-server` unattended (no one there to click
Allow) needs both rules added ahead of time, e.g.:

```powershell
New-NetFirewallRule -DisplayName "TianWen Server (TCP 1888)" -Direction Inbound -Protocol TCP -LocalPort 1888 -Action Allow
New-NetFirewallRule -DisplayName "LAN.Lib Discovery (UDP 52821)" -Direction Inbound -Protocol UDP -LocalPort 52821 -Action Allow
```

Linux mini PCs (a plausible headless rig target) need the UFW/iptables/firewalld equivalent; not
yet scripted anywhere. **Deferred:** a firewall-rule check/setup step in `release-tianwen` or a
first-run helper -- not done as part of P1, noted here so it isn't lost.

## Security (unchanged posture, stated once)

The server binds `0.0.0.0:1888` plain HTTP with no auth -- LAN-trust, as today. Discovery makes
nodes *findable*, not more privileged: every mutating endpoint already exists unauthenticated.
Any future hardening (shared token, TLS, bind-address config) is a separate plan and applies to
the server as a whole, not to this feature.

## Open questions

- Sibling name: `LAN.Lib` (matches the `X.Lib` family; chess's `Lan*` concept names carry over)
  vs `SharpAstro.Discovery`. Lean: `LAN.Lib`.
- Should the GUI beacon too (symmetric, `service="tianwen-gui"`, free multi-client awareness) or
  listen-only (`Announce=false`)? Symmetric costs one datagram/sec; take it.
- Preview transport: per-OTA JPEG poll (simple, sufficient on LAN) vs binary WS push (fewer
  round-trips, more protocol). Poll first; push is a drop-in upgrade behind the same mirror.
- Does the contracts split (`TianWen.Hosting.Contracts`) also absorb `NinaApiJsonContext` DTOs?
  No -- the mirror speaks native v1 only; nina DTOs stay put.
- ~~Device access alternative worth naming: the mini PC could expose its devices as ASCOM Alpaca
  servers ... The hub-level native API (P5) is the better fit ... not recommended.~~
  **RESOLVED the other way (2026-07-27) -- the Alpaca route is what shipped.** The objection
  assumed a separate Alpaca server stack on the node (Windows ASCOM or a third-party
  implementation); serving it *from `tianwen-server` itself* was the option not considered, and it
  keeps every advantage the native API was credited with -- one process, one port, one auth story
  later, the node stays a plain `tianwen-server` -- while deleting the entire client half
  (`AddAlpaca()` already existed) and getting ImageBytes for free. "Devices only, no
  session/planner/state" was accurate and turned out not to be a drawback: native v1 remains the
  session plane, and the two planes do not overlap.

## The Home tab: design decisions and the traps it hit (moved out of CLAUDE.md, 2026-08-20)

The multi-rig dashboard's own reasoning, kept verbatim. Several entries are DIR.Lib layout-engine
traps that were discovered here and fail silently; the one-line versions live in CLAUDE.md's
Layout DSL section, and the measured detail is here.

- **`HomeBoard.BuildCards` is the pure projection; `HomeTab<TSurface>` only draws.** The tab renders the
  `ImmutableArray<RigCard>` snapshot published on `GuiAppState.HomeCards` and never touches
  `RemoteRigRegistry` or a `LiveSessionState` -- same shape as `EquipmentTabState.BoundRigs`, and it also
  makes it impossible to paint a card from a session being mutated underneath the frame.
- **Read-only with respect to hardware.** A card click changes which rig you *look at*, via the same
  `SelectRemoteRigSignal` / `SelectLocalContextSignal` the profile picker posts. Nothing on this screen
  connects a driver, commands anything, or takes a lease.
- **Zero device I/O**, and structurally so: cards are built in the pre-gate part of `PollPreviewTelemetry`
  and the board is **not** added to that method's `ActiveTab` gate (which exists to guard polling
  already-connected *drivers*). Previews stay **off** -- N mirrors each pulling JPEGs is the failure mode
  `RemoteSessionMirror.Previews` was made opt-in for.
- **The rig section is content-sized** (a `Grid(columns).WithAutoRows()` plus a trailing `Spacer`), never
  Star-filled, because multi-night progress is the intended neighbour on that screen and a Star-sized
  section would have to be reworked to admit it. A grid rather than a `WrapH` flow because fixed-width
  cards in a flow leave ragged right-edge space and do not line up as a board.
- **A card is as tall as the rows it built, and every card shares the tallest one's box.** Rows past the
  first three are all conditional, so `CardHeight` is a **floor**, not the height -- it used to be exact
  and had to be raised by hand whenever a row was added, which silently clipped the last row when it was
  not. One shared height (computed in `Body`, not per card) is what keeps it a board: per-card heights
  read as ragged rows, and an idle rig's card would resize the moment its rig started a run.
- **The flip countdown is an instant, resolved at render time.** `ISessionTelemetry.MeridianFlipUtc` →
  `SessionStateDto.MeridianFlipUtc` → `RigCard.TimeToMeridianFlip(now)`, which is why `HomeBoardLayout`
  takes a `now`. Same rule as the prompt's `RaisedUtc`, for the same reason: a stored duration is only
  true when it was computed, so on a rig polled every 30 s it steps in 30 s jumps and reads as broken.
  The session computes it (`MeridianFlipDecision.TimeUntilFlip`, stamped from the same HA read the flip
  decision uses) because the answer needs the flip config and the destination pier side.
- **Cooling reports the camera furthest from setpoint, and "settled" is gated on the session's phase.**
  A rig is ready when its *last* camera is. The ramp's real completion test is cooler power plus a
  consecutive-sample count (`CameraCoolingState`) and is **not** on the wire, so the 1 °C figure in
  `RigCardCooling.SetpointToleranceC` is a display threshold and `Phase is Cooling` overrides it --
  reporting "finished" early is the one thing this row must not do.
- **Progress is per target, and `PlannedFrameCount` is why it has one path.** `target 2/3 · frame 23/100`
  needs a denominator, so `ObservationDto.PlannedFrameCount` crosses the wire and
  `RemoteSessionMirror.ToScheduled` rebuilds the plan carrying that total -- so
  `ScheduledObservation.PlannedFrameCount` answers identically for a local session and a mirror instead of
  the card branching on which it is. Frames-done counts **backwards** from the exposure-log tail (the run
  images one target at a time, so they are the tail) because this runs per card per frame.
- **Two shapes, selected in the header** (`HomeBoardView`: `Auto` / `Cards` / `Table`, posted as
  `SetHomeBoardViewSignal`). Auto is the default and the only value that reacts to the window: it swaps the
  cards for a one-row-per-rig table when the grid would not fit, and the header **says why** ("window too
  small for cards") -- a shape that changes with no explanation reads as a glitch, whereas a named one is
  the nudge to enlarge. An explicit Cards is never second-guessed. The rejected alternative was a stack of
  overlapping cards: it hides rigs behind other rigs, and the prompt badge is the one thing that must never
  be hidden (two rigs can be waiting, so only one could be at the front). Both shapes are `Layout.Node`
  projections over the same `RigCard` data in `HomeBoardLayout`, so the shared tree costs nothing here --
  it is a description language, not a fixed shape, and both surfaces get the table for free.
- **The header's two controls are icon segments plus a four-state theme cycler**, and both are shared-tree
  nodes rather than per-surface chrome. The shape selector is three `Layout.Content.Icon` leaves
  (DIR.Lib 7.18): the pixel painter builds each from rectangles and `CellLayout` picks a block-element
  glyph, which is why an icon names its MEANING (`Grid` / `List` / `Auto`) and not its drawing -- a `Text`
  run carrying a symbol character would be .notdef on a pixel surface missing that face, and rectangles do
  not exist on a cell one. **Auto keeps a visible segment**: it is the default state, so lighting nothing
  would leave the board's commonest configuration unlabelled, and the camera-convention bracketed `A` is
  what makes it sayable at icon size. The theme cycler beside it advances System -> Light -> Dark -> Night
  (`CycleUiThemeSignal` -> `GuiTheme.CycleTheme`) and shows a MARK PLUS THE WORD, selected by
  `ThemeControlStyle` (a code-level choice, not a user setting). Icon-only is a real option and the wrong
  default: three marks cover four states because Night IS a dark scheme and takes the same crescent as Dark,
  and inside Night the whole UI is red, so the colour that would separate them says nothing -- on the one
  control whose job is telling an observer at the mount which scheme they are in. Cycling into Night records
  where it came from, so a later F12 restores that rather than a stale toggle memory.
- **`.RowH(h)` sets `Width = Star` and silently eats a `.WFixed(w)` before it.** It means "a full-width row
  of fixed height", which is right for a card and wrong for a button. The view segments were built that way
  from the start, so `ViewButtonWidth` was inert for their whole life and three buttons sprawled across the
  bar; use `.WFixed(w).HFixed(h)` for anything that is genuinely fixed on both axes. Neither a build nor a
  screenshot review catches this -- only an arranged rect does, which is what
  `HomeTabLayoutTests.TheSegmentsKeepTheirFixedWidthInsteadOfSharingTheHeader` asserts.
- **A `Stack` places children at the cross-axis START, so centring a row's controls needs
  `.CrossCenter()`** (`Layout.CrossAlign`, DIR.Lib 7.21). Without it a `HFixed` control in a taller bar hugs
  the top and its centre sits half the slack high -- which the header did, visibly, and which reads as a
  styling bug rather than a layout one. Do **not** re-solve it by padding the container or wrapping each
  child in a spacer sandwich: both worked, and both re-derive at the call site a position the engine already
  knows, the padding version also insetting the row's label horizontally as a side effect. The Home header
  is the reference consumer, pinned by `EveryHeaderControlSharesOneTopAndBottom_CentredInTheBar`.
- **An icon draws at the size it DECLARES and every kind inks that full square** (DIR.Lib 7.20 + 7.21).
  Before 7.20 `Content.Icon.Size` was a measure-time hint only, so a 13-unit mark in a 20-unit cell painted
  at 20 and stood a third taller than the label beside it; before 7.21 the kinds inked between 63% and 100%
  of their declared size, so a row of different marks was ragged whatever the alignment. Both were measured
  from rendered ink rather than eyeballed, which is the only way to see either. Consequence for a consumer:
  **size a mark to the text it sits beside** (about 13 units against a 13 px label's ~10 units of cap
  height), and size it independently where it stands alone in a button.
- **`Build` takes the viewport, not a resolved column count.** Columns, card detail and cards-vs-table are
  all decided inside it; both hosts previously ran the same `ColumnsFor` -> `ColumnWidth` -> `DetailFor`
  arithmetic, and every new input had to be threaded through both again.
- **`ColumnsFor` clamps to the rig count.** Without it a 200-column terminal resolves to six columns for
  four rigs: two empty, and the four real cards squeezed under `FullDetailCardWidth` -- so the cards got
  *narrower the wider the window was*.
- **Two layout-engine traps this surface hit, both silent.** A `Node`'s default `Width` is `Sizing.Auto`,
  so a container whose children are all Star measures to a near-zero intrinsic width and arranges to
  nothing -- the table needs an explicit `.WStar()`. And `.CollapseBelow(u)` must **not** be paired with a
  Star *minimum* on the same node: a min-clamped Star holds its floor and overflows, so the threshold is
  never reached. The engine also prunes every under-threshold child in ONE pass rather than shedding the
  least important first, so a column that must survive takes **no** threshold rather than a small one.

