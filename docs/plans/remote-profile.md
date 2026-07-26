# Remote Profile: mirror a rig's tianwen-server in the GUI "as if local"

**Status: P1 DONE (2026-07-24).** Trigger: several rigs have a mini PC on the
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
  (still open).

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
Proposed rule: **session running -> device endpoints are read-only** (state/telemetry fine, actuation
refused); **no session -> full control**; and a remote `Connected = false` must never disconnect a
session-owned driver.

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
   500'd on an ordinary single-OTA session. **Fixed here**, since API coverage is otherwise thin: one
   shared `JsonNumber.Finite` in Contracts, applied at every wire boundary, pinned by
   `HostingWireNumberTests` (all-NaN sources through the real projections and real contexts, verified to
   fail when a guard is removed) and re-checked against the published binary.
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
   <br>Still to do here: preview JPEGs into `LastCapturedImages` (Part 2.1), prompt round-trip
   (Part 2.2), and routing session lifecycle through the mirror (`POST /session/start` with the
   schedule DTO of Part 2.8, `/session/flats`, abort) -- the client methods exist, the wiring is P4.
   <br>Verified: 18 `RemoteSessionMirrorTests` against a scripted `HttpMessageHandler`, including a
   **round-trip through the real server-side projection** (`SessionStateDto.FromSession` over a
   substituted telemetry, serialized and read back) -- the test that would have caught the `required`
   bug. Full suites 3448 unit + 311 functional green; AOT publish clean apart from the 2 known
   LibUsbDotNet rollups, with the published binary smoke-tested (GET, complex-body POST, the
   formerly-`object` endpoint, and the nina shim).
4. **`RemoteDeviceHub : IDeviceHub`** (in RemoteClient, over Part 2.9): driver proxies
   (`RemoteCameraDriver` etc.) so preview telemetry/capture, equipment connect/cool/jog,
   and sky-map slew/sync compile and behave unchanged. Precedent: the Alpaca backend already
   IS an in-repo remote-driver protocol -- this is the same shape over native v1.
5. **Fidelity tiers** (honest about what doesn't round-trip): tier 1 = everything in
   `SessionStateDto` (full Live Session tab, ~70% of `PollSession` unchanged); tier 2 =
   preview images, prompts, notifications, guider per-step stream, out-of-session preview
   capture + equipment control (needs Part 2 items + RemoteDeviceHub); tier 3 (local-only for
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
| P2 | Server session-mirror surface: preview endpoint, prompt bridging, 3 new WS broadcasts, structured devices, notification ring, telemetry depth, schedule-fidelity target DTO (2.1-2.6, 2.8) | -- |
| P3 | **MOSTLY DONE (2026-07-26)** -- `ISessionTelemetry` extraction, per-view-context `LiveSessionState`, `TianWen.Hosting.Contracts` split, `TianWen.RemoteClient` (`TianWenNodeClient` + `TianWenEventStream`) and `RemoteSessionMirror` all landed. Remaining: preview images, prompt round-trip and lifecycle routing, which are the parts that genuinely need P2 | P2 (for the remainder only) |
| P4 | Binding UX + drive mode (fetch profile, local planner, push `ScheduledObservationDto` schedule, remote start/stop/abort) | P1, P3 |
| P5 | Out-of-session remote device control: **either** the bespoke hub API (2.9) + `RemoteDeviceHub` + driver proxies, **or** an Alpaca server on the node consumed by the existing `AddAlpaca()` (see the 2.9 candidate above -- strongly preferred; no client-side code, ImageBytes free). Preview mode + Equipment tab remote control | P3 (the Alpaca-server route needs only P1, so it can land earlier) |
| Deferred | Hosted polar-alignment/planetary modes (server-side orchestration), profile editing, guide-cam image stream, auth/TLS, WAN relay, multi-rig dashboard | -- |

P1 and P2 are independent and can run in parallel. P3 is the headline ("as if local" session
mirror). P4 is the "remote profile" UX proper -- notably late in the sequence because the mirror
(P3) is what makes it useful, and because the binding UX is thin once the mirror exists. P5 is
the largest single chunk (out-of-session device control); it is what turns a session *monitor*
into full remote *operation*, and it can slip past P4 without blocking it.

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
- Device access alternative worth naming: the mini PC could expose its devices as ASCOM Alpaca
  servers and the GUI could drive them with its existing Alpaca backend -- that covers *devices
  only*, no session/planner/state, and needs an Alpaca server stack on the node (Windows ASCOM or
  a conformant implementation). The hub-level native API (P5) is the better fit: one transport,
  one auth story later, and the node stays a plain `tianwen-server`. Noted so the door stays
  marked, not recommended.
