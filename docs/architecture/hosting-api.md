# The hosting API: `TianWen.Hosting` + `TianWen.Server`

Moved out of `CLAUDE.md` (2026-08-22), which keeps the invariants a caller trips over and points
here for the rest. The headless node serves a REST + WebSocket surface plus an ASCOM Alpaca device
plane, all on one ASP.NET Core host, AOT-published as `tianwen-server`.

Related: [../plans/remote-profile.md](../plans/remote-profile.md) (what a client does with this
surface), [stacking-render-pipeline.md](stacking-render-pipeline.md) (what the enhance endpoint
runs), [../plans/server-enhance-job-model.md](../plans/server-enhance-job-model.md) (the multi-job
model this deliberately is *not* yet).

## Two API layers on one host

- **Native v1** (`/api/v1/`): multi-OTA, camelCase JSON, POST for mutations. This is the session
  plane.
- **ninaAPI v2 shim** (`/v2/api/`): single-OTA (maps to OTA[0]), PascalCase JSON, GET for everything.

`IHostedSession` holds the node's run (the one going on, or the last one to end, until the next start
replaces it), `ActiveProfileId`, `PendingTargets` (pre-session queue, drained into
`ScheduledObservation[]` at `/session/start`), `PendingSchedule`, the outstanding `PendingPrompt`, and
a `Notifications` ring. `EventBroadcaster` (`BackgroundService`) attaches to each run as the node starts
it (`HostedSession.RunStarting`, before the run's body is released, so the run's first events reach the
clients), subscribes to `PhaseChanged` / `FrameWritten` / `PlateSolveCompleted` / `ScoutCompleted` /
`GuiderStateChanged` / `PromptRequested`, and pushes through `EventHub`'s two pools; it is also the
node's **notification recorder** (it already watches every session event, so it writes what it
broadcasts into the ring).

**A broadcast never waits for a socket.** Every client has a bounded queue drained by one sender of its
own: a broadcast serialises the event once per pool and queues it, so one stalled client costs nobody
else anything, and order holds per client. A client whose queue fills, or whose send outlasts the send
timeout, is dropped (its socket aborted) and resyncs by polling, which is the authoritative channel.
It used to send to every client in turn on the broadcasting thread, so one stalled client held up every
other client and every later broadcast, without bound (P0b item 7 of
[../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), #752).

Run: `dotnet run --project TianWen.Server` or `tianwen-server [--port 1888] [--socket <path>] [--local-only]`.

## Where the node listens, and which node it is

**Every node listens on its socket, however it was started**: a Unix domain socket under the per-user AppData
root (`NodeSocket.DefaultPath`, `node.sock`; `--socket` names another), which Windows 10 1803+, Linux and macOS
all serve. A node run by hand is therefore the machine's node, and a client finds it there rather than starting
a second one onto the same hardware (P1 of [../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md),
#917). It also listens on TCP `--port` (1888) and announces itself on the LAN, unless `--local-only`.

- **One node per socket: `node.lock` beside it is the gate** (`NodeLock`), opened with no sharing (an exclusive
  open on Windows, `flock` on Unix) and held for the node's life, so the OS drops it however the node dies. A
  second node exits at once (`NodeExitCodes.AlreadyRunning`) naming the running one, which it ASKS over the
  socket, since a held lock cannot be read on Unix. The lock file is never deleted: deleting it races the next
  holder into a second lock under the same name.
- **Only the lock's holder clears a stale socket** (`NodeLock.ClearStaleSocket`, which `ListenOnNodeSocket` takes
  the lock to call), never probe-then-delete, under which two starting nodes both delete and the loser unlinks
  the winner's live socket. A node that stops removes nothing itself: the runtime deletes a socket file when the
  socket that bound it is disposed, so only a crash leaves one.
- **The socket is the access control**: owner-only on Unix (`RestrictNodeSocketToItsOwner`, since connecting
  takes write permission and the umask would decide it), the AppData folder's ACL on Windows. Nothing else
  authenticates a client on it.
- **A path is refused, never truncated**: `sun_path` holds 107 bytes on Linux and Windows and 103 on macOS
  (`NodeSocket.TryValidate`, in bytes, so an accented user name costs two per letter).
- **`GET /api/v1/node`** (`NodeInfoDto`) answers the node's stable id (the one the LAN announcement carries,
  `NodeIdentity`, minted once into `lan-node-id.txt`), its build, its wire version (`NodeWire.Version`, the only
  compatibility check), its pid, whether it is shared, and how many TianWen clients are attached. A client asks
  it first.
- **A client reaches it through `NodeTransport`**: `OverSocket` for the local node, `OverTcp` for a remote rig,
  the same `TianWenNodeClient` and `TianWenEventStream` above either. `--node-socket` / `TIANWEN_NODE_SOCKET`
  (`NodeSocket.TryGetNamed`) point a client at a node and forbid it to start one.

- **A client starts the node from its OWN directory, never from PATH**, so it always gets its own build:
  `src/ExeBeside.targets` builds and publishes `tianwen-server` beside `tianwen-gui`, `tianwen` and the
  functional tests, and `tianwen-ascomhost` beside the server, which looks for it beside itself. A file the
  client writes itself is only compared, and one the program carries in another version fails the build naming
  it; every other file is the program's and is copied.


**A client starts the KEEPER, never the node** (`tianwen-server --keeper`, `NodeKeeper`, decision 10). The keeper
takes no lock and holds no hardware: it starts the node (`--spawned`, from the data root as its working directory),
waits on it, and starts it again after a crash. It ends when the node ends cleanly, when the node never started (a
wrong command line, another node on the socket: `NodeExitCodes`), and after a crash LOOP, two crashes within
`NodeKeeper.CrashLoopWindow`, since a driver that crashes the node would crash every node started after it. On Unix
it calls `setsid()` itself and puts its standard streams on `/dev/null` (`NodeDetachment`), so a closed terminal
does not take it down and it can never write over a TUI; a spawned node logs to its file only.

