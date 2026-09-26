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
  address; the connection's end-point feature is not one a request can see), 403 over TCP.
- **`HoldsHardware`** on `GET /api/v1/node`: a device connected or a run going on. A node that holds hardware is
  never stopped to replace it.
- **`--fake-devices`** registers the fake device source and no other, for a node that must touch no hardware: a
  test's, or a demonstration's. With `TIANWEN_DATA_ROOT` it touches none of the user's data either
  (`KeptNode`, the functional tests' real keeper and node).
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
`NodeSettings`, decision 3), read at every start and changed only over the socket (`PUT /api/v1/node/share`, 403 over
TCP):
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
     `TimeoutStopSec=35min`, or it kills the process mid-ramp.

   Pinned by `NodeRunLifecycleTests`, all nine seen failing against the old code.
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
     assumes it. Not yet under two connects of one device at once, nor after a driver drops (#806).
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
  The older DTOs still send `JsonNumber.ForWire`'s 0; P5b moves them to null.
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

Connecting does not yet reconcile the mount's site or capture a camera's sensor geometry into the profile, as the GUI
does on connect: both are profile writes, which move to the node in P3. `TianWenNodeClient` has `ConnectDeviceAsync`,
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

## Previews go through the shared stretch, never a private one

`PreviewEncoder` (`Api/`) is the one JPEG preview encoder, used by `GET
/api/v1/preview/{otaIndex}` (per-OTA), `GET /api/v1/preview/guider` *and* the nina `prepared-image`. It
runs `StretchSolver` + `Image.RenderStretchedRgba` -- the same pipeline as the GPU viewer and the CPU/TUI
renderer -- and resolves `StretchMode.Auto` exactly as the live pane resolves a live frame (no
calibration, so colour renders Unlinked and mono Linked; it was a literal Linked). The shim previously
divided by `Image.MaxValue` and called it an auto-stretch, which renders a linear sub near-black; do not
reintroduce a private normalisation here.

**The frame is its publisher's, so a preview LEASES it for the encode** (`CapturedImagePreview`,
`GuidePreview`) and only ever *reads* it (`normalizeToUnit: false`). A session's preview slot holds a
lease of its own (`Session.PublishCapturedImage`), so its frame stays readable until the next one
replaces it: a refused lease means "replaced mid-read", and the next poll finds its successor. The
per-OTA preview read the slot bare, and a bare lease would not have been enough, since the imaging loop
used to leave a released frame in the slot for the rest of each exposure (P0b item 15, #752).

**The change token is a conditional GET.** A picture carries its frame's token as `X-Frame-Number` and
as an `ETag` (`PreviewHeaders`, in Contracts), and a request whose `If-None-Match` names the current one
is answered 304, before the frame is leased or encoded; `TianWenNodeClient` sends it and reads the 304 as
`Unchanged`. The token is the frame's own, `ISessionTelemetry.LastCapturedImageNumber` (the guider's
`LastGuideFrameNumber`), never `CameraExposureState.FrameNumber`: that one advances as an exposure
STARTS, so the preview served the previous frame under the new number and ran a whole sub behind.

## The ASCOM Alpaca device plane

`/api/v1/{deviceType}/{n}/{member}` + `/management/...`, wired by `MapAlpacaApi`, so a remote TianWen
consumes this node's devices with the existing `AddAlpaca()` and no new client code.

- **It is a device plane and cannot become the session plane.** Alpaca has no vocabulary for session
  lifecycle, schedule, phase, prompts, notifications, autofocus or flats -- and no Guider device type
  at all. Native v1 stays the session plane by necessity.
- **Ownership is the hub lease, not an Alpaca policy.** Actuation and `Connected=false` answer
  `0x40B` with `DeviceOwnershipGate.Describe()`; reads and `Connected=true` always pass. Never make
  the plane read-only during a session -- every standard client PUTs `Connected=true` before reading,
  so that would make a running rig unreadable. The native and ninaAPI actuation routes ask the same
  lease (`ActuationGate`), as soon as they have the device and before anything touches its driver, and
  refuse with 409 naming the run: they commanded the session's own drivers unasked, so a client could
  slew the mount or abort an exposure mid-night (P0b item 8 of
  [../plans/hardware-in-the-server.md](../plans/hardware-in-the-server.md), #752). The ninaAPI profile
  switch asks `ProfileSwitchGate` like the native one, and `DELETE /profiles/{id}` refuses the node's
  active profile and, while a run is going, any profile, since the node does not record which one the
  run was started from (P0b item 18).
- **Device numbers come from the ACTIVE PROFILE, in profile order** -- never from discovery, whose
  order varies between scans; a number that moved would point a client at different hardware
  mid-session.

Failures are **HTTP 200 with a non-zero ErrorNumber** (the spec reserves 4xx for malformed
requests). Each payload type needs its own `AlpacaResponse<T>` registration in
`AlpacaServerJsonContext` (the generic-envelope form of the no-`ResponseEnvelope<object>` rule below).
Pinned by `AlpacaServerRoundTripTests`, which drives our own `AlpacaClient` against our own server.

## The enhance endpoint (shipped shape: single-flight)

`TianWen.Server` calls `AddRcAstroAi()` (registers `SharpenPipeline`; the RC-vs-SAS probe stays
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
   endpoints that take a complex body (`CreateProfileRequest`, `PendingTarget`, `SetProfileRequest`)
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
