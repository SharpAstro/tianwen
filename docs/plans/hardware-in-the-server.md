# Hardware in the server: the GUI drives every device through a local `tianwen-server`

**Status: PLANNED (2026-09-24, raised by the user); nothing started.** P0 is urgent on its own: it
closes a regression now on `main` (P0a) and server lifecycle bugs that already hurt remote rigs (P0b).

**The goal.** Exactly one process per machine owns hardware: `tianwen-server`. The desktop GUI (and
later the TUI) becomes a client of it over a per-user local socket, using the same API and client
library a remote rig already uses. A GUI that crashes, wedges its GPU, is closed or is restarted then
touches nothing: the session keeps imaging, the cooled cameras keep their setpoint, the mount keeps
tracking, and the mount limits stay enforced.

**Why now.** The Adreno X1-85 wedge (`gpu-device-recovery.md`) showed the failure mode: the GUI's
process is the rig's process, so anything that kills or freezes the window reaches the hardware.
SdlVulkan.Renderer 7.48 made that WORSE for a running night (P0a). An open native render crash on the
Equipment tab (exit 127, `docs/todo/drivers.md`) is the same failure by another route. With hardware out
of process, "recover the GPU" shrinks to "restart the window", which is what P7 does.

Surveys this plan builds on (2026-09-24, read-only, cited below by file): what the GUI does with
hardware today, the server and client surface, and every decision the docs already record. The
transport was proven by a spike (see "Transport").

## What this plan re-decides (read before arguing with it)

Four recorded decisions assumed the GUI owns local hardware. This plan reverses each on purpose:

1. **"Closing the client must abort the local session"** (`remote-profile.md`, P3 item 2, and
   `RequestQuit` in `TianWen.UI.Gui/Program.cs`). It becomes: **closing the GUI detaches; stopping the
   rig is an explicit act.** The local server is just another node, which the overlay model already
   handles; the rule "a session on a rig keeps running, closing the client that was watching it is not
   a reason to stop the rig" now covers the local rig too.
2. **"A respawn ends the night"** (`gpu-device-recovery.md`, the Drawboard prior-art section, which
   chose in-process device recreation because leases would have to be re-acquired). With hardware in
   the server, a GUI respawn re-acquires nothing, so a respawn becomes the SIMPLE recovery (P7), and
   in-process device recreation becomes optional.