- **`POST /api/v1/node/shutdown`** stops the node the safe way (the host's stop: a run's `Finalise`, the hub's
  cameras warmed), after which it exits 0 and its keeper with it. Only over the socket (a request with no IP
  address; the connection's end-point feature is not one a request can see), 403 over TCP. Every client asks through
  `TianWenNodeClient.ShutdownNodeAsync` (P6 part 1), the launcher included.
- **`HoldsHardware`** on `GET /api/v1/node`: a device connected or a run going on. A node that holds hardware is
  never stopped to replace it.
- **`--fake-devices`** registers the fake device source and no other, for a node that must touch no hardware: a
  test's, or a demonstration's. With `TIANWEN_DATA_ROOT` it touches none of the user's data either
  (`KeptNode`, the functional tests' real keeper and node).
- **`--detach-grace <seconds>`** sets how long an interactive run goes on once no client is present (`NodeRunWatch`,
  a minute by default), passed from the keeper to its node: for a respawn that takes longer, or a test that cannot
  wait a minute per detach (`NodeRunsProcessTests`, P5's proof, runs with 3 s).
- **Logs roll at local midnight**: `Logs/<date>/Server_*.log` and `Keeper_*.log`, one file per process and day, for
  every program (`FileLoggerProvider`).


**A client finds or starts the node through `LocalNodeLauncher`**, never with a spawn of its own:
1. It asks the socket. A node there of this wire is the one (`Found`). One of another wire is stopped over its
   socket and replaced if it is idle, and left running (`BusyWithAnotherWire`) if it holds hardware: an older node
   still running a night after an update.
2. A socket the client was pointed at (`--node-socket`, `TIANWEN_NODE_SOCKET`) is only ever connected to
   (`NamedNodeUnreachable`, `AnotherWire`).
3. With nothing on the socket, a node under another account (a service) answering on `localhost:1888` is used as it
   is (`AnotherAccount`).
4. Otherwise it starts `tianwen-server --keeper` from its OWN directory (`ServerMissing` names the path it looked in)
   and waits for the node to answer. On Windows that is `CreateProcessW` (`DetachedProcess`): broken away from the
   client's job, no window, a process group of its own, no inherited handle. A job that forbids breaking away gets a
   plain start instead (`StartedWithTheClient`, which the client must say). A keeper that ends first is reported
   with its logs (`CouldNotStart`), unless another client's node won the socket, which is then the one.
5. **The node runs on the client's clock**: `StartupTimeOverride.ForAChildProcess()` hands it the client's frozen
   offset as `TIANWEN_CLOCK_OFFSET`, which wins over the `TIANWEN_NOW` it also inherits and would anchor at its own,
   later, start. `GET /api/v1/node` answers `NowUtc`, so a client can see it did.


**"Share this rig on the LAN" is a MACHINE setting the node keeps** (`node-settings.json` in the data root,
`NodeSettings`, decision 3), read at every start and changed over the socket or by a client granted control
(`PUT /api/v1/node/share`, a 401 over TCP without control; see "Who may command the node over TCP"):
- **Where a node listens** (`NodeListeningDecision`): the socket always. TCP 1888 and the LAN announcement unless
  `--local-only`, and for a node a client started only while the rig is shared, so a laptop's GUI opens no port and
  announces no rig nobody asked to share. A node run by hand keeps TCP, as a mini PC's does.
- **While shared, the node starts at logon** (`INodeLogonStart`): a `Run` value on Windows, an XDG autostart entry on
  Linux, a LaunchAgent on macOS, each starting the server's keeper. Turning it off removes it. The entry is written
  only when a client changes the setting, never reconciled at a node's start, and a host registers the real one
  itself (`NodeLogonStart.ForThisUser()`): a host without one answers 501, and a test registers a stand-in, so no test
  can write the user's real entry.
- **The listening follows at the next start.** An idle node a client started restarts at once to apply it (exit
  `NodeExitCodes.Restart`, which its keeper starts again and does not count as a crash); a node holding the rig keeps
  running and says it applies at its next start. `GET /api/v1/node` answers the setting (`ShareOnLan`) beside the
  listening (`IsShared`).


**The node's active profile is node state**, kept in the same settings file (`NodeSettings.ActiveProfileId`), so a
node restarted by its keeper is set up as it was; it used to be in memory only, null after every start. Every way of
setting it (`PUT /session/profile`, a session start with `?profileId=`, the ninaAPI shim) stays behind
`ProfileSwitchGate`. It is also what a discovery on the node pins: `NodePinnedSerialPorts` reads the active profile's
file at each pass (`Profile.TryReadDataAsync`, so an edit by another process is the one probed) and hands its ports
to the serial probe, whose provider is asynchronous for it (`IPinnedSerialPortsProvider.GetPinnedPortsAsync`). The
walk over a profile's URI slots is one, `PinnedSerialPort.In`, for the GUI's provider and the node's.


**A node that dies leaves a crash journal** (`node.journal` beside its socket and lock, `NodeJournal`,
`NodeJournalService`, decision 8): the devices connected through its hub (full URIs, what a successor reconnects by),
the run going on (`NodeRunKind`, profile, start, the target it is on) and each camera's cooler intent. It is written
atomically as any of that changes, and a 5 s poll catches what raises nothing (the target a run moves on to).
- **A cooler's INTENT is the hub's, recorded where the cooler is commanded** (`IDeviceHub.SetCoolerIntent`,
  `CoolerIntent`), because the camera's own state cannot say it: mid-ramp its setpoint is a step, and a warm-up is a
  process no setpoint describes. A session's ramp records its TARGET (`Session.IntentOf`), the hub's warm-up records
  Warm and then Off, and the Alpaca plane and the ninaAPI shim record what their command left the cooler doing
  (`RecordCommandedCoolerAsync`, which never fails the command it follows: a read-back that fails is logged and the
  camera keeps its intent). A disconnect forgets it. **A new place that commands a cooler owes the same.**
- **A node that holds nothing has no journal.** The host's stop, once the run and the cameras have ended within its
  budget, releases every other device too (a mount left for the process's exit was journaled as held, and reported by
  the next node as a crash that never happened), so a clean stop leaves none; one cut short leaves what it got to, a
  camera still warming included.
- **A node that finds one reports it** on `GET /api/v1/node` (`Recovery`, `NodeRecoveryDto`: the interrupted run, when
  the journal was written, the devices and their cooler intents) until a client dismisses it
  (`DELETE /api/v1/node/recovery`, any client, since dismissing changes nothing on the rig). The journal is kept on
  disk until then, or until this node holds something of its own or stops cleanly, so a node that dies holding
  nothing leaves the next one the same report.
- **Which journal is believed**: the one written by the node whose crash its keeper just saw, since the keeper names
  it (`--after-crash <pid>`) and is the witness that no boot came between. Any other is `Stale`, shown and never acted
  on, when it is older than the machine's last boot or its age against the boot cannot be judged. The boot comes from
  the OS (`MachineBoot`): the newest Kernel-Boot event 27 on Windows, the only source that sees a Fast Startup boot
  (the tick count, `LastBootUpTime` and the registry's shutdown time all ran on through one on the desktop that
  measured it), `btime` on Linux, `kern.boottime` on macOS. `WrittenUtc` is the machine's real clock, never the node's,
  which a simulated `TIANWEN_NOW` shifts.
- **A journal it believes, it acts on as it starts, and it never resumes a run.** It reconnects the devices, the mount
  first (which restores mount-limit enforcement), then the cameras, then the rest, each within
  `NodeJournalService.ReconnectBudget`, and reports how each went (`NodeHeldDeviceDto.Reconnected`, `ReconnectError`).
  Each device is named in its own journal BEFORE its connect (`NodeJournal.Touching`): a driver that crashes the node
  crashes it there, and nothing written after the connect would ever reach the file. It then re-establishes each
  camera's cooling from its intent: a cool-down to the target through the session's own ramp, a warm-up from wherever
  the sensor now is, and an Off left off. The run the node before it died in stays in its journal, resumed by nobody,
  until the report is dismissed. **The recovery ends as the host begins to stop** (`ApplicationStopping`), never
  only when the journal does, which stops last, and **a stop the host finished** (`HostedSession.ReleasedTheRig`)
  **leaves no journal** whatever connected after it; a failure inside the recovery is logged, never the end of the
  journal, and a URI in the file that does not parse is reported, since a person may edit it.
- **There is ONE cooling ramp, `CameraCoolingRamp`** (Lib), which `Session.CoolCamerasToSetpointAsync` delegates to
  with its telemetry as a per-step callback, and which the hub drives without a session
  (`DeviceHubCameraSafetyExtensions.CoolToSetpointAsync`). A recovery's ramps end when a run starts (the session cools
  its own cameras, and two ramps on one camera fight) and as the host starts to stop (its stop warms the cameras).
- **The crash-loop guard**: the journal carries the recent crash times (`NodeJournal.Crashes`), each node that finds one
  adding the crash that left it. Two within `NodeKeeper.CrashLoopWindow` is a loop (`NodeRecoveryDto.CrashLoop`): a
  driver that crashes the node would crash every node that reconnected it, so the node reconnects nothing and names
  the device the last one was reaching for (`SuspectDevice`). The keeper's own loop guard leaves the node down after
  the second crash; the journal's is what keeps the next client's node from walking into the same one. A boot wipes
  the history with the rig's state.

Pinned by `NodeSocketTests`, `NodeAddressTests`, `ExeBesideTests`, `NodeKeeperTests`, `NodeKeeperProcessTests`,
`LocalNodeLauncherTests`, `DetachedProcessTests`, `NodeShareTests`, `NodeShareSettingTests`,
`NodeActiveProfileTests`, `NodeJournalTests`, `DeviceHubCoolerIntentTests`, `NodeJournalServiceTests` and
`NodeJournalProcessTests` (a real keeper and node, killed holding a cooled camera, and killed twice for the loop).

## Who may command the node over TCP: seeing is free, a command needs control

P6b of [../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), decision 13, #1021. Over TCP every
read stays open and every command needs control; over the socket nothing changed, and a client of this machine never
needs a grant. **One middleware decides it, `NodeAccessGate`**, installed first in `MapHostingApi`, so the server and
every test's host go through it and a route added later is gated without remembering to be. It reads which surface a
route is on from the metadata its group puts on it (`NodeProtocolMetadata`), and what is a command there:

- **Native**: anything but GET, HEAD and OPTIONS, except the two routes by which control is asked for (`.OpenToAsk()`).
- **Alpaca**: a PUT, `Connected = true` included, since it connects the hardware through the hub.
- **ninaAPI**: every route, since its commands are GETs, unless it says it only reads (`.ReadsOnly()`). Deny by default.

A route mapped outside the three groups would escape the gate, which `NodeAccessTests` fails on. **A new ninaAPI read
owes `.ReadsOnly()`**, and a native route a client without control must be able to use owes `.OpenToAsk()`, which only
asking and polling carry.

**A command passes** when it came over the socket (`NodeEndpoints.CameOverTheSocket`), carries a grant the node holds
(`Authorization: Bearer`), or, on the Alpaca and ninaAPI surfaces only, comes from an address allowed until the node
restarts or from a host name always allowed. **A refusal is in each surface's own form**: the native API answers 401
with `WWW-Authenticate: Bearer`, the Alpaca plane its error envelope with `InvalidOperation` (0x40B) inside a 200, the
ninaAPI shim its envelope with status 403 inside a 200, and the last two say where the rig's owner allows them. A
socket-only route (a profile's writes, a device's setting, decision 4) still refuses a granted client with its 403, so a
client can tell "ask for control" from "do this on the rig's machine": a grant is control over TCP, never the socket.

**A TianWen client asks** (`POST /api/v1/node/control/requests` with a label: 202 and a ticket, an id and a secret; 409
while another request waits, since a person answers one at a time; 400 over the socket) and **polls**
(`POST .../requests/{id}/poll` with the secret, every `NodeWire.ControlRequestPollInterval`), which keeps its request
alive: one not polled for `ControlRequestLapse` is withdrawn (`LanInvites`, LAN.Lib 2.1). A poll without the right
secret learns nothing. **The rig's machine, or a client granted control, answers** (`POST .../requests/{id}/answer`).
After an Allow **the grant is minted at the asker's next poll** and its token handed over on that poll alone, so an
asker gone by then leaves no grant behind. **A grant is remembered until revoked** (`LanGrants`, `node-grants.json` in
the data root, through the atomic writer): only the token's SHA-256 is kept, so a copy of the file grants nothing, and
a revoked token is refused from the next request on.

**Another application cannot ask, so its refused command is its request**: one record per address (`RefusedAppDto`:
its user agent, an Alpaca `ClientID`, what it tried, when, and how often), a retry updating it, and its
forward-confirmed host name (`LanHostNames`) looked up once in the background, so a refusal never waits on a resolver.
From it, **Allow** lets the address command until the node restarts; **Always allow** keeps the host name
(`node-settings.json`, `AlwaysAllowedHosts`), and is a 409 for an address with no name that resolves back to it;
**Ignore** drops the record, and the next refusal records it again. The node does not answer Alpaca's UDP discovery,
so an app finds a rig by its address.

**Managing it is for the socket or a grant**: `GET /api/v1/node/access` (everything the Sharing panel shows, a 401 to
anyone else although it is a GET), revoking a grant, allowing or revoking an app or a host, and `PUT /api/v1/node/share`,
which only the socket could set before. `ACCESS-CHANGED` is pushed on every change as a hint with no content, so a
watcher learns nothing from it, and the read is authoritative. `GET /api/v1/node` answers whether the caller may
command (`CallerMayCommand`). **A terminal does the same over the socket** (`tianwen node requests`, `grants`, `allow
[<address> [--always]]`, `decline`, `revoke <grant id, label, address or host>`, `ignore <address>`, `share on|off`), which
is how a headless rig's owner answers a laptop over SSH. **Loopback TCP is the LAN too**, deliberately: a web page in a
browser on the rig's machine can reach `localhost:1888`, and a bodiless POST (`/session/abort`) needs no preflight. So a
client that reaches a node under another account (a service) on loopback (`LocalNodeLauncher`'s `AnotherAccount`) asks
for control like a laptop, and is answered by `tianwen node allow` run as that account, over its socket.

**Who counts on the event socket**: whether a client may command is asked at every count (the socket, or the grant its
upgrade carried while the node still holds it: `EventHub.AddClient`'s `mayCommand`), so a grant revoked while its
socket is open stops counting at once. A prompt waits only for a client present AND able to command
(`AnsweringClientCount`), and the quit question's "last client" counts only clients able to command (`ClientsAttached`,
`CommandingClientCount`): a watcher could answer neither. An interactive run's detach grace (`NodeRunWatch`) counts every
client present, since a run someone watches is watched.

**What it does not stop: plain HTTP.** A token can be read off the wire and an address or a host name spoofed. It
closes "anyone who can reach the port", not a hostile LAN, which is TLS's job. The wire version moved to 3.

**The clients** (P6b part 3). A connection over TCP keeps its token in the credential store by the node's id
(`NodeGrants`) and presents it from its first request; it reads whether it may command (`CallerMayCommand`) and, when it
may, the Sharing panel's read, at first contact, on `ACCESS-CHANGED` and every 30 s, and forgets a token the node no
longer holds. It asks with `AskForControlAsync`, which polls the request alive and, granted, keeps the token and reopens
the event socket with it (`TianWenEventStream.Reconnect`). A view's runs and device actions go to the node on show
through `CommandTargetOrSay`: this computer's, or a rig's once granted, never a rig only watched. A request to this
computer's rig is drawn over its Live Session view, and the Home board shows who may command each rig, with the Sharing
panel of the rig on show under it.

Pinned by `NodeAccessTests` (the gate on each surface, asking and answering, a grant across a restart and revoked, the
Alpaca and ninaAPI refusals, Allow and Always allow, the counts), `NodeShareTests` and `EventHubTests`.

## Six invariants on the session plane

1. **A pushed schedule beats the target queue.** `POST /session/schedule` takes
   `ScheduledObservationDto[]` and preserves per-filter plans, the planner's altitude-optimised
   `Start`, and `AcrossMeridian`; `PendingTarget` carries none of those and `/session/start` stamps
   `Start = now` on whatever it drains. `/session/start` drains the schedule first and only falls
   back to the queue, so never route a real schedule through `/targets`.
2. **Subscribing to `PromptRequested` takes over the session's unattended answer.** A session answers
   a prompt itself only while *nothing* is subscribed, which is what keeps unattended runs from
   blocking on a step nobody will perform. `EventBroadcaster` is a subscriber, so it restores the
   guarantee: **no native WebSocket client attached -> answer immediately with
   `SessionPromptEventArgs.DefaultIfUnanswerable`** (the session's own policy, carried on the prompt
   so it cannot drift); **one attached -> hold indefinitely**, with no timer, because guessing after
   an arbitrary interval fabricates a decision rather than fixing an unresponsive client. Only a
   native client counts (`EventHub.PromptObserverCount`): a ninaAPI v2 socket has no prompt route, so it
   is nobody to wait for, and it used to hold every prompt indefinitely. The only bound is liveness --
   if the last observer goes while a prompt is outstanding the poll loop resolves it. Any new
   subscriber on a headless path owes the same.
   **Liveness is a client's presence BEAT, not its socket** (P1, #917). A window frozen by a GPU wedge
   keeps its socket open, so "a socket is registered" held a prompt, and the night, for ever. A client
   calls `TianWenEventStream.Beat()` from the loop that DRAWS it (the GUI from `SdlEventLoop.OnLoopIteration`,
   which fires every iteration whether or not a frame was drawn, so an idle window keeps beating and a
   stuck one stops; never `OnPostFrame`, which an idle window never reaches; the TUI from its main loop),
   and the stream sends one `BEAT` text frame (`NodeWire.PresenceBeat`) per second while it has been
   called since the last. The node counts a native client as an observer only while its last beat is under
   `NodeWire.PresenceLapse` (5 s) old, so an attached client that has not beaten yet is nobody to wait for,
   and one that stops is let go within a few seconds with its socket still open. **Never beat from a timer
   or the socket's own thread**: that proves the process lives, not that its window can show anything.
   Pinned by `NodePresenceTests` (a real node and stream, the loop stopped under an open socket).
   **A prompt is offered until it settles** (`SessionPromptEventArgs.Settled`): answered, or withdrawn
   by whoever raised it (the session withdraws one when its run is cancelled while it waits; a mirror
   when its node stops offering it). Every holder drops it then, `/session/state`, a mirror and the
   GUI's prompt bar alike, rather than go on asking a question the run has moved past (P0b item 13).
3. **The JSON contract uses numeric enums.** No `JsonStringEnumConverter` is configured on
   `HostingJsonContext`, so every enum crosses as its ordinal. A request DTO with a `required` enum
   is therefore hostile to hand-written callers -- default it (as `ScheduledObservationDto.Priority`
   does) rather than forcing a caller to guess the number.
4. **A run is the NODE's, never a request's** (P0b of
   [../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), #752). Every start
   (`/session/start`, `/session/flats`, the shim's `sequence/start`) goes through
   `IHostedSession.TryStartAsync`, which runs it on a token the node owns; `TryAbort` is the only thing
   that cancels it, and the host stopping calls that. **Never pass a request's token to a run**:
   Kestrel reuses a connection's cancellation source for its next request, so a later request on the
   same connection that its client abandoned (what a GUI restarting does) cancelled the night, and a
   token already cancelled made `Task.Run` skip the run while its session stayed published. The rest:
   - **The run record is replaced whole, by compare-and-swap**, so two starts cannot both win, and a
     run that has ENDED stays readable (`/state` shows how it ended) but never blocks the next start,
     which disposes it before the new run touches the rig.
   - **An abort cancels and lets the run end through its own `Finalise`**; the session is never
     disposed underneath it (the old abort disconnected the drivers at once while the run carried on).
   - **The host starts and stops `HostedSession`** (it was never registered as a hosted service, so
     discovery never ran and a SIGTERM abandoned the rig). Discovery starts in the BACKGROUND, so the
     server listens at once, and a start awaits it on the request's token, before draining anything.
     Stopping aborts the run, awaits its `Finalise`, then warms and disconnects the hub's cameras
     (`IDeviceHub.StopConnectedCamerasAsync`, the same tail the GUI's quit runs), inside
     `HostedSession.ShutdownBudget` (30 min, set as `HostOptions.ShutdownTimeout`; the default 30 s cut
     every warm-up off). **A service manager's own stop timeout must allow as long**: systemd
     `TimeoutStopSec=35min`, or it kills the process mid-ramp. **A request that lasts ends as the host starts
     stopping** (`ApplicationStopping`), since the host waits for open requests out of that same budget, and the
     server stops before the node's own services (`WebApplicationBuilder` registers it last): the event socket
     read only until its client closed, so a stop with the GUI attached waited all 30 minutes and left the
     `Finalise` and the warm-up none of the budget (#985, measured). A stopping node aborts the socket, as its
     death would, and the client reconnects with its backoff.
   - **A run is of any KIND, not only a session** (P5 part 1 of
     [../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), #934). A run that is not a session
     is an `INodeRun` (its `Kind`, a body, what it holds), started through the same `TryStartAsync` on the same
     token and record, one run at a time with a session, and journaled by kind. `CurrentSession` is the latest
     run's session or null; `CurrentRun` the latest other run; `GET /api/v1/node`'s `Run` the one going on,
     whatever it is. **A refused start names the run going on** (`NodeRuns.AlreadyGoingOn`), never "a session"
     for a dark library, and **each kind is stopped through its own route**: `/session/abort` stops a session or
     a flat run only. **A run releases its lease as its BODY ends**, not when the next run disposes it, or an
     ended run would go on refusing every command to its devices.
   - **The dark library is the first** (`POST`, `GET` and `DELETE /api/v1/darks`, `NodeDarkLibrary`): the CLI's
     `darks` capture (`DarkFrameRun`) on a camera connected to the node, refused in the device plane's order (a
     run going on, the lease, the camera, a job working on it), the camera leased for the run.
   - **Polar alignment is the second** (`POST`, `GET` and `DELETE /api/v1/polar`, `NodePolarAlignment`, P5 part 4):
     the GUI's own routine, `PolarAlignmentRun` in Lib, which resolves the devices from the active profile and
     CLAIMS the mount and the capture devices, refusing in words, and a start is refused while a job works on any
     device it claimed (#981: a job holds its device in `NodeJobs`, not through the lease, so the claim alone let a
     start rotate a mount a slew job was still driving). Its probe and refinement frames become the OTA's
     (`NodeFrames`), and `GET` carries Phase A, the latest refine tick with its overlay, and the latest WCS with
     the token of the frame it is of, NaN crossing as null. **Cancellation is how it ends**: `DELETE` is Done and
     Cancel alike and is answered AT ONCE, since the restore reverses the Phase A rotation for longer than a
     request may take; `GET` says when it has ended, the mount restored and the devices given back.
   - **A live planetary capture is the third** (`POST`, `GET` and `DELETE /api/v1/planetary`, `PUT
     /api/v1/planetary/controls`, `NodePlanetary`, P5 part 5b): the GUI's own capture loop, `PlanetaryCapture` in
     Lib, through the OTA's camera, and the rolling stack the GUI draws from, run on the node instead (decision 5:
     only the master and a live frame cross). **The start comes in two halves**: `TryPrepare` resolves the camera,
     claims it and sets its window as the request is answered, refusing in words, and `StartPrepared` starts the
     loop on the node's token once the run is the node's, so it never streams on the request's token nor before
     the node has taken it. The node stacks on the run's own task (`RollingWindowStacker`, a master every 250 ms at
     most) and shows two frames of the run's own, `planetary/live` (the camera's frame, copied at about 30 Hz at most, a display's rate,
     into recycled planes by `FrameSampler`) and `planetary/master` (linear), through `/frames` like any other and
     announced by `FRAME-AVAILABLE`; `NodeFrames` keeps them until the node's next run starts. A control is STAGED
     and taken after the next frame, and a new window size is snapped to the camera's rule in the capture loop,
     for every host. The stop is answered once the capture has ended, a frame or two. **Both frames also STREAM**
     (P5 part 5c, `FrameStreamWire`): a WebSocket per source at `/api/v1/frames/planetary/{live,master}/stream`,
     on which the client ASKS for each frame (the text `next`) and the node answers with one binary message, its
     number and then the frame as `/frames` sends it: the newest, or the first newer than the last one sent.
     **The client asks because a send is "done" once the kernel has the bytes**: a loopback socket buffers
     megabytes, dozens of planetary frames, so the first version, which sent whenever its last send had gone, fed
     a reader that paused for 2 s every frame of the pause, the first of them 100 ms after the frame before it
     (measured, with the live frame then at 10 Hz). The node wakes on `NodeFrames.NextPublish`, taken BEFORE the source is read. A stream ends
     when its client closes it and as the host starts stopping, with a close the client has 2 s to answer
     (#985's rule). The live frame carries when it arrived as its start time, since no driver dates a video
     frame. The client is `NodeTransport.OpenFrameStreamAsync`, over the socket or TCP alike. **A recording to
     disk** (P5 part 5d, `POST` and `DELETE /api/v1/planetary/record`, `SerRecording` in Lib) writes every frame
     for its duration into a SER file under `Planetary/<date>/` beside the snapshots, in the camera's own shape (a
     mono plane or a Bayer mosaic, 16 bits) with each frame's arrival time in the trailer. **It finishes its
     duration whether or not anyone watches**: the run is not interactive while it records (`EndsUnwatched`), and
     the live view left after it gets a whole grace of its own. The capture loop only converts a frame into a
     pooled buffer and queues it, and a writer task of the recording's own does the disk, so a slow disk costs
     recorded frames, counted, never the capture's rate. A frame of another size (a window resized) ends it, as
     does the capture ending; the file is whole once its writer has closed it (`Written` in the state).
   - **A live view is the fourth** (`POST`, `GET` and `DELETE /api/v1/live`, `PUT /api/v1/live/controls`, `NodeLiveView`,
     #1111): the Preview mode's [Live], the same capture loop as `LiveCaptureKind.LiveView`, which keeps no frames (no
     stack, no recenter, no recording), over the whole sensor at the Preview's binning. It claims only the camera, so the
     focuser and the mount stay free while it runs; its frame is `live` (`FrameSources.LiveView`), sampled and streamed
     as `planetary/live` is, and it ends unwatched as the planetary run does. A still from the camera waits for it to
     end: the GUI stops it first, since a Canon's Live View and its shutter exclude each other.
   - **An interactive run stops once nobody watches it** (`INodeRun.EndsUnwatched`, `NodeRunWatch`): polar
     alignment and a planetary live view are meaningless unseen, so once no client has been PRESENT
     (a fresh presence beat, `EventHub.PresentClientCount`, the same rule a prompt waits by) for the detach grace
     (60 s, `NodeRunWatchOptions`), the watch stops it through its own ending and says so in the node's
     notifications. The grace runs from the moment nobody is present, the run's start included; a client back
     within it cancels it. A session, a flat run and a dark library go on regardless.
   - **A stop names the run it means** (`IHostedSession.TryAbort(INodeRun)`): the watch and every kind's own stop
     route look at the run and then stop it, and a session that replaced it in between must not be the one
     stopped. Only `/session/abort` and the ninaAPI stop keep the untargeted abort.

   Pinned by `NodeRunLifecycleTests`, all nine seen failing against the old code, `NodeDarkLibraryTests`,
   `NodePolarAlignmentTests`, `NodePlanetaryTests` and `NodeRunWatchTests`.
5. **A start runs on the DECLARED defaults plus what the request sets, and the whole configuration
   crosses the wire** (P0b item 10, #752). `new SessionConfiguration()` is the declared defaults (an
   explicit parameterless constructor); without it, it was the struct's zero-initialiser, which zeroes
   every field, the declared defaults included, so every API session synced the mount's site to 0, 0,
   autofocused 0 steps over a 0 range and never warmed its cameras. `default(SessionConfiguration)` is
   still zeros; never use it. `SessionConfigApiDto` carries every field, each optional, and
   `SessionConfigApiDtoTests` round-trips one with every field away from its default, so **a field added
   to the configuration is added to the DTO and to that test**. The body is read whatever its framing
   (the client's own `JsonContent` is chunked, with no Content-Length, and used to be ignored), and a
   malformed body is a 400, never a run on defaults. **A run's site is settled once its mount connects**
   (`Session.Site`, #798): the site the request names, else the mount's own reconciled with the profile's
   under `SiteTieBreaker` (`MountSiteExtensions`, the rule the GUI applies on connect, now in Lib for
   every host). So a mount with no site takes the profile's, where the factory used to settle only the
   profile-wins case, into the configuration, and never saw a mount that had none. Every reader of a
   run's site asks `Session.Site`: the limit poll computed its altitude from the configured site alone,
   so a run whose request named no site had its horizon limit off all night.
6. **A run's drivers are the hub's** (P0b item 11, #752). A session connects each device THROUGH the
   hub (`ControllableDeviceBase.ConnectAsync(hub)`, which calls `IDeviceHub.AdoptAsync`): the hub takes
   the driver the wrapper built, with whatever its caller configured on it, or, when it already holds
   that device connected, the wrapper switches to the hub's instance. It used to connect a driver of its
   own unless the hub held one, so on a server, where nothing pre-connects, the run and the hub were two
   driver worlds: `/devices` called the run's devices disconnected, the Alpaca plane could not see them,
   its `Connected=true` opened a second driver on a device the run was driving, and the shutdown warm-up
   missed the run's cameras. So:
   - **A node holds one driver per device**, whoever connected it, and every later phase of the plan
     assumes it. It holds under two connects of one device at once and after a driver drops (#806): the
     hub serialises connect, adoption and disconnect per device, and reconnects a down driver in place
     while its URI is unchanged.
   - **A run no longer disconnects what it adopted when it is disposed.** `Finalise` still stops,
     parks and disconnects the mount and the guider through their drivers. The cameras, focusers,
     wheels and covers stay connected in the hub, which is where the shutdown warm-up finds them.
   - **`IDeviceHub.ConnectedDevices` lists only a driver that is up.** Every run now leaves the mount and
     the guider as entries whose driver is down, and a listing that kept them had the profile-switch
     gate name a parked mount as connected and the limit watcher evaluate it every tick.
   - **Read `Driver` after a connect, never before**: the connect can switch it (the flat run's cover
     and the built-in guider's pier-side oracle both had to move).

   Pinned by `DeviceHubAdoptionTests` and by
   `SessionLifecycleTests.GivenNothingPreConnectedWhenInitialisationThenTheHubHoldsTheSessionsOwnDrivers`.

## Slow operations are JOBS

A request that starts something slow answers **202 with a `JobDto`** at once, and the node finishes it on
its OWN token (`NodeJobs`), as a run is the node's (invariant 4): a client's short budget running out, or
its connection closing, never cancels a serial probe half-way.

- `GET /api/v1/jobs/{id}` is authoritative. It answers 404 once the job is forgotten; the node keeps the
  last 32 that ended.
- `GET /api/v1/jobs` lists the running and recently ended ones, newest first, for a client that reconnects.
- `DELETE /api/v1/jobs/{id}` cancels; the job ends `Cancelled` once its work notices.
- A `JOB-PROGRESS` push (`Id`, `Kind`, `State`, `Step`, `Error`) is the latency hint.
- **One of a kind runs at a time, and a second start JOINS it**: a discovery is one sweep of the ports, and
  a second would fight the first for them.
- **A job on a device holds the DEVICE, not its kind** (`NodeJobs.TryStartOrJoin`, keyed on the URI's
  `DeviceKey`): another start of the same kind joins it, one of another kind is refused with a 409 naming the
  job (a disconnect half-way through a connect would race it for the driver), and two devices run their jobs
  side by side. `JobDto.DeviceUri` names the device, so a client that reconnects can put a job back on its row.
- **A job carries no result.** What it produced is read where it lives (a discovery's devices from
  `/devices/structured`), so a job kind never needs a payload type of its own on the wire.

Discovery is the first: `POST /api/v1/devices/discover`. It was a `GET` that ran the whole discovery
inline on the REQUEST's token and answered display strings, so a client's 10 s control budget cut a serial
sweep off mid-probe (P0b item 17 of [../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md),
#752, fixed by #916). Connect, disconnect and warm-and-disconnect followed in P2 (below); a preview exposure, solve
and sync, and a move follow them; **a new slow endpoint starts a job, never runs inline**. `TianWenNodeClient`
has `StartDiscoveryAsync`, `GetJobAsync`, `GetJobsAsync` and `CancelJobAsync`. Pinned by `NodeJobTests`.

**What discovery found is `GET /api/v1/devices/structured`** (`DeviceDto`, `TianWenNodeClient.GetDevicesAsync`): each
device's URI, name, type and whether the node holds it connected, and for a camera what it IS (P6 part 1): its named
gains (`GainModes`, a DSLR's ISO steps; null for a camera that takes a gain value) and whether it has a cooler
(`CanCool`). A session's camera settings are offered by those two, and a client knows them with nothing connected, as the
GUI knew them from its own device registry. The fake devices are listed like any other (every node registers the fake
source); showing them is the client's choice (the GUI's Shift+Discover). Pinned by `DeviceListingCapabilityTests`.

## The device plane's read side: one reader, and a held device is the run's

What a client shows of a device with no session running (P2 part 1 of
[../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), #929). `DeviceStatePoller`
(`BackgroundService`) is the node's one reader of every connected device; the rest of P2 adds the commands.

- **`GET /api/v1/devices/state` is authoritative**: every device the hub has connected or leased, as a
  `DeviceStateDto` each. Its connection and its lease owner are read live on the request; its reading is the
  poller's last. A camera carries the hub's cooler intent beside its reading, and a mount the node's
  safety-limit verdict (`MountLimitWatcher.VerdictFor`, which the GUI reads every frame today).
  `TianWenNodeClient.GetDeviceStatesAsync`.
- **`DEVICE-STATE` (`Data["Device"]`, one `DeviceStateDto`) is the latency hint, pushed only on a change.**
  The comparison leaves out when the device was read, and compares each sensor reading at the resolution a
  reader is shown it (0.1 °C, a whole percent of cooler power): a thermometer's last digits differ on every
  read, and compared in full every device carrying one was pushed on every read. A device that goes is pushed
  once with `Connected = false`, then dropped. `DeviceStateDto.TryFromEvent` reads the push back.
- **The node and the GUI read a device through the same code**, `DeviceHubReadingExtensions`
  (`ReadCameraAsync`, `ReadFocuserAsync`, `ReadFilterWheelAsync`, `ReadMountAsync`, `ReadCoverAsync`), so they
  cannot read one two ways (no host reads a cover yet; the node does, for P6). A topocentric mount's J2000
  position uses the active profile's site, read at most every 30 s; a J2000 mount's is its own and asks for
  no transform.
- **A leased device is never read.** The run holding it reads it itself, and two readers on one serial
  port race. The node keeps the last reading and names the run (`LeaseOwner`), which is also the lease table
  P6 re-sources the GUI's ownership gate from.
- **Cadence is the GUI's, while anyone is watching** (a socket client attached, or a snapshot asked for in
  the last 10 s): a camera and a filter wheel every 2 s; a focuser, and a cover's flap, every second while it
  moves, else 2 s; a mount every 0.5 s slewing, every second for 10 s after it settles tracking and then every 10 s, 2 s
  otherwise. With nobody watching, every device every 10 s.
- **A reading a device does not give is null, never 0** (a cooler at 0 °C and RA 0 are real readings).
  The session state, the guider's, the profile's site and the planetary state do the same since P5b part 2
  (`JsonNumber.OrNull`, wire 2). The ninaAPI shim, the Alpaca plane and the broadcast events, which go to ninaAPI
  sockets too, keep `JsonNumber.ForWire`'s 0, N.I.N.A.'s own convention.
- The five DTOs are records, unlike the rest, so the node's copies of a reading are `with` expressions,
  which cannot forget a field (a field a hand-listed comparison left out is a change never pushed).

Pinned by `DeviceStateTests` (a real node: every device read and served with its lease and limit; a held
device left alone and re-read once let go; a change pushed, an idle one not, and a goodbye),
`DeviceStatePollerTests` (the cadences and the push's wire form) and `DeviceHubReadingTests`.

### Connect and disconnect, as the node's jobs

P2 part 2 (#929). `DeviceOperations` asks the Equipment tab's questions in the Equipment tab's order, so a client
that switches over at the cut (P6) meets the same answers; each route takes the WHOLE device URI in its body (its
settings ride on the query, and a URI's left part cannot be a path segment).

- **`POST /api/v1/devices/connect`** (`DeviceRequestDto`): the node builds the device from its URI through the source
  its host names (`IDeviceHub.TryGetDeviceFromUri`; 404 when none does) and connects it as a job.
- **`GET /api/v1/devices/disconnect-safety?deviceUri=`** (`DisconnectCheckDto`): the read BEFORE a disconnect is
  offered, a camera's cooler and whether it is at work (`GetDisconnectSafetyAsync`) and the run holding the device.
- **`POST /api/v1/devices/disconnect`** (`DisconnectRequestDto`): refused with a 409 naming the run while one holds
  the device, FIRST; then, unless `SkipWarmUp`, refused while a camera is cold or at work, saying so.
- **`POST /api/v1/devices/warm-and-disconnect`**: the hub's ramp (`WarmAndDisconnectAsync`) as a job, so a warm-up the
  window started finishes whatever becomes of the window. Cancelling it (`DELETE /jobs/{id}`) stops the ramp where it
  is and disconnects nothing.
- **`SkipWarmUp` is consent to a COLD disconnect, never to ending a night**: no request gets past a lease, exactly as
  the GUI's Force Off does not. Stopping the run is the way past it.

Connecting the active profile's mount reconciles its site, and connecting its camera records the sensor, as the GUI's
connect does: both are profile writes, made through the node's one writer (P3 part 2, below). `TianWenNodeClient` has `ConnectDeviceAsync`,
`GetDisconnectSafetyAsync`, `DisconnectDeviceAsync` and `WarmAndDisconnectDeviceAsync`. Pinned by
`DeviceOperationTests` (a real node) and `NodeJobsPerDeviceTests`.

### A camera's cooling and settings

P2 part 3 (#929), on the same `DeviceOperations`, every route refusing a camera a run holds (the `Actuate` gate) and
a device that is not a camera:

- **`POST /api/v1/devices/camera/cool`** (`CoolRequestDto`): a JOB through the session's own ramp
  (`CameraCoolingRamp`, via `IDeviceHub.CoolToSetpointAsync`), never a jump, on the session's default ramp time
  unless the request names one. It records the whole-degree TARGET as the camera's cooler intent, which the crash
  journal keeps. The GUI's setpoint today is an immediate command that records nothing; P6 moves it here.
- **`POST /api/v1/devices/camera/warm`**: the hub's warm-up ramp and cooler off as a job, the camera left connected.
- **`POST /api/v1/devices/camera/cooler-off`**: at once, no warm-up (`IDeviceHub.CoolerOffAsync`, which the GUI's
  cooler off now goes through too), recording the intent as off so a node after a crash does not re-cool it.
- **`POST /api/v1/devices/camera/settings`** (`CameraSettingsRequestDto`): gain, offset, binning and frame, each
  only when named, answered with what the camera reads back (`CameraSettingsDto`). **Every value is checked before
  any is applied**, so a bad one changes nothing. The frame is set through `CameraFrameExtensions.SetFrame`: binning
  FIRST (the frame's setters check against the binned sensor), then the frame snapped to the camera's
  `RoiConstraints` and kept on the BINNED sensor, which a driver's constraints do not always shrink for binning
  (the fake's and ZWO's name the unbinned sensor at any binning).
- **A command that is not a job is refused (409) while a job holds the camera**: a cooler switched off under a
  running cool-down, or a camera rebinned under a job, fights the job for it. `NodeJobs.TryGetRunningOn` answers.

`TianWenNodeClient` has `CoolCameraAsync`, `WarmCameraAsync`, `CameraCoolerOffAsync` and `SetCameraSettingsAsync`.
Pinned by `CameraOperationTests` (a real node), `CameraFrameTests` and `DeviceHubCoolerIntentTests`.

### Moving a focuser, a filter wheel or a mount

P2 part 4 (#929). A move is a JOB that ends when the device has SETTLED (the focuser has stopped, the wheel reads
the position, the mount has landed or reports itself parked), and cancelling it halts the device on its way out.

- **Focuser**: `POST /api/v1/devices/focuser/move` (`FocuserMoveRequestDto`: a `Position`, or `Steps` from where it
  is, one of the two, inside `0..MaxStep`) and `/focuser/stop`.
- **Filter wheel**: `POST /api/v1/devices/filterwheel/change` (`FilterChangeRequestDto`, a position counted from 0).
- **Mount**: `POST /api/v1/devices/mount/goto` (`MountGotoRequestDto`, J2000), `/park`, `/unpark`, `/tracking`
  (immediate), `/stop`, and `/nudge` (P6 part 1: `MountNudgeRequestDto`, a direction and arcseconds above 0), one
  guide-rate pulse through `MountNudge` as a job that ends when the mount reports the pulse DONE, since the pulse runs on
  after its start returns. A mount with no guide rate on that axis fails the job saying so: `MountNudge` returns the pulse
  it issued, or null, so no caller reports a nudge that did not happen.
- **The goto is the GUI's own**, `MountGoto` in Lib (lifted out of the GUI's `MountActions`, whose solve and sync
  and nudge followed it, as `MountSolveSync` and `MountNudge`): unpark, the J2000 to mount transform at the ACTIVE PROFILE's site, the horizon limit, the
  destination pier side, the tracking rate. With no active profile there is no site, and no goto (409). What only
  the mount's geometry can answer (below the horizon, a pier side it cannot reach) FAILS the job with its reason,
  where a lease, a busy device or a bad request is refused before any job.
- **A second move while one runs is refused (409), not joined**: a move to another position is not the same move,
  which `NodeJobs`' join-the-same-kind rule would otherwise make it.
- **A stop ends the job it stops**, so it is the one command a running job does not refuse; a lease still does.

**The session-scoped routes from before the device plane are re-pointed at it**: `/api/v1/mount/slew|park|unpark|
tracking` and `/api/v1/ota/{index}/focuser/move|stop` and `/filterwheel/change` resolve the device they mean (the
running session's, else the active profile's) and ask `DeviceOperations`, so they work with no session and follow
the same rules, the lease FIRST. **What changed for a caller**: `/mount/slew`'s coordinates are J2000 through the
goto (it passed them to the mount as they were), and a slew, a park, an unpark, a move and a filter change answer
202 with their job rather than 200 with a string. The ninaAPI shim's equipment routes are unchanged.

**Ownership is asked before whether the device is even connected**, on every device-plane route
(`TryConnectedAndFree`): a device a run holds is refused with the run's name whatever state its driver is in, which
is `ActuationGate`'s rule and what `NodeActuationGateTests` pins over the re-pointed routes. `TianWenNodeClient` has
`MoveFocuserAsync`, `StopFocuserAsync`, `ChangeFilterAsync`, `GotoAsync`, `ParkMountAsync`, `UnparkMountAsync`,
`SetMountTrackingAsync`, `NudgeMountAsync` and `StopMountAsync`. Pinned by `MotionOperationTests` (a real node) and
`NodeActuationGateTests`.

### The leased move-axis

P2 part 5 (#929). **`POST /api/v1/devices/mount/move-axis`** (`MoveAxisRequestDto`: an axis and a signed rate in
degrees a second, inside one of the axis's `AxisRates`; 0 stops it) moves the axis, and the motion is LEASED: it
stops by itself `NodeWire.MoveAxisLease` (2 s) after the last request that asked for it. A move-axis used to run
until something stopped it, so a client that died, or lost its connection, mid-move left the axis running.

- **A client holding a move repeats the request while it holds**, every half second: the same request RENEWS the
  motion, and one naming a new rate for an axis changes it. The answer is the motion's job, the same one while it
  lives (`TianWenNodeClient.MoveAxisAsync`).
- **The motion is ONE job on the mount** (`move-axis`) holding both axes, so a goto or a park is refused while it
  runs. It ends when the lease lapses, when both axes are asked to stop, or when it is cancelled (`/mount/stop`,
  `DELETE /jobs/{id}`), and it stops both axes on the node's own token whichever ended it: a stop a cancellation
  could skip is how an axis keeps running.
- A renewal racing the lapse can land just as the job stops; the next renewal starts a new job, a hiccup rather
  than a runaway. The ninaAPI shim's own move-axis is unchanged, and unleased, for its clients.
- The fake mount moves both axes now (it refused `MoveAxis`), reporting itself slewing while either moves; an abort
  stops axis motion, as ASCOM's does.

Pinned by `MotionOperationTests`: a motion nobody renews stops by itself, a renewed one outlives its lease and stops
when asked, a stop ends it, and a held mount refuses it.

### The proof: the Equipment tab's flows over the socket

P2 part 6 (#929). `EquipmentFlowProcessTests` spawns a real node (`KeptNode`: `--fake-devices --local-only`, a temp
socket and data root) and drives the Equipment tab's flows through `TianWenNodeClient` over the SOCKET, in the order
the tab runs them: discovery; connecting each device, a job each; every device read, listed and pushed; a cool-down
and the cooler off, each recorded as the cooler intent; a settings change; a focuser move and a filter change; a
goto, a park and an unpark, tracking, and a held move-axis let go; the disconnect check, a warm-and-disconnect and
the disconnects, each device's goodbye pushed. What passes there is what the GUI can do once it has no hub of its
own (P6), and every call it makes is a `TianWenNodeClient` method the cut can switch to.

It found one bug the in-process tests could not: a filter wheel's reading carried the installed filter's
`Position`, which is its FOCUS OFFSET (the fake's Red is +20), as the wheel's slot. `ReadFilterWheelAsync` reads the
slot from `GetPositionAsync` now (-1 while the wheel turns), and `DeviceHubReadingTests` turns a wheel to a slot
whose offset is not its number, where the old test compared the reading with the same wrong field.

## Profiles: the node is the one writer

P3 part 1 (#930). Every profile the node writes goes through ONE writer, `NodeProfiles`, whoever asked for it: an edit
over the socket, and the node's own writes (reconcile, sensor capture, the backlash mirror, which follow in later parts).
Each write reads the profile's FILE as it is now, inside the writer's lock, never the registry: until P6 the GUI, the
TUI and the CLI still save profiles themselves, so the node's registry can be behind the disk.

- **`GET /api/v1/profiles/{id}` carries the WHOLE profile** (`ProfileDetailDto.Data`, the `ProfileData` the file holds)
  and its REVISION. `Equipment` and the site fields stay for the clients that read them; `Data` is what they leave
  out: the guider focuser, the OAG OTA, mount limits, the site tie-breaker, focus direction and the sensor geometry.
  A client on the LAN may read it (decision 4).
- **`PUT /api/v1/profiles/{id}` takes the whole profile back** (`UpdateProfileRequest`: `Data`, an optional new
  `Name`, and the `Revision` it was read at) and answers the profile as stored. A revision is the hash of the stored
  file's bytes (`StoredProfile`, `Profile.ComputeRevision`), so it keeps no state, holds across a restart, and moves
  when ANY process saves the profile. An edit whose revision the file has moved past is refused with a **412**: the
  client reads the profile again and reapplies its edit, rather than overwriting a change it never saw. An edit that
  would store what is stored already writes and pushes nothing. A profile with no mount, guider or telescope list, or
  a telescope with no name or camera, is a 400 (JSON leaves a missing field null rather than refusing it).
- **Changing a profile is for the socket only** (decision 4): `PUT`, `POST /api/v1/profiles` and `DELETE` answer 403
  over TCP. Create and delete were open to the LAN before P3.
- **`PROFILE-CHANGED` is pushed for every write** (`NodeWire.ProfileChangedEvent`, `ProfileChangedDto`: the id, the
  name and revision it has now, or `Deleted`), after the registry is re-read so `GET /api/v1/profiles` lists it. A
  hint: `GET /api/v1/profiles/{id}` is authoritative.

`TianWenNodeClient` has `GetProfileAsync`, `UpdateProfileAsync`, `CreateProfileAsync` and `DeleteProfileAsync`. Pinned
by `NodeProfileWriterTests` (a profile with every field set crosses and is stored exactly as sent, a stale revision is
refused, every write is pushed and listed, a malformed profile is refused, and a LAN client reads but cannot write) and
`ProfileRevisionTests`.

### What the node writes into a profile on its own

P3 part 2 (#930). The GUI's Equipment tab writes into a profile without being asked, at four moments, and the node now
does the same at the same moments, through `NodeProfiles`, so each write is pushed. The RULES moved from the GUI's
`EquipmentActions` into Lib and both apply the one copy until P6: `DeviceDiscoveryExtensions.ReconcileStoredProfile`,
`ProfileDataExtensions` (`MigrateSiteFromMountUri`, `CaptureSensorSpecs`, `WithSite`, `DiffTo`) and
`MountSiteExtensions` (`ReconcileSiteWithProfileAsync`, `PushSiteToMountIfProfileWinsAsync`).

- **The discovery job ends by reconciling every stored profile** (`NodeProfiles.ReconcileAllAsync`): a device URI that
  drifted (COM5 to COM6, a new DHCP address) is rewritten, and a site kept on the mount's URI (the legacy place) moves
  into the profile. A profile in sync is not written. Each change is logged field by field, and the job's last step says
  how many profiles it wrote.
- **Connecting the active profile's mount reconciles its site** (`SiteTieBreaker`): the mount-side half is applied at
  once, the profile-side half written, the SITE alone onto the profile as it is at the write.
- **Connecting a camera of the active profile records its sensor** (pixel size and dimensions, into its OTA, for smart
  framing), once: a sensor already recorded is not written again.
- **An edit that changes the active profile's site gives it to the connected mount when the profile wins**, as the GUI's
  site edit does, and never to a mount a run holds (`DeviceOwnershipGate`), since a site write commands the hardware and
  the run keeps the site it settled on. The GUI's own site edit asks the gate too now; it did not.

Pinned by `NodeProfileOwnWritesTests` (each of the four, against a node with fakes) and `ProfileDataRuleTests` plus
`MountSiteReconcileTests` (the rules).

**At a run's end the node writes the run's backlash back into the profile it was started from** (P3 part 3), as the GUI
does at a session's end: the per-focuser estimates the run inferred go onto the profile's focuser URIs
(`focuserBacklashIn` / `focuserBacklashOut`, `ProfileDataExtensions.WithBacklashEstimates`, lifted from `EquipmentActions`),
so the next run starts from them. `HostedSession.RunEnded` raises it once the run has ended, its Finalise included, and
`NodeRunProfileWrites` makes the write, reading the estimates at once because the next run's start disposes the session.
It is registered before the host of the runs, so a run that a stop ended still gets its write before the node exits.
Pinned by `NodeRunProfileWritesTests`.

### A device's setting, and the credential store

P3 part 4 (#930). `PUT /api/v1/devices/setting` (`DeviceSettingRequestDto`: the device's URI, the setting's key and
value, and optionally the profile whose slot takes it) commits a setting as the Equipment tab's text field does, by the
one rule both apply (`DeviceSettingHelper.Commit`, lifted from `EquipmentActions`):

- **A masked setting (an API key) goes into the node's credential store**, keyed by device and shared across profiles,
  never onto the URI or into the profile. **It never comes back**: the answer says only that it was a secret, and
  `GET /api/v1/devices/setting/secret` answers whether one is set.
- **Any other setting goes onto the device's URI** as a query parameter; with a profile named, the URI is replaced in
  that profile's slot for the device through the one profile writer (`ProfileDataExtensions.ReplaceDeviceUri`, also
  lifted), and the answer carries the new revision.
- **Only over the socket**: a secret must not cross the LAN, and a LAN client changes no profile (decision 4).

The device is resolved through the node's own device sources, so a device the node was not composed with (tianwen-server
composes the weather sources) has no masked settings there. **A node on a data root kept apart from the user's
(`TIANWEN_DATA_ROOT`) keeps its secrets in a file under it**, on Windows too, and one composed without `AddExternal`
falls back to the same: a test's node never writes the user's credential vault. `TianWenNodeClient.SetDeviceSettingAsync`
and `GetDeviceSecretAsync`. Pinned by `NodeDeviceSettingTests`.

### Every profile write, and the route that carries it

P3 part 5 (#930): the checklist P6 cuts the hosts over by. The GUI and the TUI share `AppSignalHandler`, so their write
sites are the same ones; each keeps the `EquipmentActions` transform it computes its edit with and swaps only its save.
`ProfileEditParityTests` pins that a transform's edit sent through the node stores exactly what the host's own save
stores today (by revision, the hash of the stored bytes), one case per site that edits.

| Write site today (`AppSignalHandler.Equipment.cs` unless named) | Through the node |
|---|---|
| New profile (`ProfileNameInput`, `EquipmentActions.CreateProfileAsync`) | `POST /api/v1/profiles` |
| Site (the latitude, longitude and elevation inputs, `SetSite`), and its push to the mount | `PUT /api/v1/profiles/{id}`; the node pushes an edited site to the connected mount when the profile wins |
| Mount limits (the four limit inputs, `SetMountLimits`) | `PUT /api/v1/profiles/{id}` |
| Guider focal length | `PUT /api/v1/profiles/{id}` |
| An OTA's name, focal length, aperture, design (`UpdateOTA`) | `PUT /api/v1/profiles/{id}` |
| Add an OTA (`AddOtaSignal`), remove one (`EquipmentTab.ProfilePanel`) | `PUT /api/v1/profiles/{id}` |
| Assign a device to a slot (`AssignDeviceSignal`, `ApplyAssignment`), the manual light panel (`AssignManualCoverSignal`) | `PUT /api/v1/profiles/{id}`; a slot's previous device is disconnected through P2's `disconnect-safety` and disconnect job, as `AutoDisconnectOrphanAsync` does |
| The site tie-breaker (`EquipmentTab.ProfilePanel`) | `PUT /api/v1/profiles/{id}` |
| A filter wheel's filter table (`EquipmentTab.FilterTable` and the TUI's, `SetFilterConfig`) | `PUT /api/v1/profiles/{id}` |
| A device's setting (`StringSettingInput`, `EquipmentTab.DeviceSettings`): a masked one into the credential store, any other onto the URI | `PUT /api/v1/devices/setting` |
| Every other `UpdateProfileSignal` (the generic replace the panels above post, the TUI's Equipment tab among them) | `PUT /api/v1/profiles/{id}` |
| Reconcile-all after a discovery (`DiscoverDevicesSignal`, `ReconcileAllProfilesAsync`) | the node's own, at the end of the discovery job |
| The legacy site migration at start (`TianWen.UI.Gui/Program.cs`) | the node's own, in the same reconcile |
| The mount's site on connect (`ConnectDeviceSignal`, `ReconcileSiteWithProfileAsync`) | the node's own, in the connect job |
| A camera's sensor on connect (`ConnectDeviceSignal`, `CaptureSensorSpecs`) | the node's own, in the connect job |
| The backlash mirror at a session's end (`SessionBootstrapper`, `SaveBacklashEstimatesIfChangedAsync`) | the node's own, at a node run's end |
| The CLI's `profile` verbs (`ProfileSelector`: create, pick the mount and the guider; `ProfileSubCommand`: create and every edit through `SaveAndListAsync`) | `POST` and `PUT /api/v1/profiles`, once the CLI is a client of the node (P6) |

`ProfileFlowProcessTests` runs a profile's life through a spawned node over its socket: created, assigned, reconciled by
a discovery, written into by connecting its mount and camera, a device setting placed, a stale edit refused, the active
profile's delete refused, and every write pushed.

## Previews go through the shared stretch, never a private one

`PreviewEncoder` (`Api/`) is the one JPEG preview encoder, used by `GET
/api/v1/preview/{otaIndex}` (per-OTA), `GET /api/v1/preview/guider` *and* the nina `prepared-image`. It
runs `StretchSolver` + `Image.RenderStretchedRgba` -- the same pipeline as the GPU viewer and the CPU/TUI
renderer -- and resolves `StretchMode.Auto` exactly as the live pane resolves a live frame (no
calibration, so colour renders Unlinked and mono Linked; it was a literal Linked). The shim previously
divided by `Image.MaxValue` and called it an auto-stretch, which renders a linear sub near-black; do not
reintroduce a private normalisation here.

**The frame is its publisher's, so a preview LEASES it for the encode** (`FramePreview`, one renderer for
every source) and only ever *reads* it (`normalizeToUnit: false`). A session's preview slot holds a
lease of its own (`Session.PublishCapturedImage`), so its frame stays readable until the next one
replaces it: a refused lease means "replaced mid-read", and the next poll finds its successor. The
per-OTA preview read the slot bare, and a bare lease would not have been enough, since the imaging loop
used to leave a released frame in the slot for the rest of each exposure (P0b item 15, #752).

**The change token is a conditional GET.** A picture carries its frame's token as `X-Frame-Number` and
as an `ETag` (`PreviewHeaders`, in Contracts), and a request whose `If-None-Match` names the current one
is answered 304, before the frame is leased or encoded; `TianWenNodeClient` sends it and reads the 304 as
`Unchanged`. The token is the node's for the source (`NodeFrames`, below), never
`CameraExposureState.FrameNumber`: that one advances as an exposure STARTS, so the preview served the
previous frame under the new number and ran a whole sub behind.

**The frame on show, whoever took it** (`NodeFrames`, P5 part 2 of
[../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), #934). Every route that serves a frame
reads it: the JPEG previews above, the linear frames below, the ninaAPI `prepared-image` and `FRAME-AVAILABLE`.

- **Which frame an OTA shows:** the node's own preview of that OTA, when it was taken since the node's latest run
  started; else the latest session's slot, going on or ended. A preview taken before a run is stale once the run
  starts, and is given back the moment that is seen; none can be taken during one, so a session going on always
  shows its own frames.
- **One token per source, the node's**, bumped whenever the frame on show changes (another object, or none). A
  producer's own number cannot serve: a session numbers its frames from the start and a preview slot would number
  its own, so a client holding a session's frame N would be answered "unchanged" for a preview numbered N too. The
  frame last seen is held WEAKLY, only to tell a new one from it.
- **A preview exposure is a job** (`POST /api/v1/preview/ota/{index}/exposure`, `NodePreviews`) with the camera of
  an OTA of the active profile, through `PreviewCapture`, the capture the GUI uses. It is refused in the device
  plane's order, and **the camera is LEASED while it exposes** (the GUI's preview never was), by the job, which
  therefore never joins another (`NodeJobs.TryStart`). `POST /api/v1/preview/ota/{index}/snapshot` saves the
  frame an OTA shows, leased for the write.
- **A plate solve is a job, and its result lives with the frame it is of** (P5 part 3): `POST .../ota/{index}/solve`
  solves the frame an OTA shows through `PreviewCapture.SolveAsync`, one solve of an OTA at a time (a slot of its
  own, `solve/ota/{index}`, not the camera), and `GET .../ota/{index}/solution` answers the last one
  (`PlateSolutionDto`): the FRAME TOKEN it belongs to, whether it solved, its words, and the whole solution
  (`WcsDto`: the reference point, the CD matrix and the SIP polynomials, a value it lacks as null, never 0). A solve
  reads a frame, so a run holding the camera does not refuse it.
- **Solve and sync is ONE job** (`POST .../ota/{index}/solve-sync`, `MountSolveSync`, the sky map's own, which moved
  to the Lib with `StatusText`): expose, solve, sync, with the mount and the camera LEASED for all of it. Its frame
  becomes the OTA's and its solution the OTA's solution whatever came of it, and it ends Succeeded only when the mount
  synced, otherwise Failed with the outcome's own words. A profile with no mount, a mount that cannot sync and a run
  going on are refusals, before any job.

Pinned by `NodeFramesTests`, `FramePreviewTests`, `NodePreviewExposureTests`, `WcsDtoTests` and `NodeSolveTests`.

## Linear frames on the wire

P4 part 1 (#931). A client that shows a frame (the viewer's stretch, statistics, star profile, plate solve and snapshot
save) needs it LINEAR, in floats, which the preview JPEG is not: it is stretched, 8-bit and downscaled. `FrameWire`
(`TianWen.Lib/Imaging`) is the binary shape a frame crosses the socket in, and `FrameReader` the client's reader:

- **The planes as they are held**, row after row (`[y, x]`), so neither end transposes; before them a small preamble (the
  magic `TWFR`, a version, the sample format) and a JSON header carrying the image's `ImageMeta`, bit depth and pedestal
  and each channel's filter, range and place.
- **16 bits when that loses nothing.** A frame whose every sample is a whole number from 0 to 65535, the usual camera
  frame in ADU, goes packed to 16 bits: half the bytes, still bit-exact. The check stops at the first sample that is not
  (a fraction, a NaN, anything out of range), and the frame goes as floats, bit for bit.
- **The reader recycles its planes.** A frame's planes come back to its `FrameReader` when the image is released
  (`ChannelBuffer`'s `onRelease`), and the next frame of the same shape is read into them, so a client showing frame
  after frame allocates no plane after the first; a 26 MP float plane is 104 MB, large-object-heap garbage otherwise. It
  reads through a pooled 1 MB band, never a frame-sized buffer. It keeps released planes BY SHAPE, so one reader can
  serve several sources.

Pinned by `FrameWireTests`, including a frame from the fake camera itself coming back bit for bit with its metadata.

**The route and the push** (P4 part 2). `GET /api/v1/frames/ota/{index}/latest` and `/api/v1/frames/guider/latest` serve
the frame a source shows now, the same frame the JPEG previews encode (`FrameSources`, `NodeFrames`), and
`/api/v1/frames/planetary/live/latest` and `/planetary/master/latest` a planetary capture's own (P5 part 5b), and
`/api/v1/frames/live/latest` a live view's (#1111), which the node keeps until its next run starts:

- **The number answers first.** A request names the number of the frame it holds (`after`); while the source still shows
  that one the answer is a 204 carrying the number, and nothing is leased. The number is the node's token for the
  source, which names exactly the frame it came with (`NodeFrames`). Compare for difference, not order.
- **The frame is leased for the write**, so the slot keeps it for whoever asks next.
- **`FRAME-AVAILABLE`** (`FrameAvailableDto`: the source and the number) is pushed by the broadcaster's poll when a
  source's token moves, with or without a session, so a new run's first frame and a preview taken outside a run are
  announced like any other. A hint: the route is authoritative.
- `TianWenNodeClient.GetLatestFrameAsync(source, after, reader)` reads through a `FrameReader` the caller keeps.
- **A planetary capture's two sources, and a live view's, also stream** (P5 part 5c): `NodeTransport.OpenFrameStreamAsync(source)` gives
  a `NodeFrameStream` whose `ReadAsync` asks for the next frame and reads it, drop-to-latest (see the planetary run
  above for why the client asks).

**Measured** (Release, this 16-core x64 desktop, with another session's builds running): a 26 MP frame (6248 x 4176)
crosses the socket into a recycled plane in 37 to 38 ms packed to 16 bits and 37 to 42 ms as floats. The plan had
extrapolated about 40 and 80: on the local socket the copy is cheap enough that twice the bytes cost almost nothing, and
packing earns its keep over TCP. The first run found the 16-bit pack's scalar loop slower than the bytes it saved (187 ms
against 42 in Debug, 29 ms of a 37.5 ms write in Release); it is vectorised through signed 32 bits, since the unsigned
float conversion is emulated below AVX-512 and measured slower than the scalar loop. `NodeFrameTests` pins the route,
the 204, the guider, the push and the measurement.

**Compressed over TCP only, and only when asked** (P4 part 3). A frame served to a TCP client whose `Accept-Encoding`
names Brotli goes Brotli at its fastest setting, else gzip at its fastest when it names only that; `q=0` refuses a coding.
The client's own transport asks over TCP (`NodeTransport.OverTcp`'s handler decompresses Brotli and gzip) and never over
the socket, and the node never compresses a frame on the socket whatever is asked: a sky frame compresses about 2x at
best (the plan's measurements), and on the socket a copy is cheaper than any codec, while on WiFi or 100 Mbit
compression roughly halves a frame's transfer. The saved-frame fetch (P4r, deferred) will travel as the FITS file's own
bytes instead. Pinned by `NodeFrameCompressionTests`.

**The client side** (P4 part 4). `RemoteSessionMirror` fills its frame slots (`LastCapturedImages`, `LastGuideFrame`)
with the node's LINEAR frames through this route, never the preview JPEG, so a remote rig's Live Session and Guider
panes stretch, measure and save the node's own frame exactly as they do a local session's; `LiveFramePreviewSource` is
unchanged, with the network behind the mirror.

- **Each poll names the frame a slot holds**, so a slot that has not moved costs a 204.
- **The slots keep a local session's contract.** A replaced frame is released only once its successor is published, so
  a pane's lease finds one or the other, and its planes go back to the mirror's one `FrameReader` once the last lease is
  disposed. A mirror asked for no frames, a finished session and a disposed mirror give back everything held.
- **Frames follow the screen.** `ViewContexts.PollAll`, which the GUI and the TUI run every frame, asks the rig on
  screen for its OTA and guide frames and every other rig for none, since N bound rigs each pulling full frames is the
  load the opt-in exists to prevent (the Home board's rule). Set per poll, so a rig connected while on screen needs no
  second step. Until this, nothing asked a rig for frames and a remote rig's panes stayed empty.
- **A saved sub is its FITS file, on this machine only.** `SavedFramePathOnThisMachine` hands the node's
  `LastFramePath` only to a mirror that reaches its node over the local socket (`IsOnThisMachine`, set from the
  transport), to read with `Image.TryReadFitsFile`; over TCP the path names a file on another machine, which a file of
  the same name here must never be taken for (fetching it is P4r, deferred).

Pinned by `RemoteSessionMirrorDriveTests` and, over TCP to a real node through the GUI's own `RemoteRigConnection`,
`RemoteRigLiveFrameTests`.

## The ASCOM Alpaca device plane

`/api/v1/{deviceType}/{n}/{member}` + `/management/...`, wired by `MapAlpacaApi`, so a remote TianWen
consumes this node's devices with the existing `AddAlpaca()` and no new client code.

- **It is a device plane and cannot become the session plane.** Alpaca has no vocabulary for session
  lifecycle, schedule, phase, prompts, notifications, autofocus or flats -- and no Guider device type
  at all. Native v1 stays the session plane by necessity.
- **Ownership is the hub lease, not an Alpaca policy.** Actuation and `Connected=false` answer
  `0x40B` with `DeviceOwnershipGate.Describe()`; reads and `Connected=true` always pass the lease.
  (Over TCP an application must first be allowed to command at all, its connect included: "Who may
  command the node over TCP" above. That is who may, not whose the device is.) Never make
  the plane read-only during a session -- every standard client PUTs `Connected=true` before reading,
  so that would make a running rig unreadable. The native and ninaAPI actuation routes ask the same
  lease (`ActuationGate`), as soon as they have the device and before anything touches its driver, and
  refuse with 409 naming the run: they commanded the session's own drivers unasked, so a client could
  slew the mount or abort an exposure mid-night (P0b item 8 of
  [../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), #752). The ninaAPI profile
  switch asks `ProfileSwitchGate` like the native one, and `DELETE /profiles/{id}` refuses the node's
  active profile, which is also the one any run going on was started from (a start makes its profile active, and
  `ProfileSwitchGate` keeps it while the run goes); the run writes back into it as it ends. Any other profile may go
  during a run (P0b item 18; until P3 part 3 every profile was refused while a run went).
- **Device numbers come from the ACTIVE PROFILE, in profile order** -- never from discovery, whose
  order varies between scans; a number that moved would point a client at different hardware
  mid-session.

Failures are **HTTP 200 with a non-zero ErrorNumber** (the spec reserves 4xx for malformed
requests). Each payload type needs its own `AlpacaResponse<T>` registration in
`AlpacaServerJsonContext` (the generic-envelope form of the no-`ResponseEnvelope<object>` rule below).
Pinned by `AlpacaServerRoundTripTests`, which drives our own `AlpacaClient` against our own server.

## The enhance endpoint (shipped shape: single-flight)

`TianWen.Server` calls `AddRcAstroAi()` (registers `SharpenPipeline`; the RC-vs-in-house probe stays
deferred, so startup spawns no `rc-astro`). The single-flight `HostedImageEnhancer` (an `Interlocked`
gate) runs `ProcessAsync` on a background task tied to **`ApplicationStopping`, not the request** (so
it outlives the POST and dies only on shutdown), with a **synchronous** `IProgress` relay that swaps
an immutable `EnhanceStatusDto` snapshot atomically (lock-free read; `Progress<T>` would post
out-of-order and could clobber the terminal status).

- `POST /api/v1/image/enhance` -- path-in/path-out via `EnhanceRequestDto`, mirroring `image sharpen`
  rather than uploading pixels. Returns `Enhance started` / `409 already running` / `404` /
  parse-error.
- `GET /api/v1/image/enhance/status` -- returns the concrete `EnhanceStatusDto`.
- `ENHANCE-PROGRESS` + `ENHANCE-COMPLETED` push through `EventBroadcaster` -> `EventHub` on the same
  `WebSocketEventDto` + `Dictionary<string,object?>` path as the session events.

AOT: the three DTOs (`EnhanceRequestDto`, `EnhanceStatusDto`,
`ResponseEnvelope<EnhanceStatusDto>`) are registered in `HostingJsonContext`. Verify by
**publishing** `win-arm64` and smoke-testing the binary -- body binding and the concrete status DTO
are the AOT-fragile parts -- not just building.

## Native-AOT correctness (`tianwen-server` is `PublishAot=true`)

Three things keep the minimal API working under AOT; none are optional, and a normal `dotnet build`
will NOT flag a regression (the IL2026/IL3050 trim/AOT warnings only surface on `dotnet publish -r
<rid>`):

1. **RDG runs in `TianWen.Hosting`, not just the server.** The Request Delegate Generator only
   intercepts `Map*` call sites in the project where it is enabled, and all the endpoints live in the
   `TianWen.Hosting` *library*. So `TianWen.Hosting.csproj` sets
   `<IsAotCompatible>true</IsAotCompatible>` +
   `<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>`. Without this the AOT
   publish emitted ~130 IL2026/IL3050 warnings (one pair per `Map*`) and the endpoints fell back to
   reflection-based delegates. `IsAotCompatible` also turns the trim/AOT analyzers on for the Hosting
   code itself, catching regressions at library-build time.
2. **Both JSON source-gen contexts are registered via `ConfigureHttpJsonOptions`** (in
   `AddHostedSession`): `HostingJsonContext` (camelCase) then `NinaApiJsonContext` (PascalCase) on the
   `TypeInfoResolverChain`. This is what makes **request-body binding** AOT-safe; the POST/PUT
   endpoints that take a complex body (`CreateProfileRequest`, `UpdateProfileRequest`, `PendingTarget`,
   `SetProfileRequest`, the device plane's request DTOs)
   would otherwise throw `NotSupportedException` at runtime. Responses do not depend on it; every
   `Results.Json(...)` passes an explicit `JsonTypeInfo`.
3. **No `ResponseEnvelope<object>` payloads.** A polymorphic `object` payload cannot be resolved by a
   source-gen context under AOT (it needs the runtime type's metadata). The two offenders were
   replaced with concrete types: `GET /api/v1/session/targets` -> `ResponseEnvelope<PendingTarget[]>`,
   and the ninaAPI `list-devices`/`rescan` anonymous types -> `NinaDeviceListItemDto[]`. **Never
   reintroduce a `ResponseEnvelope<object>` or an anonymous-type payload**; register a concrete DTO in
   the relevant `JsonSerializerContext` instead.

Verify after any endpoint change by *publishing* (not just building) and smoke-testing the binary:
`dotnet publish TianWen.Server -c Release -r win-arm64`, then run `tianwen-server.exe --port <p>` and
`curl` a GET, a complex-body POST, and a previously-`object` endpoint. The only expected publish
warnings are 2 third-party rollups (IL2104/IL3053) from `LibUsbDotNet` (optional Canon-over-USB
discovery; the lib ships no AOT annotations and we do not mask the warning).

## Invariants 7-13 in full (moved from CLAUDE.md, 2026-09-29)

7. **A run is the NODE's, never a request's.** Every start goes through `IHostedSession.TryStartAsync`
   (the node's token, a compare-and-swapped run record) and only `TryAbort` cancels it: Kestrel reuses a
   connection's cancellation source, so a request token let a later abandoned request cancel the night. An
   abort ends the run through its `Finalise`, never disposing it underneath; the host stopping aborts, awaits
   `Finalise`, then warms the hub's cameras inside `HostedSession.ShutdownBudget` (and systemd's
   `TimeoutStopSec` must allow as long). **A request that lasts (an event socket, a frame stream) ends at
   `ApplicationStopping`**, or one that ended only with its client held every stop for 30 minutes (#985).
   **A run is of any kind** (`INodeRun`: a dark library, polar alignment, a planetary capture; one that claims devices as the request is
   answered starts only once it is the node's: `PlanetaryCapture.TryPrepare`, then `StartPrepared` in its body): a refused
   start NAMES the run going on (`NodeRuns.AlreadyGoingOn`), a stop NAMES the run it means
   (`TryAbort(INodeRun)`), a run releases its lease as its body ends, and an interactive run stops once no
   client has been present for the detach grace (`INodeRun.EndsUnwatched`, `NodeRunWatch`; a RECORDING
   planetary capture is not interactive). Pinned by `NodeRunLifecycleTests`, `NodeRunWatchTests` and, over a
   spawned node's socket, `NodeRunsProcessTests` (`--detach-grace`).
8. **`new SessionConfiguration()` is the DECLARED defaults; `default(SessionConfiguration)` is all zeros**
   (a record struct with required primary-constructor parameters zero-fills on `new()` unless it declares a
   parameterless constructor; every API session once synced the mount to site 0, 0). `SessionConfigApiDto`
   carries every field: **a field added to the configuration is added there and to
   `SessionConfigApiDtoTests`'s round trip**, or it silently cannot cross the wire.
9. **A slow operation is a JOB, never an inline request.** Start it through `NodeJobs.StartOrJoin`, answer
   202 with a `JobDto`; the node runs it on its own token, `GET /jobs/{id}` is authoritative, `DELETE`
   cancels, `JOB-PROGRESS` is the hint (`/devices/discover` inline had a client's 10 s budget cut a serial
   sweep off mid-probe). **A job on a device holds the DEVICE** (`NodeJobs.TryStartOrJoin`): the same kind
   joins, another is a 409 naming the holder. A device-plane refusal (a lease, a cold camera) is an answer
   before the job (`DeviceOperations`), a non-job command is refused while a job holds the device, and **so
   is a run's start for every device it would claim** (a job holds its device in `NodeJobs`, not through
   the lease, which let polar alignment rotate a mount a slew job was driving, #981).
10. **The machine's node is found on its SOCKET, and one lock admits it** (`NodeSocket`, `NodeLock`): every
   node takes `node.lock`, only its holder clears a stale socket (never probe-then-delete), and the lock
   file is never deleted. A client reaches a node through `NodeTransport` (`OverSocket` / `OverTcp`), asks
   `GET /api/v1/node` first (compatibility is `NodeWire.Version`, never the build) and finds or starts it
   through `LocalNodeLauncher`, which starts the KEEPER (`--keeper`). **A node that dies leaves a crash
   journal** (`node.journal`, `NodeJournalService`) of its devices, its run and each camera's cooler INTENT
   (`IDeviceHub.SetCoolerIntent`; a ramp records its TARGET, and **a new place that commands a cooler owes
   the same**). It is believed only from the node its keeper saw crash (`--after-crash <pid>`) or when
   younger than `MachineBoot`, and ACTED on (devices reconnected mount first, cameras re-cooled), never a
   run resumed; two crashes within `NodeKeeper.CrashLoopWindow` reconnect nothing. **There is ONE cooling
   ramp, `CameraCoolingRamp`**: a second copy is a second answer to how fast a sensor may be cooled.
11. **A device is read through ONE set of readers, `DeviceHubReadingExtensions`** (GUI polls and the node's
   `DeviceStatePoller` alike), and **the node never reads a device a run holds** (two readers on one
   serial port race). `DEVICE-STATE` is pushed on a change compared at the resolution a reader is shown.
12. **The node writes a profile through ONE writer, `NodeProfiles`, and never from a cached copy** (#930):
   each write reads the file inside the writer's lock, an edit names the REVISION it was read at (stale =
   412), every write pushes `PROFILE-CHANGED`. **A new write goes through it, and so does a READ**
   (`GET /session/profile` on the discovery registry answered "no longer exists" for a profile saved since).
   Changing a profile is socket-only; a LAN client reads.
13. **Over TCP, seeing is free and a command needs control** (#1021), decided in ONE middleware,
   `NodeAccessGate`, by the surface a route's GROUP declares (`NodeProtocolMetadata`): a native route that is
   not a GET, an Alpaca PUT (`Connected = true` included) and any ninaAPI route not `.ReadsOnly()` is a
   command, passing over the socket, with a grant (`Authorization: Bearer`, `LanGrants`) or, for another
   app, from an allowed address or host. **A new route goes in its surface's group** (a new ninaAPI read owes
   `.ReadsOnly()`, only asking for control is `.OpenToAsk()`); `NodeAccessTests` fails on a route outside
   the groups. Refusals are each surface's own (a native 401, never the 403 a socket-only route keeps).
   Control is asked through `LanInvites`; managing who may is socket-or-grant, and prompts and the quit
   question count only clients that may command. Plain HTTP still: `docs/architecture/hosting-api.md`,
   "Who may command the node over TCP".