3. **"You look at a rig, you do not reconfigure it"** (`remote-profile.md`, "Profile editing over the
   API"). The local socket's client IS the Equipment tab, so it must edit profiles. Resolved by
   TRANSPORT, not by relaxing the rule: editing endpoints answer only on the local socket, and a LAN
   client keeps today's rights.
4. **The GUI drives its own `MountLimitWatcher`** (`Program.cs`, `mount-safety-limits.md` P3). After
   the cut only the server runs one. Two watchers over one mount is a double actuation.

Decisions that STAND and bind this plan: the session always runs on the node that owns the hardware
(so the GUI never runs a session over proxied drivers); reads are never leased; polling is
authoritative and the WebSocket is a latency hint; one `LiveSessionState` per view context; a pushed
schedule beats the target queue; with no client attached a prompt gets the session's unattended
answer; numeric enums on the wire; AOT verified by `publish`.

## P0a: a dead GPU must not end the night (regression on `main`)

**What happens today, read from the code and not yet reproduced live.** SdlVulkan.Renderer 7.48
(SharpAstro/SdlVulkan.Renderer#111) declares a device that keeps refusing submits dead and stops the
event loop (event 117). That is the Adreno's actual failure mode, rejected submits. The GUI sets no
`OnGpuWedged`, so `loop.Run` just returns, and then `Program.cs`:

- calls `cts.Cancel()`, which cancels the local session, because `SessionBootstrapper` links the
  session's token to it;
- never calls `RequestQuit`, so no camera warm-up is queued for cameras outside a session;
- drains background work for at most 5 s, then lets the process exit, cutting the session's
  `Finalise` (park, warm-up, covers) off mid-ramp.

Before 7.48 the same wedge gave a frozen window, but the night completed underneath it and
`Finalise` ran at dawn. **The fix I shipped made the night strictly worse.** The fake
`reject` with no count reproduces it on demand (`gpu_fault`, DEBUG).

**Fix: keep running without a window.** This is what `gpu-device-recovery.md` already asks for: "a
host that cannot rebuild the GPU device keeps running headless, never a frozen window".

1. `OnGpuWedged` records that the loop stopped because the GPU died, and logs it at Critical.
2. After `loop.Run` returns for that reason, the process does NOT cancel `cts`. Instead:
   - prompts switch to the unattended answer (`SessionPromptEventArgs.DefaultIfUnanswerable`), since
     nobody can see them;
   - a running session is left to finish on its own, `Finalise` included;
   - a flat run is left to finish too;
   - polar alignment and planetary capture are cancelled (both are interactive and meaningless
     unseen; polar's `finally` reverses the axis or parks);
   - once the runs are done, the `RequestQuit` camera tail runs (warm-up or disconnect), with no 5 s cap
     while any warm-up is in progress;
   - then the process exits.
3. The SDL window stays pumped so it remains closable. Its title says "Display lost: the session
   continues (close to stop the rig)". Setting a title needs no GPU. Closing it then means "stop the
   rig": abort, wait for `Finalise` in full, exit.
4. The decision (continue, drain or exit) goes into a small class the tests can reach, following the
   `StandaloneViewerHost` pattern, instead of living in top-level statements.

Verify in a live Debug GUI with a fake rig: start a session, then `gpu_fault reject` with no count. The
session must run to its end and the log must show `Finalise`'s park and warm-up. Check first that
`ShutdownDrain.PumpUntilComplete`'s background-only paint does not touch the abandoned device.

**Out of scope for P0a.** Commands only run after a rendered frame: `bus.ProcessPending` sits in
`OnPostFrame`, so with the GPU dead no signal runs. The headless path must not rely on the bus.
Everything above is plain code after `loop.Run`.

## P0b: server lifecycle bugs (independent; they hurt remote rigs today)

Each item below is confirmed in the code, except where it says otherwise. The first four decide
whether a server can be trusted with a night at all:

1. **A session runs on the HTTP request's token.** `POST /session/start` passes the endpoint's
   `CancellationToken` (bound to `RequestAborted`) to `RunAsync`. A client dropping its connection can
   cancel the night, and a GUI restart is exactly that. The run belongs to a server-lifetime token that
   `HostedSession` owns.
2. **`HostedSession` is never started or stopped by the host.** It is registered as `HostedSession` and
   `IHostedSession`, never as `IHostedService`. A probe of `AddHostedSession()` lists exactly two hosted
   services, `MountLimitWatcherService` and `EventBroadcaster`. So `StartAsync`, and with it
   `SessionFactory.InitializeAsync` (the plate-solver check and device discovery), never runs, and
   neither does `StopAsync` on shutdown.
3. **`POST /session/abort` cancels nothing and disposes the rig under the run.** `_cts` is null (item
   2), so `StopAsync` skips the cancel and goes straight to `session.DisposeAsync()` while `RunAsync`
   carries on over disposed drivers. `Finalise` never runs properly. The fix: abort cancels the run's
   token, awaits `RunAsync` (and so `Finalise`), and disposes only afterwards.
4. **Host shutdown force-disconnects everything with no park and no warm-up.** On SIGTERM or Ctrl+C the
   container disposes `DeviceHub`, which force-disconnects every driver.
   - The fix: `HostedSession.StopAsync` runs the same safe stop as an abort.
   - Connected cameras outside a session are warmed before they disconnect.
   - That needs `EquipmentActions.WarmAndDisconnectAsync` moved from `TianWen.UI.Abstractions` into Lib,
     as a device-model operation in `Devices/*Extensions.cs`. The server does not reference
     `UI.Abstractions`, deliberately.
   - `HostOptions.ShutdownTimeout` must cover a warm-up ramp (up to 15 min), and a service manager's own
     stop timeout (systemd `TimeoutStopSec`) must be documented to match.

Then the correctness items:

5. **Every WebSocket event is probably dropped client-side.** The server sends
   `ResponseEnvelope<WebSocketEventDto>`, while `TianWenEventStream` decodes a bare `WebSocketEventDto`
   whose `Event` is `required`. So FrameWritten, PlateSolve and GUIDE-STEP never reach a remote mirror.
   No test sends a real server event through the real client: add that test first, see it fail, then
   fix.
6. **A "no frame yet" preview is HTTP 200 with a JSON body**, and the client checks only the status, so
   it decodes JSON as a JPEG.
7. **`EventHub` can call `SendAsync` concurrently on one socket** (fire-and-forget broadcasts, no send
   ordering). Give each client a send channel: a lock-free hand-off, per the lock rules.
8. **Native v1 in-session actuation skips the lease.** The mount and OTA endpoints call `session.Setup`
   drivers directly, without `DeviceOwnershipGate`.
9. **GUI: the abort confirmation cancels the LOCAL session while a remote rig is on screen.** The Live
   Session tab sets `ShowAbortConfirm` on the Active context, but `ConfirmAbortSessionSignal`'s handler
   cancels `LocalLiveSession.SessionCts`. Esc then Enter while watching a rig therefore aborts this
   machine's session if one is running, and silently does nothing otherwise. The prompt reply handler
   has the same shape. The survey also found preview, focuser, goto, solve and sync, planetary, jog and
   cooler NOT gated by context: they drive the local hub while a remote rig is on screen. **Verify each
   with a test before fixing**; this is the trap CLAUDE.md names ("reaching for Active where Local is
   meant"), in the other direction.

## Target architecture

```
tianwen-gui ──┐                            ┌─ IDeviceHub (the only one), leases, drivers
tianwen (TUI) ┼─ local socket (per user) ──┤  Session / flats / polar / planetary runs
LAN clients ──┘  TCP :1888 (opt-in)        └─ MountLimitWatcher, discovery, profiles (one writer)
```

### Transport: a Unix domain socket, named on the command line

The server listens on a Unix domain socket, which Windows 10 (1803+), Linux and macOS all support. The
GUI's client connects through `SocketsHttpHandler.ConnectCallback`, and the WebSocket through
`ClientWebSocket.ConnectAsync(uri, invoker)`. The protocol is today's native v1, so local and remote are
one API, one client and one test surface.

**Spike, 2026-09-24, win-arm64 Windows 11 26200, NOT elevated.** Kestrel `ListenUnixSocket` on
`%LOCALAPPDATA%\TianWen\spike.sock` (58 chars) served a GET and a WebSocket echo through that client
code. A warm 16 MB body came back in 12 ms. No elevation, no port, no firewall prompt, nothing reachable
from the LAN.

- **The path is an argument**: `tianwen-server --socket <path>`. The default comes from ONE shared
  helper in `TianWen.Hosting.Contracts`, a short name under the per-user `%LOCALAPPDATA%\TianWen` (or
  `$XDG_RUNTIME_DIR` / `~/Library/Application Support` elsewhere), so a restarted GUI finds a server
  that outlived the old one. A test or dev run passes its own path and gets an isolated node.
- **Access control is the directory's ACL**: the per-user AppData tree admits that user, SYSTEM and
  administrators. Nothing else authenticates, and LAN trust is unchanged for TCP.
- **Length**: `sun_path` holds about 108 bytes. The helper checks the length and fails with a message
  naming the path rather than binding something truncated.
- **The socket file outlives a crash**, and a bind onto it fails. Clean-up is gated by the lock below,
  never by probe-then-delete: two servers probing at once would both delete, and the loser would unlink
  the winner's live socket, leaving it running and unreachable.

### One server per user: the lock file is the gate

A server opens `node.lock` beside the socket with `FileShare.None` and holds it for its lifetime. That
maps to `flock(LOCK_EX)` on Unix, and the OS drops it on process death. Holding the lock is what entitles
a server to delete a stale socket and bind. A second server that cannot take the lock exits at once with
"a node is already running" and its pid, which it reads from the lock file's contents.

`InstanceGate` in AppShell makes its pipe the lock AND the transport. Here the transport is a socket on
every OS (Kestrel's named-pipe transport exists only on Windows, and one transport everywhere is the
point), and a socket file cannot be the lock, as the race above shows. So the lock is a separate file,
held for the server's whole life.

### Spawn and lifetime: the server outlives the GUI

`AscomHostProcess` is the right shape for a spawn (the parent passes the transport name as an argument,
then waits for the connection) and the WRONG lifetime: its helper sits in a kill-on-close job. The
hardware server must survive its parent:

- **Windows**: `CreateProcessW` with `CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB` (a LibraryImport,
  since `Process.Start` exposes no creation flags). A job that forbids breakaway, such as some launchers
  and debuggers, fails that call; fall back to a plain spawn and warn that the server will die with that
  job. **Spike this under `tools/start-app.ps1`, Visual Studio and the MSIX-less release build.**
- **Linux / macOS**: `posix_spawn` with `POSIX_SPAWN_SETSID`, so a closed terminal or a killed GUI
  session does not take the server with it; stdio to the log.
- **Readiness**: connect to the socket with a bound, then `GET /api/v1/node` for its identity and wire
  version. A failed spawn reports the server's stderr, which is `AscomHostProcess`'s order: kill before
  reading.
- **When the server exits by itself**: never while it holds hardware, meaning any connected device, any
  run, or any warm-up in progress. Otherwise, after an idle period with no client attached (decision 2).
  `POST /api/v1/node/shutdown` (socket only) performs the safe stop from P0b item 4, then exits.
- **Version skew**: the GUI and the server ship together, but an older server may still be running from
  before an update. The handshake compares wire versions. An idle older server is asked to exit and a
  new one spawned. A busy one is left alone: the GUI says an older node is running a session, connects
  read-only through the mirror, and offers to restart the node once the run ends.
- **One clock**: `TIANWEN_NOW` is frozen at EACH process's start, so a GUI and a server launched seconds
  apart disagree by the launch gap. The GUI passes its frozen offset to the child explicitly (a new
  `StartupTimeOverride` input), so the two agree exactly. A server started by hand anchors itself as
  today.
- **LAN exposure**: a GUI-spawned server listens on the socket only. `--port` opens TCP, as today, and a
  server run by hand on a mini PC keeps its current default (decision 3).
- **Logs**: `Logs/<date>/Server_*.log`, beside the GUI's.

### What crosses the socket

The rule that decides every row below: **an operation that must finish if the GUI dies runs in the
server as ONE request, never as a sequence of driver calls the GUI drives.** A warm-up ramp driven from
the GUI stops when the GUI does; one that runs in the server finishes.

| GUI feature today (survey, `AppSignalHandler.*`) | Over the socket | Server work |
|---|---|---|
| Equipment connect / disconnect / warm and disconnect | `POST /devices/{key}/connect\|disconnect\|warm-and-disconnect` | new; the ramp runs server-side, with the cooler safety check the GUI does today (`GetDisconnectSafetyAsync`; Alpaca `Connected=false` skips it) |
| Camera cooling, gain, offset, bin, ROI | device-plane settings | new |
| Camera, focuser, filter, mount telemetry (per-tab polls: camera 2 s, OTA 2 s or 1 s moving, mount 0.5 s slewing / 1 s settle / 10 s tracking / 2 s idle) | a device SNAPSHOT endpoint, authoritative, plus a `DEVICE-STATE` push on change | the server polls at those cadences while any client is attached, slower with none |
| Preview exposure, snapshot, plate solve, solve and sync | one operation each | new; solve and sync is expose, solve and sync as one request |
| Focuser jog / goto, filter change, mount goto / sync / nudge / park / tracking | device-plane actions, every one through `DeviceOwnershipGate` | partly exists, session-scoped only |
| Mount move-axis (polar Phase A; the ninaAPI shim has one) | start carries a LEASE the client must renew; the axis stops when it lapses or the connection drops | new. Safer than today: a GUI that dies mid-move today leaves the axis running |
| Discovery, profile edits (about 8 writers in the GUI), site reconcile, sensor capture, credential store | async discovery job with progress and a completion event; profile edit endpoints on the socket only | new; **the server becomes the only profile writer** |
| Session start / abort / prompts / flats | exists (`/session/*`); the GUI must subscribe to the mirror's prompts, which it never does today | P0b fixes |
| Full-resolution frames: preview, subs, guide frames, polar refine | linear frames, P4 | new |
| Polar alignment, planetary capture with its rolling stack | server run kinds, P5 | new |
| Mount limit verdict (the GUI reads `MountLimitWatcher.VerdictFor` every frame) | session-less telemetry field | new |

**Frames are linear and binary, never the preview JPEG.** The viewer's stretch, statistics, star
profile, plate solve and snapshot save all assume linear floats in memory, and a camera buffer
(`ChannelBuffer`) already is one: a `float[,]` per channel.
- **Wire format.** The float planes plus an `ImageMeta` header, bit-exact. When every sample is a
  whole number in 0 to 65535, which is the usual case for a camera frame in ADU, the planes are packed
  as 16-bit instead. That halves the bytes and is still lossless; the packer checks every sample as it
  writes and falls back to float on the first one that is not.
- **Rate.** At the spike's rate, a 26 MP frame is about 40 ms packed (52 MB) or 80 ms as float
  (104 MB), extrapolated. Measure a real frame before designing anything faster.
- **Fetching.** `GET /frames/{source}/latest?after=N` answers only when frame N+1 exists, and a
  `FRAME-AVAILABLE` push tells the client when to ask. The same endpoint serves a remote rig over TCP,
  so remote rigs get linear frames too.
- **Shared memory** (a `MemoryMappedFile` ring) only if a measurement asks for it.
- **Ownership.** `Image` ownership does not cross the boundary: the client owns what it decoded, and
  the server releases its buffer once sent.

**Planetary stacks in the server.** The ring, the stacker and the centre-of-mass recentering, which needs
the frame AND the mount, all move with the capture loop (survey ranking: hardest item). Only the rolling
master and a display-rate live frame cross the socket, drop-to-latest over a binary WebSocket channel.
This is also the only placement where a GUI crash keeps the stack.

**Render-thread reads go away.** The GUI reads hub and driver properties synchronously on the render
thread in about seven places: `IsConnected` in the Equipment, profile and Home views, and the camera
pixel size and sensor size on every sky-map frame. After the cut these read the snapshot, which also
retires a standing breach of "no blocking I/O on the render thread".

### Which rig the GUI shows

The local node is a node like any rig, found on the socket rather than by LAN discovery. The GUI's
**Local** view context binds to it; nothing else about view contexts changes. It must not show up as
"(remote)" in the peer table or as a second card of itself on the Home tab. Its identity is the node's
stable id, which is what `RemoteRigBinding` already persists against.

## Phasing

The cut is **one wave** ("cut an API in ONE wave"; "one path, designed first"). Two processes cannot
share hardware: a server probing serial ports while the GUI holds them is contention, not redundancy.
So the server surface is built and tested first while the GUI keeps its in-process hub. Remote rigs gain
each piece as it lands. Then the GUI switches over in one step.

| Phase | Scope | Proves it |
|---|---|---|
| **P0a** | A dead GPU keeps the night alive (above) | live `gpu_fault reject` over a fake session: the session ends at its own time, `Finalise` runs |
| **P0b** | Server lifecycle and wire bugs 1-8; the GUI context-gating bug 9 | a test per item that fails first; a server test that aborts a session and sees park and warm-up |
| **P1** | Local node transport and lifetime: `--socket`, the lock, spawn with breakaway / setsid, readiness, idle exit, version handshake, clock hand-off, LAN opt-in; `TianWenNodeClient` and the event stream over the socket | functional tests spawn a real server on a temp socket with fakes; AOT `publish -r win-arm64` and `linux-arm64`, then run it |
| **P2** | Session-less device plane: connect / disconnect / warm-and-disconnect, cooling and camera settings, focuser, filter, mount actions, leased move-axis, snapshot plus `DEVICE-STATE`, mount-limit verdict, `DeviceOwnershipGate` on every actuation | the server functional suite drives the Equipment flows the GUI runs today, end to end, against fakes |
| **P3** | Profiles and discovery on the server: async discovery job, profile edits (socket only), site reconcile, sensor capture, credential store; the server as the one profile writer | reconcile and edit parity tests against today's `EquipmentActions` results |
| **P4** | Linear frames: binary format, `FRAME-AVAILABLE`, guide frames; a network-backed `LiveFramePreviewSource` | a round-trip test that is pixel-exact against the camera's own buffer; a measured 26 MP transfer |
| **P5** | Run kinds: polar alignment, planetary capture with the rolling stack and recentering, preview / snapshot / solve and sync | each mode run over a fake rig through the socket, including a client killed mid-run with the run continuing |
| **P6** | **The cut**: the GUI drops `IDeviceHub`, every device source and its `TianWen.Devices.Native` reference; Local means the local node; quit detaches; its `MountLimitWatcher` is deleted; inspector snapshot fields read the mirror; `unattended-ui-driving.md` and the E2E harness spawn a server; packaging ships `tianwen-server` inside the GUI's archive and `.app` (every file in `Contents/MacOS` signed) | the unattended-driving flows pass unchanged against a spawned server; killing the GUI mid-session leaves the session running; a new GUI re-attaches to it |
| **P7** | GPU recovery by respawn: `OnGpuWedged` starts a successor GUI and exits; `gpu-device-recovery.md` updated (in-process recreation becomes optional) | `gpu_fault lost` mid-session: a new window appears on the same session within seconds |
| **P8** | The TUI as a client (its exit today does not cancel its token and warms nothing) | the TUI drives a fake session through the local node |

## Decisions for the user

1. **Closing the GUI while a run is active.** Recommended: the quit dialog offers "Leave the rig
   running" (the default) and "Stop the rig and quit" (abort, `Finalise`, warm-up, disconnect, with
   progress). The alternative is always detaching silently.
2. **When an idle server exits.** Recommended: after 10 minutes with no client and no hardware
   connected. The alternative is staying resident until logoff.
3. **LAN exposure of a GUI-spawned server.** Recommended: off, with a setting to share the rig on the
   LAN (TCP :1888). A server run by hand keeps its current default.
4. **Profile editing rights.** Recommended: local-socket clients only.
5. **Where the planetary stack runs.** Recommended: in the server, since only the master and a live
   frame cross.
6. **Wire format for frames.** Recommended: float planes plus the `ImageMeta` header, packed losslessly
   to 16-bit when every sample allows it. The alternative is streaming FITS, which is self-describing,
   but a socket cannot seek and parts of our FITS read path do (CLAUDE.md, the gzip note under "The
   image is not necessarily in HDU 0").
7. **Migration.** Recommended: build the server surface first, then cut the GUI over in one wave (P6).
   The alternative is a startup flag with both paths alive, which this repo's rules argue against.

## Open questions (engineering, not the user's)

- **OS shutdown and logoff give a server seconds, not the 15 minutes a warm-up ramp takes.** What is the
  least-bad stop: cooler off at once, or leave the cooler running at its setpoint?
- **ASCOM COM drivers in the server run on MTA thread-pool threads, as they do in the GUI today.** No
  change, but `tianwen-ascomhost` is still not shipped with any app (`ascom-oop-host.md`), and its
  kill-on-close job is now correctly scoped to the server.
- **`user.config`-scoped ASCOM driver settings do not travel between host processes** (the Gemini
  `MyComPort` bug, `ascom-oop-host.md`). Check each driver that keeps its own settings, because the
  process that opens it changes at P6.
- **Canon EVF and live stacking have no server endpoints and no row above.** Place them in P5 or defer
  them explicitly.
