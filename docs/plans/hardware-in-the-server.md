# Hardware in the server: the GUI drives every device through a local `tianwen-server`

**Status: PLANNED (2026-09-24, raised by the user); reviewed against `main` 2026-09-25, every decision
made; P0 DONE (2026-09-26: P0a #743, P0b #752, P0c #788); P1 DONE (2026-09-26, #917): the socket, the lock and
`GET /api/v1/node` (#918), the server built beside its clients (#919), the keeper and the safe stop over the socket (#920), the client's launcher (#921), the LAN share (#922), the active profile as node state (#923), presence for prompts (#924), the crash journal (#925) and acting on it (#926); proven on win-x64, AOT-published with the process tests run against the binary; P2 onward not started.** Issue #751. P0 is urgent on its own: it
closed a regression on `main` (P0a, #743), and it closes server lifecycle and wire bugs that already
hurt remote rigs (P0b, #752) and bugs in the hosts that the split would carry over (P0c, #788). **It ships as 10.0**, a major (P9): closing a window no
longer stops the rig, and every host needs `tianwen-server` beside it.

**The goal.** Exactly one process per machine owns hardware: `tianwen-server`. The desktop GUI, the TUI
and the CLI's hardware commands become clients of it over a per-user local socket, using the same API
and client library a remote rig already uses. A GUI that crashes, wedges its GPU, is closed or is
restarted then touches nothing: the session keeps imaging, the cooled cameras keep their setpoint, the
mount keeps tracking, and the mount limits stay enforced.

**Clients are peers** (user, 2026-09-25). The GUI on the machine itself (over RDP or at its screen), a
dashboard on a laptop over the LAN, the TUI and a CLI command can all be attached at once. None is
primary, and none attaches or detaches as a step: a client connects and sees the rig as it is, and a
command from any of them goes through the node's own rules (the lease). The worked case is the user's
NUC: it logs on by itself, its node starts at logon (decision 3), and the user then opens the GUI over
RDP, or watches from the laptop, or both, with nothing to set up on either.

**Why now.** The Adreno X1-85 wedge (`gpu-device-recovery.md`) showed the failure mode: the GUI's
process is the rig's process, so anything that kills or freezes the window reaches the hardware.
SdlVulkan.Renderer 7.48 made that WORSE for a running night (P0a). An open native render crash on the
Equipment tab (exit 127, #410) is the same failure by another route. With hardware out of process,
"recover the GPU" shrinks to "restart the window", which is what P7 does.

Surveys this plan builds on (2026-09-24, read-only, cited below by file): what the GUI does with
hardware today, the server and client surface, and every decision the docs already record. The
transport was proven by a spike (see "Transport").

**The review of 2026-09-25** checked every claim below against `main` with four read-only surveys (the
server and its wire, the GUI's hardware surface, what a mirrored session can show, the TUI, the CLI and
who writes each file in AppData), and put every open decision to the user. It confirmed the design
(the socket, the lock, the journal, a saved frame as its FITS file) and changed the plan in five ways:
- **Today's server is further from carrying a night than P0b said.** A session started over the API
  runs on a ZERO-FILLED configuration and syncs the mount's site to 0°, 0°; a run's drivers live
  outside the hub; a finished session blocks the next start. P0b grew from nine items to eighteen.
- **A mirror cannot show a local session yet.** The running flag, the abort token, the pending prompt
  and the notification feed are written only by the in-process bootstrappers, so a mirrored run shows
  the Live Session tab's IDLE layout, and a dozen fields cross lossy. A new phase, P5b, closes that
  before the cut, proven by one fake session rendered both ways.
- **The TUI and the CLI cannot wait for a later phase.** The TUI builds the same `AppSignalHandler` as
  the GUI, and five CLI verbs build their own hub and probe every port, so all three cut over in one
  wave (P6; P8 folded in).
- **Present-day bugs the split would carry over** go first, as P0c: the TUI's quit hangs, polar and
  planetary take no device lease, and the AppData write is atomic within a process but not across two.
- **The user's decisions, all made** ("Decisions"), add a keeper that restarts a crashed server, a
  persistent LAN share that also starts the node at logon, and defer shared memory (P4b) and the remote
  saved-frame policies to a 10.x minor.

## What this plan re-decides (read before arguing with it)

Four recorded decisions assumed the GUI owns local hardware. This plan reverses each on purpose:

1. **"Closing the client must abort the local session"** (`remote-profile.md`, P3 item 2, and
   `RequestQuit` in `TianWen.UI.Gui/Program.cs`). It becomes: **closing a window ASKS, and its default
   leaves the rig running; stopping the rig is an explicit act** (decision 1). The local server is just
   another node, which the overlay model already handles; the rule "a session on a rig keeps running,
   closing the client that was watching it is not a reason to stop the rig" now covers the local rig
   too. **Only the LAST client attached asks at all**: closing the RDP window while the laptop still
   watches detaches with no question, so one window can never warm up a rig another is using.
2. **"A respawn ends the night"** (`gpu-device-recovery.md`, the Drawboard prior-art section, which
   chose in-process device recreation because leases would have to be re-acquired). With hardware in
   the server, a GUI respawn re-acquires nothing, so a respawn becomes the SIMPLE recovery (P7), and
   in-process device recreation becomes optional.
3. **"You look at a rig, you do not reconfigure it"** (`remote-profile.md`, "Profile editing over the
   API"). The local socket's client IS the Equipment tab, so it must edit profiles. Resolved by
   TRANSPORT, not by relaxing the rule: editing endpoints answer only on the local socket, and a LAN
   client keeps today's rights.
4. **The GUI drives its own `MountLimitWatcher`** (`Program.cs`, `mount-safety-limits.md` P3), and so
   does the TUI (`TuiSubCommand`). After the cut only the server runs one. Two watchers over one mount
   is a double actuation.

Decisions that STAND and bind this plan: the session always runs on the node that owns the hardware
(so the GUI never runs a session over proxied drivers); reads are never leased; polling is
authoritative and the WebSocket is a latency hint; one `LiveSessionState` per view context; a pushed
schedule beats the target queue; with no client attached a prompt gets the session's unattended
answer; numeric enums on the wire; AOT verified by `publish`.

## Not INDI: nobody manages the server

This is INDI's shape (a device server, clients over a socket), and the user's condition is that it must
not bring INDI's management along (2026-09-24). INDI's jank is almost none of it in the protocol: you
choose drivers and start a server before a client can connect, a crashed client leaves a server on
the port, settings live in two places, and client and drivers skew in version. **The server is an
implementation detail of the GUI, and a user never starts it, stops it, configures it or waits for it.**
Every rule below is a requirement, and a phase that breaks one is not done:

| INDI pain | The rule here |
|---|---|
| Pick drivers, then start `indiserver` with that list; a device not in the list does not exist | No driver list. The server registers every device source the GUI does today, and discovery finds what is plugged in. There is nothing to select |
| Start the server (by hand, from Ekos, or through INDI Web Manager) before the client can connect | The GUI finds or starts its node itself, before its first frame asks for anything. No menu item, no setting, no "connect to server" step, no separate launcher |
| A crashed client or server leaves a process on port 7624, so the next start fails | No port for a local node unless the user shares the rig (decision 3). The lock file admits exactly one server, and the lock holder clears a stale socket. A spawned server stays until logoff (decision 2), so a reopened window never waits for one |
| Settings in two places: the client profile and each driver's saved config | One place. The server is the only profile writer, and the GUI edits through it (P3). No per-driver configuration exists to save or load |
| Client and drivers of different versions | The server is spawned from the client's OWN directory, never from `PATH`, and ships in the GUI's and the CLI's archives and in the GUI's `.app`, so a client always gets its own build. The handshake handles the one skew that can happen (an older server still running a night after an update) without asking anything unless a session is running |
| A generic property bag: the client must know each driver's property names | A typed API of whole operations (connect, warm up and disconnect, solve and sync, a session), each completed in the server. A client never drives hardware one property at a time |
| BLOB mode and per-client BLOB enabling, just to receive an image | Frames arrive with no opt-in: bytes over the socket, packed to 16-bit when lossless and compressed only for a remote client that asks (P4); a saved frame is its own FITS file |
| A driver stuck in a bad state means restarting the server | A stuck device is disconnected and reconnected from the Equipment tab, as today. Restarting the node is never a user action for a local rig |

**What the user does see**:
- one status indicator (for example "Rig: running, 2 clients");
- the quit dialog, from the last client attached only, when a run is active or a device is connected
  (decision 1);
- a single setting, "Share this rig on the LAN", which the machine keeps and which also starts the node
  at logon while it is on (decision 3).

**When the server itself dies**, the keeper that started it starts a new one at once (decision 10),
which reconnects what the dead one held (see "When the server dies" under the target architecture); a
client says so in plain words. The run it was in is lost, as a GUI crash loses one today. That case
becomes RARER, because the server has no window and no render thread, and runs no GPU work while it
holds hardware (decision 12).

INDI runs one process per driver so that a driver crash takes out only its device. This plan runs one
process for all drivers, which is simpler to own and to manage. It isolates only the class of driver
known to crash its host, in-proc .NET Framework COM drivers, which already go through
`tianwen-ascomhost`.

**The fallback is never a dead end.** If the node cannot be spawned the way it should be (a job that
forbids breakaway), the GUI spawns it plainly, so the server dies with the GUI, and says the rig will
not outlive the window. It never refuses to run. If `tianwen-server` is missing beside the client (a
broken install, a dev build of one project), the rig tabs say so and name the path it looked in, and
the planner, the sky map and the viewer keep working.

## P0a: a dead GPU must not end the night (DONE 2026-09-25, #743; see "What shipped" at the end)

**What happened before the fix, read from the code** (the fix was checked live; the old failure was
never run). SdlVulkan.Renderer 7.48
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
session must run to its end and the log must show `Finalise`'s park and warm-up.

**Out of scope for P0a.** Commands only run after a rendered frame: `bus.ProcessPending` sits in
`OnPostFrame`, so with the GPU dead no signal runs. The headless path must not rely on the bus.
Everything above is plain code after `loop.Run`.

**What the review found (2026-09-25): P0a needs a SdlVulkan.Renderer release first.**
- **The GUI cannot reach `OnGpuWedged`.** It exists only on `SdlWindowView`. The GUI uses the
  single-window `SdlEventLoop` constructor, whose forwarding properties stop at `OnRenderDegraded`, and
  the loop's `_primary` view is private (its accessor is DEBUG-internal). Either the loop forwards
  `OnGpuWedged`, or the GUI moves to `AddWindow`; the forward is the smaller change.
- **Nothing could pump the window without the GPU.** `ShutdownDrain.PumpUntilComplete` re-runs the
  same loop: `RenderView` still ran on any armed redraw, and the abandoned `GpuRecoveryTask` was never
  cleared, so it re-fired the wedge and stopped at once.
- **Some stops never fired the callback**: a recovery that faults and a recovery that throws while
  being started. A non-Vulkan exception from `OnRender` is rethrown out of `Run` on purpose (an app
  bug), and `Program.cs` has no `try` around `loop.Run`, so that path crashes with no drain at all:
  the GUI's half of P0a wraps it.
- **The title freezes with the frames**, because the GUI sets it from `OnPostFrame`.
  `SdlVulkanWindow.SetTitle` touches no Vulkan, so the headless tail sets it directly.
- **The inspector ran on the render thread only while `Run` did**, so after a wedge only the log could
  confirm the headless path; a re-entered `Run` brings it back (no screenshot: no frame is drawn).
- **During a submit storm the bus already stalls**: dropped frames never reach `OnPostFrame`, so
  signals, tracker completions and the title all wait, while `CheckNeedsRedraw` keeps starting
  hardware polls. The headless tail must not rely on the bus (as P0a already said) and drops the
  GUI's `CheckNeedsRedraw` poll for its own completion check.

**SdlVulkan.Renderer 7.49 ships first** (SharpAstro/SdlVulkan.Renderer#112), then TianWen repins. It
adds no new pump: one hand-off, `DeclareGpuWedged`, now serves all six terminal paths (the two silent
ones included) and marks the window `IsGpuWedged`, after which the loop keeps it INERT (never
rendered, never resized, no recovery polled) while its events and its `CheckNeedsRedraw` still run.
So step 3 is simply `Run` again, which is what `ShutdownDrain` already does, and the single-window
loop forwards `OnGpuWedged` and `IsGpuWedged` at last. **The quit path that P0a leans on has bugs of
its own**, fixed in the same change because the headless tail calls into it:
- `RequestQuit` keys on `IsRunning`, so a flat run and a polar run are not cancelled, and their leased
  cameras are force-warmed and disconnected under them (`force: true`).
- Polar's refine loop is `while (!ct.IsCancellationRequested)` on a token nothing cancels, so a quit
  during polar hangs the shutdown for ever: keys are ignored and `OnQuit` is intercepted.
- `QuitRequested` is never cleared when the confirmation is dismissed, so the GUI quits by itself when
  the session later ends.
- With a remote rig on screen, `RequestQuit` sets the confirmation on the LOCAL context while the tab
  renders the remote one: the confirmation is invisible, Enter does nothing, and Esc twice aborts the
  local session unconfirmed. (This is P0b item 9's reachable half; its described half is not reachable
  today, see there.)

**What shipped (2026-09-25, #743).**
- **`RigShutdown`** (`TianWen.UI.Abstractions`) is the one stop sequence, for a quit and for a lost
  display:
  - polar alignment is cancelled either way;
  - a quit aborts the session and a flat run;
  - a lost display lets them finish, with prompts answered unattended, until the caller's stop request
    (closing the headless window) turns that into an abort;
  - each run ends through its own ending, awaited through `LiveSessionState`'s `SessionEnded`,
    `FlatRunEnded` and `PolarRunEnded`;
  - only then are the cameras warmed or disconnected, once per process however many stops overlap.
- **`Program.cs` sends a wedge (`OnGpuWedged`) and a fault out of the loop down the same headless
  tail.** A fault out of the loop is an exception from a render or an input handler, which used to crash
  the process with no drain. On that tail:
  - the window stays pumped and closable;
  - its title is the stop's progress, and says what closing does while a run goes on;
  - closing it stops the rig.
- **The four quit-path bugs above are fixed in the same change.**
- **The live check found a fifth**, and it is what turned the first run into "Display failed". The Live
  Session and guider previews read a frame the session or the guider had already released, and threw
  on the render thread. They lease it now (`LiveFramePreviewSource.AcceptFrame`), and the doc of
  `Image.TryLease` no longer claims the render thread gets away with a bare reference.

Checked live twice, on a fake rig on the desktop (2026-09-25, `TIANWEN_NOW` at 21:30 in Melbourne):
1. **Display failed.** The preview's recycled-frame exception left the loop while the session was
   focusing.
   - The session went on without a display through guider calibration into Observing, slewing and
     solving.
   - Closing the window aborted it into `Finalise`, which warmed the camera from -9 to 20 °C and parked.
   - The process exited about eight minutes after the close.
2. **Display lost.** `gpuFault reject`, with no count, was injected during cooling. The renderer
   declared the GPU wedged 9 s later, and the GUI went headless.
   - With no display, the session went on through rough focus, autofocus, guider calibration, a plate
     solve and centering into Observing.
   - It exposed, fetched and wrote a 320 s frame #1, then started #2.
   - Closing the window 16 minutes after the wedge aborted the session within 17 ms.
   - `Finalise` stopped guiding and tracking, warmed the camera to 20 °C, then disconnected the guider
     and parked and disconnected the mount. "Shutdown complete" came 8 min 16 s after the close, and
     the process exited 2 s later.

Neither run let a session end at its own time, which takes a whole night.
`RigShutdownTests.WithTheDisplayLostTheSessionFinishesOnItsOwnAndItsPromptIsAnsweredUnattended` covers
that path, and the prompt.

Pinned by:
- `RigShutdownTests`: the order, both modes, the close, two stops at once, and the title's progress;
- `LiveFramePreviewSourceTests`: the lease.

The run also found that the inspector's signal directory read a parameter named `RA` from the key `rA`,
so a pin posted with `ra` went out at RA 0 and answered "queued". DIR.Lib 11.5 matches keys in any case
and refuses one that binds nothing (SharpAstro/DIR.Lib#101).

## P0b: server lifecycle and wire bugs (independent; they hurt remote rigs today)

**Progress (2026-09-25).** Items 1, 2, 3, 4 and 12 are fixed by the lifecycle PR (#797): a run
is the node's, an abort ends it through its `Finalise`, and the host starts and stops the node in the
safe order (see `docs/architecture/hosting-api.md`, the fourth session-plane invariant). Item 10 is
fixed by #799: an API session runs on the declared defaults, and the whole configuration crosses the wire
(the fifth invariant). The site case it left is fixed by #808 (#798): a run settles its one site once its
mount connects, by the reconcile the GUI applied on connect, now in Lib, and the limit poll uses it, where
the configured site alone had left the horizon limit off for a run whose request named no site. Items 5,
6, 15 and 16 are fixed by #800: a mirror hears the node's events, an error answers its own HTTP status,
and the live preview leases a frame the session keeps on show, under the slot's own token as a
conditional GET (`hosting-api.md`, the preview section). Items 7 and 13 are fixed by #801: a broadcast
only queues, each client with its own bounded sender, and a prompt is held only for a client that can
answer it, and only until it settles. Item 14 is withdrawn (below). Items 8 and 18 are fixed by #803: a
client cannot command hardware a run is driving, nor switch or delete the profile out from under it.
Item 11 is fixed by #807: a run's drivers are the hub's, so a node holds one driver per device. Item 9
is fixed by #812: the GUI's device actions refuse to drive this computer's rig while a remote rig is on
screen. The rest (17) waits for P1's long-operation model.

Each item below is confirmed in the code, except where it says otherwise; the review of 2026-09-25
re-checked all nine and found nine more (10 to 18). The first four, and 10 and 11, decide whether a
server can be trusted with a night at all:

1. **A session runs on the HTTP request's token.** `POST /session/start` passes the endpoint's
   `CancellationToken` (bound to `RequestAborted`) to `RunAsync`. A client dropping its connection can
   cancel the night, and a GUI restart is exactly that. The run belongs to a server-lifetime token that
   `HostedSession` owns. **Also** `/session/flats` and the ninaAPI shim's sequence start; and when the
   token is already cancelled, `Task.Run(..., ct)` skips the run AFTER `SetSession`, leaving a phantom
   session that answers every later start with 409. `/image/enhance` already does this right, on
   `ApplicationStopping`.
2. **`HostedSession` is never started or stopped by the host.** It is registered as `HostedSession` and
   `IHostedSession`, never as `IHostedService`. A probe of `AddHostedSession()` lists exactly two hosted
   services, `MountLimitWatcherService` and `EventBroadcaster`. So `StartAsync`, and with it
   `SessionFactory.InitializeAsync` (the plate-solver check and device discovery), never runs, and
   neither does `StopAsync` on shutdown. (Profiles are discovered today only as a side effect of the
   limit watcher's first tick.)
3. **`POST /session/abort` cancels nothing and disposes the rig under the run.** `_cts` is null (item
   2), so `StopAsync` skips the cancel and goes straight to `session.DisposeAsync()` while `RunAsync`
   carries on over disposed drivers. `Finalise` never runs properly. The fix: abort cancels the run's
   token, awaits `RunAsync` (and so `Finalise`), and disposes only afterwards. **Worse than it read**:
   the dispose disconnects each driver directly (`ControllableDeviceBase.DisposeAsync`), past the hub
   lease, and `_session` is cleared at once, so `/state` answers 404 while the old run still holds its
   leases.
4. **Host shutdown force-disconnects everything with no park and no warm-up.** On SIGTERM or Ctrl+C the
   container disposes `DeviceHub`, which force-disconnects every driver. **Worse than it read**: a run's
   drivers are not in the hub at all (item 11), so nothing even force-disconnects them; they are
   abandoned at process exit. No `ShutdownTimeout` is set, so the default 30 s applies.
   - The fix: `HostedSession.StopAsync` runs the same safe stop as an abort.
   - Connected cameras outside a session are warmed before they disconnect.
   - That needs `EquipmentActions.WarmAndDisconnectAsync` moved from `TianWen.UI.Abstractions` into Lib,
     as a device-model operation in `Devices/*Extensions.cs`. The server does not reference
     `UI.Abstractions`, deliberately. It needs nothing UI-specific; it is camera-only (no park), and
     reaches only hub drivers, which item 11 makes every driver.
   - `HostOptions.ShutdownTimeout` must cover a warm-up ramp (up to 15 min), and a service manager's own
     stop timeout (systemd `TimeoutStopSec`) must be documented to match.

Then the correctness items:

5. **Every WebSocket event is dropped client-side** (CONFIRMED). The server sends
   `ResponseEnvelope<WebSocketEventDto>`, while `TianWenEventStream` decodes a bare `WebSocketEventDto`
   whose `Event` is `required`; the `JsonException` is swallowed at Debug while `IsEventStreamConnected`
   reads true. So FrameWritten, PlateSolve and GUIDE-STEP never reach a remote mirror, and the mirror's
   event-sourced `PlateSolveHistory` stays empty. No test sends a real server event through the real
   client: add that test first, see it fail, then fix. (Even decoded, the mirror handles only two of the
   seven events the node sends; that is P5b.) **FIXED**: the stream decodes the envelope, a message it
   cannot decode warns once per connection, and `NodeEventStreamTests` sends a real session event through
   the real node to the real client, which failed first.
6. **A "no frame yet" preview is HTTP 200 with a JSON body**, and the client checks only the status, so
   it decodes JSON as a JPEG, and logs the failure on every poll. The same for no session, a bad OTA
   index and the guider preview: `Results.Json(ResponseEnvelope.Fail(..., 404))` with no `statusCode:`
   answers 200. **FIXED**: every native v1 answer goes out under its envelope's own status
   (`EnvelopeResults.Json`, all 90 of them); the ninaAPI shim keeps HTTP 200, since Touch N Stars reads it,
   and the Alpaca plane keeps ASCOM's convention.
7. **`EventHub` sends to one socket from several broadcasts at once** (PARTIAL). The runtime's
   `ManagedWebSocket` serialises whole-message sends, so frames do not interleave; the real faults are
   that ordering is not guaranteed and nothing times a send out, so one stalled client blocks every
   later client and every later broadcast queues behind it without bound. Give each client a bounded
   send channel with a single writer and a send timeout: a lock-free hand-off, per the lock rules.
   **FIXED**: every client has a bounded queue (256 events) drained by its own sender, each send bounded
   at 10 s. A broadcast serialises once per pool and queues, so it never waits on a socket, and order
   holds per client. A client that falls behind or stalls a send is dropped, its socket aborted, and
   resyncs by polling. The two socket endpoints are one helper now.
8. **Native v1 in-session actuation skips the lease.** The mount and OTA endpoints call `session.Setup`
   drivers directly, without `DeviceOwnershipGate`: `/mount/slew`, `/park`, `/unpark`, `/tracking`,
   `/ota/{i}/focuser/move`, `/focuser/stop`, `/filterwheel/change`, and every actuation route of the
   ninaAPI shim. Only the Alpaca plane asks the gate. **FIXED**: all 22 routes (7 native, 15 ninaAPI)
   ask `ActuationGate` as soon as they have the device, before any capability check or driver access,
   and refuse a device a run is driving with 409 naming the run. `NodeActuationGateTests` sends every
   route against a leased rig of fake devices; before the fix each fell through to its driver check.
9. **GUI: the abort confirmation cancels the LOCAL session while a remote rig is on screen.** The Live
   Session tab sets `ShowAbortConfirm` on the Active context, but `ConfirmAbortSessionSignal`'s handler
   cancels `LocalLiveSession.SessionCts`. The prompt reply handler has the same shape. **The review found
   this trigger unreachable TODAY**: Esc and ABORT fire only while `IsRunning`, which nothing sets for a
   mirror. It becomes reachable the moment P5b makes a mirror set it, so P5b routes abort and prompt
   replies to the Active context's own node. **What IS reachable now** is the reverse, the trap
   CLAUDE.md names ("reaching for Active where Local is meant"): the invisible quit confirmation (fixed
   in P0a), and four actions that drive the LOCAL rig while a remote rig is on screen: planetary Start
   (the remote mode pill offers Planetary, and Start drives the local camera and flips the local mode),
   the planetary N/S/E/W nudges, Goto from any object panel, and Solve and Sync (its reticle is the
   Active mount's). Until P6 routes each to its context's node, each refuses outside the Local context.
   **Verify each with a test before fixing.** **FIXED** by #812, with a fifth the review missed: the
   planetary panel's focuser jog, beside its nudges, moved the local focuser the same way, and the preview
   panel's focuser jog and goto share its handler's resolver. Each now asks `EnsureLocalContext`, as the
   three run starts already did, and refuses with the reason. `GuiContextGatingTests` drives the GUI's own
   signal handler with a remote rig on screen: all five tests failed first, each on the local work
   actually starting, and a control shows the same Goto still slews with this computer's rig on screen.

Found by the review (2026-09-25), all confirmed in the code:

10. **A session started over the API runs on a ZERO-FILLED configuration.** `SessionConfiguration` is a
    `record struct` whose primary constructor has required parameters, so `new SessionConfiguration()`
    is the zero-initialising struct constructor and NONE of the declared defaults apply (the trap in
    CLAUDE.md's "record struct defaults"). The site is 0°, 0°, not NaN, so `Session.Lifecycle` syncs
    the MOUNT's site to latitude 0, longitude 0; the setpoint is 0 °C; autofocus has 0 steps over a 0
    range; guiding gets 0 tries; nothing dithers; the flip window is 0/0; the cameras are not warmed at
    the end. `SessionFactory.Create` passes it through unchanged. The client's `StartSessionAsync` sends
    no body at all, and a body sent chunked (the client's own `JsonContent`) is IGNORED, because the
    endpoint reads it only when `ContentLength > 0`; a body that fails to parse falls back to the same
    zeros in silence. Even a body carries only 16 of the 60 fields (`SessionConfigApiDto`), and
    `/session/flats` 9 more; 35 have no wire form, including the site, the flip windows and
    `UnattendedPromptResponse`. The fix: the baseline is the declared defaults (a real constructor, or
    `SessionConfiguration.Default`), the site comes from the profile when the request has none, the
    body is read whatever its framing and a bad one is a 400, and the whole configuration crosses the
    wire (P5b finishes the parity).
11. **A run's drivers live outside the hub.** `Session.Lifecycle` connects each device's driver itself
    unless the hub already holds it connected, so on a server, where nothing pre-connects, a session
    and the hub are two driver worlds: `/devices` reports the session's devices disconnected, the
    Alpaca plane cannot see them, and an Alpaca `connected=true` opens a SECOND driver on a device the
    session is driving; the limit watcher loses a mount once its run ends. The fix: the session connects
    through the hub and borrows, so a node holds one driver per device. Every later phase assumes it.
    **FIXED** by #807 (`hosting-api.md`, the sixth session-plane invariant):
    - A wrapper connects through the hub (`ControllableDeviceBase.ConnectAsync(hub)`), which ADOPTS the
      driver it built (`IDeviceHub.AdoptAsync`), so whatever its caller configured on that instance
      survives, or switches the wrapper to the hub's own when the hub already holds one connected.
    - Two things it exposed are fixed with it. `ConnectedDevices` listed drivers that had gone down, and
      every run now leaves two (`Finalise` disconnects the mount and the guider), so the profile-switch
      gate named a parked mount as connected. And a fake camera's coupling was keyed on the hub holding a
      mount, which initialisation now always arranges, so `coupleCameraToMount: false` is a flag on the
      camera's own driver (`FakeCameraDriver.CouplesToMount`).
    - The mount's site on connect, for every host, followed on top of this (#798).
    - What it left, both older than it, is fixed by #806: the hub runs one connect, adoption or
      disconnect of a device at a time, so two at once leave one driver, and reconnects a driver that
      dropped, the same instance, while its URI is unchanged.
12. **A finished session is never cleared**, so the next `/session/start` answers 409 until an
    `/abort`, which disconnects the rig (item 3). And the start is check-then-set (an
    `Interlocked.Exchange`, not a compare-and-swap), so two starts can race.
13. **A prompt can hold for ever.** Liveness is "a socket registered in `EventHub`", and a ninaAPI v2
    socket (Touch N Stars) counts as an observer although v2 has no prompt route, so it holds every
    prompt indefinitely. A prompt the session cancelled is never cleared from `_pendingPrompt`. And
    `EventBroadcaster` attaches to a new session by 1 s polling, so events of a run's first second are
    lost and a prompt raised then gets the unattended answer while a client is watching. **FIXED**,
    all three:
    - Only a native socket counts as an observer (`EventHub.PromptObserverCount`).
    - The session withdraws a prompt it stops waiting on, and whoever holds a prompt drops it once it
      settles (`SessionPromptEventArgs.Settled`): the node's `/session/state`, the mirror, and the GUI's
      flat-run prompt bar.
    - The broadcaster attaches as the node starts a run (`HostedSession.RunStarting`), before the run's
      body is released.

    The node's test hosts ran on the fake auto-advancing clock, whose `SleepAsync` made the broadcaster's
    1 s poll spin, and that hid the third bug: attaching from a spinning poll is instant. They run on a
    real clock now, and the two hosting test classes went from 6.2 s to 4.5 s with the spin gone.
14. **WITHDRAWN (2026-09-25): not a bug.** This item called `/session/flats`'s hard-set
    `UnattendedPromptResponse = Proceed` a breach of "an unanswered prompt is declined". That rule is
    the SCHEDULED run's. An operator-invoked flat run opts into Proceed on purpose, as
    `flat-frame-automation.md` (the prompt channel) and the enum's own documentation say. A human asked
    for the run and may have switched the panel on before walking back inside, and flats have a
    backstop, since metering fails when the panel is off. The review cited that doc and missed its
    exception.
15. **The preview encoder breaks two rules** (FIXED, with two findings this list did not have). It
    rendered with a literal `StretchMode.Linked` (CLAUDE.md: every renderer resolves through `Auto`,
    headless included), and the per-OTA preview read the session's `LastCapturedImages` WITHOUT a lease
    (the guider preview leases). It also encoded the whole frame before comparing the change token, so
    every 500 ms poll cost a full encode per OTA.
    - **A lease alone would have blanked the preview.** The imaging loop released a frame once its FITS
      write was done (autofocus once it had counted the stars) and left the slot pointing at the
      released frame for the rest of the exposure, so a lease succeeded only in the second or so after a
      frame landed; and rough focus never released its frames at all. Every publisher now goes through
      `Session.PublishCapturedImage`: the slot takes a lease of its own and releases the frame it
      replaces, so a frame stays readable until the next one lands. The price is one camera array per
      OTA (the memory table under "What crosses the socket").
    - **The token named the wrong frame.** `X-Frame-Number` was `CameraExposureState.FrameNumber`, which
      advances as an exposure STARTS and restarts at every target: the preview served the previous frame
      under the new number, then skipped the new frame when it landed, and so ran a whole sub behind. The
      token is now the slot's own (`ISessionTelemetry.LastCapturedImageNumber`), and it is a conditional
      GET: `If-None-Match` naming the current frame is answered 304 before anything is leased or encoded.
16. **`SCOUT-COMPLETED` never serialises**: `StarCountsPerOTA` is an `int[]` and no `int[]` is reachable
    from `HostingJsonContext`, so the event is dropped where `EventBroadcaster` swallows the failure.
    Add a test that serialises every event type the broadcaster sends. **FIXED**, and wider than stated:
    five of the ten events never serialised on the ninaAPI socket either. Both contexts now register the
    value types a payload holds, every event is built in one place (`BroadcastEvents`), and
    `BroadcastEventSerializationTests` sends each through both contexts.
17. **`/devices/discover` runs a full discovery inline on the request token** and returns only display
    strings. The client's 10 s control budget cuts it off mid-probe, and a dropped request cancels a
    serial probe half-way. It becomes a job (P1's long-operation model). **FIXED** by #916, with the job model
    pulled forward from P1 for it: `NodeJobs` runs a job on the node's token, `POST /devices/discover`
    answers 202 with the job (a second start joins the running one), `GET /jobs/{id}` is authoritative,
    `DELETE /jobs/{id}` cancels, and `JOB-PROGRESS` is pushed; `TianWenNodeClient` drives all of it.
    `NodeJobTests` failed first on every one, and each is pinned: running the discovery on the request's
    token, starting a second, dropping the cancel or the push each turns a test red.
18. **The ninaAPI shim and the profile endpoints skip their own gates.** `GET /v2/api/profile/switch`
    switches the active profile ungated (the native `PUT /session/profile` asks `ProfileSwitchGate`),
    and `DELETE /profiles/{id}` deletes a profile without asking whether it is active or running.
    **FIXED**: the ninaAPI switch asks `ProfileSwitchGate`, and a delete refuses the node's active
    profile and, while a run is going, any profile. The node did not record which profile a run was
    started from, so it could not tell the one in use from the rest; refusing all of them for the length
    of a run was the safe answer. It never needed to: a start makes its profile the active one, and
    `ProfileSwitchGate` keeps it active until the run ends, so refusing the active profile protects the
    run's. Since P3 part 3 a delete refuses that one only.

## P0c: bugs in the hosts that the split would carry over (#788; review, 2026-09-25)

Present-day bugs outside the server. Each would reappear inside the server or in the cut, so they go
first; none needs a decision.

1. **Quitting the TUI hangs.** Q or Ctrl+C breaks the input loop, then `tracker.DrainAsync()` awaits
   every background task WITHOUT cancelling it. The TUI's `MountLimitWatcher` loops until its token is
   cancelled, and nothing in `TianWen.Cli` calls `Cancel`, so the alternate screen freezes; Ctrl+C is
   swallowed (`TreatControlCAsInput` is reset only at the very end); a running session is awaited, not
   aborted; and were the drain ever to return, disposing the host would force-disconnect every device
   cold. The TUI gets the GUI's quit rule (cancel, warm, disconnect, bounded), which P6 then replaces
   with the quit dialog for both. **FIXED** by #910: the rule is one class both hosts drive, `AppQuit`
   (UI.Abstractions), moved out of the GUI's `Program.cs`. The TUI runs its background work on a token of
   its own, the loop shows the rig stopping, and a second quit is refused rather than frozen out. Seen
   live before the fix (the TUI still running minutes after Q, its screen gone) and after (it exits at
   once; the GUI's Esc-twice quit, on the same class, too); `AppQuitTests` pins the rule.
2. **Polar alignment and planetary capture take no device lease.** Only `Session.RunAsync` and
   `RunFlatsOnlyAsync` acquire one, and the gate is lease-only, so nothing stops a jog or a second run
   from moving a mount polar is rotating, or reconfiguring a camera planetary is streaming, although
   `AppSignalHandler` assumes otherwise. Two smaller holes: the preview Capture button stays live
   during a flat run, and `WarmAndDisconnectAsync` ramps a LEASED camera's cooler before the disconnect
   is refused. Both run kinds claim their devices through `DeviceLeaseSet`, at the Lib level, so the
   server's P5 inherits the claim instead of re-inventing it. **FIXED** by #912: the start handlers claim through
   `DeviceLeaseSet.TryAcquire` (new, a refusal is a verdict) and each run owns its claim:
   `PolarAlignmentSession` (the mount plus what its capture source drives, released after the mount
   restore) and `PlanetaryCaptureController` (the camera only, since its own nudges drive the mount through
   the gate, released when its loop ends). The preview capture asks the camera's claim, and both warm-ups
   refuse a claimed camera before the ramp unless forced. `RunClaimTests` failed first on all six claims.
3. **The AppData write is atomic within a process, not across two.** `IExternal.AtomicWriteAsync`
   writes a FIXED `<file>.tmp` with `FileShare.None` and then `File.Move(overwrite)`: two processes
   writing one file collide on the temp name, and on Windows the move fails while another process holds
   the target open without `FileShare.Delete`, which both readers (`ProfileIterator`, `IExternal`) do.
   The server's limit watcher re-reads every profile every 5 s, so after the split this is routine. The
   fix: a unique temp name (as `ObjectPictureStore` already uses), readers that open with
   `FileShare.ReadWrite | FileShare.Delete`, and a bounded retry on a sharing violation.
   `SmallBodies/apparitions.json` is a whole-snapshot overwrite from every host (GUI, CLI, server,
   viewer, MCP), so one process drops another's fetched entries; it merges on write. Files with two
   writers after the split: profiles (until P3 makes the server the only writer), the comet caches, and,
   once the GUI and the TUI are both clients, planner, session, remote-rig bindings and the weather
   cache. **FIXED** by #914: one primitive, `SharedFile` (`TianWen.Lib/IO`), under `IExternal`'s atomic write, its
   JSON read and the profile and backlash readers. A write stages under a name of its own, a read shares
   read, write and delete, and a sharing violation is retried for a bounded time. **Delete sharing alone
   does not let the write through, which this item assumed**: `MoveFileEx`, under `File.Move`, refuses to
   replace a file ANY handle holds open. On Windows the replace is therefore the POSIX-semantics rename
   (`SetFileInformationByHandle`, `FileRenameInfoEx`, Windows 10 1709 and later, falling back to
   `File.Move` where the file system has none): the name moves at once and a reader holding the old file
   keeps reading it, as on Unix. A file every host adds to goes through `IExternal.UpdateJsonAsync`, which
   holds its directory's lock (`.lock`) across the read, the merge and the write, and the apparition cache
   merges there, newest fetch winning per comet; a lock waiter polls every few milliseconds for up to
   30 s rather than backing off, since a holder that updates again takes the lock straight back (a
   backed-off waiter starved and gave up, measured). **A replace on NTFS leaves the name absent for a
   moment**, with either rename (0.1 to 0.3 ms at rest, about one query in 25,000 beside two writers, and
   longer under load), so a single `File.Exists` or listing can call a file that exists gone, and a planner
   that loaded "no pins" would save that back. A reader believes absence only after looks spread over about
   130 ms (`SharedFile.TryOpenReadAsync`, and `ListAsync` for a file the last listing had; Windows only, a
   Unix rename being atomic). One look 5 ms later still missed once in 60 runs. **A lock between every
   reader and writer would make that exact, and measured, it wedges**: the real-time scanner holds a freshly
   replaced file in kernel mode (the System process, which a share check does not see), and with each
   replace taking the lock that hold stopped clearing, so every rename onto the file was refused for
   minutes. That happened in half the runs with readers locking too, and in 2 of 30 with only writers
   locking, against none in about 150 runs without the lock. The credential store's temp name is its own
   too. `SharedAppDataFileTests` failed first (two writers of one profile collided on the temp name) and
   `CometRepositoryTests` too (a host's write dropped another's upgrade). Every deterministic part of the
   fix is pinned: taking out the unique name, the reader's delete sharing, the POSIX rename, the retry,
   the lock or the merge turns a test red.
4. **On Linux, where lights land depends on the working directory.** `ImageOutputFolder` calls
   `GetFolderPath(MyPictures)` without the create option, so on a box with no `~/Pictures` it resolves
   to a relative `TianWen` folder, and a spawned server's working directory would decide where a
   night's subs go. It resolves to an absolute path (created if missing), and the spawner sets the
   child's working directory anyway (P1). **FIXED** by #915: `SpecialFolderHelper.ResolveAppSubFolder` asks for the
   special folder WITH the create option and takes it only as a full path, falling back to
   `<AppData>/Images` where there is none to create (a service account with no home). `ImageOutputFolderTests`
   failed first on both, with the folder lookup injected, since the machine running the test has its own
   answer.

Found on the way and filed as issues of their own, since the split does not touch them: the neural
guider saves its model with a plain `WriteAllBytesAsync`, so a crash mid-save leaves a truncated file
as the newest, which the loader deletes without falling back to the older good one (#786; its file
name comes from `HashCode.Combine`, randomised per process, which is harmless only because the loader
takes the newest file by date, not by name); and tianwen-mcp's instructions advertise `devices.*` and
`profile.*` tools it does not register (#787).

## Target architecture

```
tianwen-gui ───┐                             ┌─ IDeviceHub (the only one), leases, drivers
tianwen (TUI) ─┤                             │  session / flats / polar / planetary / darks runs
tianwen <cmd> ─┼─ local socket (per user) ───┤  MountLimitWatcher, discovery, profiles (one writer)
LAN clients ───┘  TCP :1888 (a shared rig)   └─ crash journal; restarted by its keeper
```

Any number of these are attached at once, as peers ("Clients are peers", above).

### Transport: a Unix domain socket, named on the command line

The server listens on a Unix domain socket, which Windows 10 (1803+), Linux and macOS all support. The
GUI's client connects through `SocketsHttpHandler.ConnectCallback`, and the WebSocket through
`ClientWebSocket.ConnectAsync(uri, invoker)`. The protocol is today's native v1, so local and remote are
one API, one client and one test surface.

**Spike, 2026-09-24, win-arm64 Windows 11 26200, NOT elevated.** Kestrel `ListenUnixSocket` on
`%LOCALAPPDATA%\TianWen\spike.sock` (58 chars) served a GET and a WebSocket echo through that client
code. A warm 16 MB body came back in 12 ms. No elevation, no port, no firewall prompt, nothing reachable
from the LAN. **It is AOT-clean**: the same spike published with native AOT (`-r win-arm64`, a 13.3 MB
executable) with no trim or AOT warnings, and the native binary ran the GET, the WebSocket echo and the
16 MB body (8 ms). P1 still verifies the real server by `publish`, per the repo rule.

- **The path is an argument**: `tianwen-server --socket <path>`. The default comes from ONE shared
  helper in `TianWen.Hosting.Contracts`, a short name under the per-user AppData root the apps already
  use (`SharedStaticData.CommonDataRoot`: `%LOCALAPPDATA%\TianWen`, `$XDG_DATA_HOME/TianWen` or
  `~/.local/share/TianWen`, `~/Library/Application Support/TianWen`), so a restarted GUI finds a server
  that outlived the old one. A test or dev run passes its own path and gets an isolated node. **Not
  `$XDG_RUNTIME_DIR`**, as first drafted: it is removed at logout, and the crash journal beside the lock
  must survive a reboot for its stale-journal guard to see it.
- **A client can be pointed at a node instead of spawning one**: `--node-socket <path>` (and the
  `TIANWEN_NODE_SOCKET` variable) make the GUI, the TUI or a CLI command connect to that socket and
  never spawn. That is how a developer runs the server under the debugger first and attaches a GUI to
  it, and how a test gives a client its own node.
- **Access control is the directory's ACL**: the per-user AppData tree admits that user, SYSTEM and
  administrators. Nothing else authenticates, and LAN trust is unchanged for TCP.
- **Length**: `sun_path` holds 108 bytes on Linux and Windows and 104 on macOS, whose root alone is 44
  bytes plus the user name. The helper checks the length and fails with a message naming the path
  rather than binding something truncated.
- **The socket file outlives a crash**, and a bind onto it fails. Clean-up is gated by the lock below,
  never by probe-then-delete: two servers probing at once would both delete, and the loser would unlink
  the winner's live socket, leaving it running and unreachable.

### One server per user: the lock file is the gate

A server opens `node.lock` beside the socket with `FileShare.None` and holds it for its lifetime. That
maps to `flock(LOCK_EX)` on Unix, and the OS drops it on process death. Holding the lock is what entitles
a server to delete a stale socket and bind. A second server that cannot take the lock exits at once with
"a node is already running" and its pid, which it reads from the lock file's contents.

**Every server takes the lock and binds the default socket, however it was started**, unless `--socket`
names another. So a server the user started by hand IS the local node, and a GUI finds it rather than
spawning a second one onto the same hardware. Today's server has neither, binds `0.0.0.0:1888`
unconditionally and announces itself, so a spawned and a hand-run server would collide on the port and
share `lan-node-id.txt`; the review found both.

**A server under ANOTHER account** (a service on the NUC) is invisible to the per-user lock. So before
spawning, a client asks `GET /api/v1/node` on `localhost:1888`; if a TianWen node answers, it becomes
the Local rig and nothing is spawned. Profile edits then need that machine's own socket (decision 4).

`InstanceGate` in AppShell makes its pipe the lock AND the transport. Here the transport is a socket on
every OS (Kestrel's named-pipe transport exists only on Windows, and one transport everywhere is the
point), and a socket file cannot be the lock, as the race above shows. So the lock is a separate file,
held for the server's whole life. (`InstanceGate`'s own exclusivity is tested only within one process,
and off Windows it rides the runtime's socket emulation of named pipes; nothing here leans on it.)

### Spawn and lifetime: the server outlives the GUI

`AscomHostProcess` is the right shape for a spawn (the parent passes the transport name as an argument,
then waits for the connection) and the WRONG lifetime: its helper sits in a kill-on-close job. The
hardware server must survive its parent:

- **Windows**: `CreateProcessW` with `CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB` (a LibraryImport,
  since `Process.Start` exposes no creation flags). A job that forbids breakaway, such as some launchers
  and debuggers, fails that call; fall back to a plain spawn and warn that the server will die with that
  job. **Spike this under `tools/start-app.ps1`, Visual Studio and the MSIX-less release build** once a
  client starts its node (P6): the launcher is built and tested in P1 (`DetachedProcess`,
  `LocalNodeLauncher`), and no client calls it before the cut.
- **Linux / macOS**: the child calls `setsid()` ITSELF at startup (a LibraryImport; a child of
  `Process.Start` is never a process-group leader, so it succeeds), so a closed terminal or a killed GUI
  session does not take the server with it. Not `posix_spawn` with `POSIX_SPAWN_SETSID`, as first
  drafted: `posix_spawnattr_t` is opaque on macOS and a 336-byte struct on glibc, which a portable
  LibraryImport would have to guess at.
- **Stdio and working directory**: a spawned server logs to its file only. Its console logger goes (a
  TUI-spawned child would write over the TUI's alternate screen), it reopens stdin, stdout and stderr on
  the null device, and nothing redirects its stdio into a pipe nobody drains (a full pipe blocks the
  child: the shape `AscomHostProcess` has today). Its working directory is the AppData root, never the
  client's.
- **Readiness**: connect to the socket with a bound, then `GET /api/v1/node`, which is NEW (P1; the review
  found no node endpoint and no wire version anywhere, only the LAN beacon carrying the id): the node's
  id, its version, its wire version, its pid, whether it is shared, and how many clients are attached. A
  failed spawn reports the server's log, since there is no stderr to read.
- **A keeper restarts a server that crashes** (decision 10). A client spawns `tianwen-server --keeper`,
  which starts the real server (the same binary; the breakaway was the keeper's), waits on it, and starts
  it again after an abnormal exit; a clean exit ends the keeper too. The journal's crash-loop guard is the
  keeper's as well: two crashes within a few minutes and it stops, leaving the journal for the next
  client to show. The keeper holds no hardware and does nothing but wait, which is why it outlives what
  it guards.
- **When the server exits by itself**: never while it holds hardware, meaning any connected device, any
  run, or any warm-up in progress. Otherwise a spawned server stays until logoff (decision 2), and one
  started by hand never exits by itself. `POST /api/v1/node/shutdown` (socket only) performs the safe
  stop from P0b item 4, then exits; "Stop the rig and quit" (decision 1) calls it, only from the last
  client attached.
- **Version skew**: the GUI and the server ship together, but an older server may still be running from
  before an update (or a newer one, after a downgrade). The handshake compares wire versions. An idle
  server of another version is asked to exit and a new one spawned. A busy one is left alone: the client
  says a node of another version is running a session, connects read-only through the mirror, and offers
  to restart the node once the run ends. **On Windows a running server's executable and native
  libraries are locked**, so extracting a release over the folder a node runs from fails part-way: the
  release notes say to stop the rig first, or to extract into a new folder, which the handshake handles.
- **One clock**: `TIANWEN_NOW` is frozen at EACH process's start, so a GUI and a server launched seconds
  apart disagree by the launch gap. The GUI passes its frozen offset to the child explicitly, which
  needs a new PUBLIC `StartupTimeOverride` input: today `TryParse` and `OffsetTimeProvider` are internal
  and only an absolute instant is read, so an inherited `TIANWEN_NOW` would re-anchor the child at its
  own start. A server started by hand anchors itself as today.
- **LAN exposure** (decision 3): a spawned server listens on the socket only and announces nothing until
  "Share this rig on the LAN" is on. That is a MACHINE setting, kept by the node in AppData (not per
  profile, not per session) and read at every start. While it is on, the node binds TCP 1888, announces
  itself on the LAN, and registers itself to start at logon: `HKCU\...\Run` on Windows, an XDG
  autostart entry on Linux, a LaunchAgent on macOS. Turning it off removes all three. It is changed only
  over the local socket (decision 4's rule). A server run by hand on a mini PC keeps TCP 1888 by
  default. So the user's NUC, which logs on by itself, has its rig reachable from the laptop after any
  reboot with nobody logged in over RDP.
- **Pinned serial ports**: a profile scan must never probe a pinned port, and today only the GUI
  registers the provider that knows them (from its own active profile). The server's active profile is
  in memory, null at start and not persisted; it becomes node state, persisted, set over the socket and
  gated by `ProfileSwitchGate`, and the provider reads it.
- **Logs**: `Logs/<date>/Server_*.log`, beside the GUI's. The day folder is fixed at process start
  today, so a server running overnight writes into the evening's folder; it rolls at local midnight.
  CLAUDE.md's "ground truth for fine telemetry is `GUI_*.log`" moves to `Server_*.log` at the cut.

### When the server dies

The socket connection is gone at once: the GUI's WebSocket closes and every request is refused. What
else happens, and what the plan does about each:

- **The hardware is released, not stopped.** The OS closes the process's USB and serial handles. A mount
  controller keeps tracking on its own, but **nothing enforces the mount limits any more**, and that
  is the dangerous part. Whether a cooled camera keeps its setpoint without a host is per camera and
  unverified; #785 asks the bench. The run is lost.
- **The socket FILE stays on disk.** It is stale until the next server takes the lock and replaces it
  (the lock rule above). A stream's shared-memory section (P4b, shipped for the streams) behaves per OS:
  - Windows keeps a section alive while the client still maps it; the client's stream ends with the node's socket, and
    its mapping with the stream.
  - A file under `/dev/shm` persists until it is unlinked, so the new lock holder removes every file under the node's
    prefix (`AddNodeSharedMemory`).
- **The keeper starts a new server at once** (decision 10), and every client notices the socket drop,
  says so in plain words, and reconnects. Nobody is asked first: the rule "nobody manages the server"
  holds here too. Only a server spawned plainly, with no keeper (the fallback), is restarted by the next
  client instead.
- **A crash journal (DECIDED 2026-09-24, decision 8).** The server keeps a small file beside
  `node.lock`, in the AppData root, which survives a reboot, written atomically whenever it changes, of
  what it holds:
  - the connected devices;
  - the run in progress (kind, profile, target, start time);
  - each camera's cooler INTENT, set out below.

  A clean exit deletes it, so a server that starts and finds one knows the last one died. It then:
    - reconnects those devices at once, which restores mount-limit enforcement;
    - **re-establishes each camera's cooling from its recorded intent**:
      - cooling to a setpoint is re-applied through the same cool-down the session uses
        (`CoolCamerasToSetpointAsync`), never as a jump;
      - a warm-up in progress continues its ramp from the camera's CURRENT temperature, so a camera
        that was being warmed is not cooled back down to the old setpoint;
      - a cooler that was off stays off;
    - tells the GUI which run was interrupted and when.
  - The GUI offers "Stop the rig safely" (park if configured, warm-up, disconnect) or "Start the session
    again".
  - **It never resumes a session silently**: a mount that has kept tracking for unknown minutes is
    exactly where a blind resume goes wrong.
  - **Two guards:**
    - **A crash loop.** The journal counts restarts. If a DRIVER crashed the server, reconnecting it
      crashes the next one too, so after two crashes within a few minutes the server stops
      reconnecting and names the device it was touching when it died.
    - **A stale journal.** A journal older than the machine's last boot (a power cut) describes a rig
      whose state nobody knows, so it is shown for information and not acted on. The boot time comes
      from the OS (`/proc/stat`'s `btime` on Linux, `kern.boottime` on macOS, the system boot time on
      Windows), never from `Environment.TickCount64`, which stops during suspend on Linux and runs on
      through a Windows Fast Startup shutdown.
  - Without the journal, a server crash is today's GUI crash: the mount runs unguarded until someone
    notices.
  - **Shipped in P1 part 7a (#925):** the journal and its report (docs/architecture/hosting-api.md, "A node that dies leaves
    a crash journal"). Two things the design above did not say: a camera's cooler INTENT is recorded where the cooler
    is commanded (the hub keeps it), since mid-ramp the camera's own setpoint is a step; and the keeper names the node
    that crashed (`--after-crash <pid>`), since it alone can vouch that no boot came between. On Windows the boot is
    the newest Kernel-Boot event 27: the tick count, `LastBootUpTime` and the registry's shutdown time all missed a
    Fast Startup boot on the desktop that measured it.
  - **Shipped in P1 part 7b (#926):** acting on it. The cool-down is the session's own, lifted out of `Session` into
    `CameraCoolingRamp` so a node can drive it with no session; a recovery's ramps end when a run starts and as the
    host starts to stop. The device being reconnected is written to the journal BEFORE its connect, which is the only
    way "the device it was touching when it died" can be known after a crash inside a driver.

### What crosses the socket

The rule that decides every row below: **an operation that must finish if the GUI dies runs in the
server as ONE request, never as a sequence of driver calls the GUI drives.** A warm-up ramp driven from
the GUI stops when the GUI does; one that runs in the server finishes.

| GUI feature today (survey, `AppSignalHandler.*`) | Over the socket | Server work |
|---|---|---|
| Equipment connect / disconnect / warm and disconnect | `POST /devices/{key}/connect\|disconnect\|warm-and-disconnect`, each a JOB (below) | new; the ramp runs server-side, with the cooler safety check the GUI does today (`GetDisconnectSafetyAsync`; Alpaca `Connected=false` skips it) |
| Camera cooling, gain, offset, bin, ROI | device-plane settings | new |
| Camera, focuser, filter, mount telemetry (per-tab polls: camera 2 s, OTA 2 s or 1 s moving, mount 0.5 s slewing / 1 s settle / 10 s tracking / 2 s idle) | a device SNAPSHOT endpoint, authoritative, plus a `DEVICE-STATE` push on change (the GUI's `hub.DeviceStateChanged` today) | the server polls at those cadences while any client is attached, slower with none |
| Preview exposure, snapshot, plate solve, solve and sync | one job each | new; solve and sync is expose, solve and sync as one job |
| Focuser jog / goto, mount goto, the planetary nudge, solve and sync | device-plane actions, every one through `DeviceOwnershipGate`. The GUI has NO filter change, park, tracking or move-axis command of its own (the review); the API keeps them for other clients | partly exists, session-scoped only |
| Mount move-axis (the ninaAPI shim has one; polar moves the axis inside its own run, P5) | start carries a LEASE the client must renew; the axis stops when it lapses or the connection drops | new. Safer than today: a client that dies mid-move today leaves the axis running |
| Discovery, profile edits, site reconcile, sensor capture, credential store | a discovery JOB; profile edit endpoints on the socket only, taking and returning the WHOLE profile (today's `ProfileDetailDto` drops the guider focuser, the OAG OTA, mount limits, the site tie-breaker, focus direction and the sensor geometry smart framing needs); a `PROFILE-CHANGED` push | new; **the server becomes the only profile writer**. The GUI has 12 persisting write sites (21 entry points), one of which (reconcile-all) rewrites EVERY profile, and the session-end backlash mirror into the focuser URIs, which the server has no counterpart for today |
| Device ownership (the GUI's gates read its OWN hub) | the lease table crosses: who holds each device, for which run | new. Removing the hub silently OPENS both gates (`DeviceOwnershipGate(null)` allows everything, `ProfileSwitchGate(null, ...)` sees no devices), so the GUI's gates are re-sourced from this before the cut |
| Session start / abort / prompts / flats | exists (`/session/*`) but cannot carry a night yet (P0b 1-4 and 10-14); the WHOLE `SessionConfiguration` crosses (P0b 10, P5b); prompts go to every client and the first answer wins | P0b, then P5b |
| Notifications (the GUI's feed, the Home card's last note) | a `NOTIFICATION` push merged into the client's feed, plus the node's own ring on connect | exists server-side; nothing in the GUI consumes it (P5b) |
| Full-resolution frames: preview, subs, guide frames, polar refine | linear frames, P4; a saved sub is its FITS file, read locally through `FitsReader` (#755, shipped) | new |
| Polar alignment, planetary capture with its rolling stack, a dark library | server run kinds, P5 | new |
| Mount limit verdict (the GUI reads `MountLimitWatcher.VerdictFor` every frame) | session-less telemetry field | new |
| Device commands from a terminal ("the CLI makes it possible to warm up a camera from the command line", user, 2026-09-25) | the same device-plane jobs the Equipment tab uses, behind `tianwen device connect\|disconnect\|warm\|park\|status` and the existing `darks`, `flats` and `profile` verbs | new client code only, once P2 exists |
| AI enhance (the viewer's Enhance) | stays CLIENT-side: the GUI and the viewer enhance in their own process, as today. The node keeps `/image/enhance` for other clients (user, 2026-09-25) | it answers 409 while the node holds any device or run (decision 12), so no GPU work runs in the process that owns the rig |

**Slow operations are JOBS, one model for all of them.** Connect (a slow ASCOM driver takes 30 s and
more), warm and disconnect (a ramp of up to 15 minutes), discovery (minutes over a serial sweep), a
preview exposure, solve and sync, and a focuser or mount move each start with a request that answers
202 and a job id, then finish in the server: `GET /jobs/{id}` is authoritative, a `JOB-PROGRESS` push is
the latency hint, and `DELETE /jobs/{id}` cancels. It generalises the enhance endpoint's single-flight
job (`server-enhance-job-model.md`) instead of adding a variant per endpoint, and it is what lets every
request keep its short budget. Discovery was the first (P0b 17): it ran inline on a 10 s client budget and
was cut off mid-probe, and it is a job now (`NodeJobs`, `/api/v1/jobs`); the other slow operations join
it in P2.

**A prompt is held only while a client can SEE it.** Liveness today is "a socket is registered"
(P0b 13), and a frozen window keeps its socket open, so a GPU wedge that freezes rather than crashes
would hold a prompt, and with it the night, for ever. So each client beats from its FRAME loop, never
from its socket's own thread (the GUI from `SdlEventLoop.OnLoopIteration`, public since SdlVulkan.Renderer
7.50, since `OnPostFrame` runs only after a drawn frame and an idle window draws none; the TUI from its
main loop); a client whose beat lapses for a few seconds stops counting as an observer, and a prompt with
no client that can see it gets the session's unattended answer, exactly as with none attached. Shipped:
`NodeWire.PresenceBeat` / `PresenceLapse`, `EventHub.RecordBeat`, `TianWenEventStream.Beat`,
`AppSignalHandler.BeatRemoteRigs` (docs/architecture/hosting-api.md, invariant 2). Every client sees every
prompt and the first answer wins; the others see it resolved.

**Frames are linear and binary, never the preview JPEG.** The viewer's stretch, statistics, star
profile, plate solve and snapshot save all assume linear floats in memory, and a camera buffer
(`ChannelBuffer`) already is one: a `float[,]` per channel.
- **What exists today, and why none of it serves the viewer** (checked 2026-09-24):
  - **The preview JPEG** (`/preview/{ota}`, `/preview/guider`) is stretched, 8-bit, downscaled and
    session-only. `RemoteSessionMirror` can fetch it, but nothing in the GUI sets `Previews`.
  - **Alpaca `imagearray`** (the device plane only; native v1 never uses it) is ImageBytes. Our writer
    sends channel 0 only, as Int32. The protocol mandates column-major order, so both ends transpose,
    and it carries no `ImageMeta`. It answers only for a hub-connected camera with an image ready. The
    protocol would allow UInt16, Single and colour planes, so channel 0 and Int32 are our choice, but
    the transpose and the missing metadata are the protocol's. It stays as it is, for third-party
    Alpaca clients.
  - **`SessionStateDto.LastFramePath`** names the last sub the server wrote. A local GUI could open it
    as is, linear and with its headers, but only saved subs have one; previews, polar and planetary
    frames never touch disk. A remote client cannot open it at all: it is a path on the node, and no
    endpoint serves the file.
- **Where the 16-bit to float conversion happens: in the camera driver, so in the server, unchanged.**
  - The DAL driver (ZWO, QHY, Player One, ToupTek) converts right after `GetDataAfterExposure` fills its
    reused native buffer. Alpaca and ASCOM convert as they decode.
  - The server needs floats itself, for star detection, HFD, plate solving, guiding and writing the FITS,
    so the frame is float before any client exists, and a slot carries it as float.
  - Moving the conversion to the client would halve a slot but make the server convert twice.
- **The DAL conversion read 16-bit pixels as SIGNED, found while tracing this, and FIXED in #349** ("fix(dal): a
  16-bit camera's pixels are read unsigned, in place, in one pass").
  - A pixel of 32768 or more became a large negative float. That is every bright star core on a 16-bit
    converter (ASI2600, ASI6200, QHY268, QHY600) and on Player One's left-aligned 12-bit data.
  - The read also went through a NEW 52 MB `short[]` per 26 MP frame.
  - `RawPixelConversion.WidenToSingle` now reads the SDK buffer in place, unsigned, in one fused vector
    pass. Measured 26 MP, win-arm64: 3.5 ms and no allocation, against 34.5 ms and 52 MB.
  - #653 asks a real 16-bit camera to confirm.
- **Wire format.** The float planes plus an `ImageMeta` header, bit-exact, row-major, so neither end
  transposes. When every sample is a
  whole number in 0 to 65535, which is the usual case for a camera frame in ADU, the planes are packed
  as 16-bit instead. That halves the bytes and is still lossless; the packer checks every sample as it
  writes and falls back to float on the first one that is not.
- **Rate.** At the spike's rate, a 26 MP frame is about 40 ms packed (52 MB) or 80 ms as float
  (104 MB), extrapolated. **Measured in P4 part 2** (Release, this desktop, loaded): 37 to 38 ms packed and 37 to 42 ms
  as float, the local socket's copy being cheap enough that the bytes barely count
  (`docs/architecture/hosting-api.md`, "Linear frames on the wire"). Measure a real frame before designing anything faster. The reader copies the
  body straight into a recycled `float[,]` (viewed flat as bytes), never through a buffered array, or
  every frame is large-object-heap garbage in the client.
- **Compression, measured 2026-09-25** (user: "see if we can use lzip or gzip for the frames over the
  wire"). Three real 9 MP 16-bit OSC subs from the test data (ASI533MC Pro at 60 s and 120 s, SV605CC at
  60 s; 18.1 MB each as uint16), .NET 10 on the 16-core x64 desktop, median of three, each codec on the
  preparation that suited it best (byte-shuffled planes, a same-colour delta, the zero low bits shifted
  out):

  | Codec | Ratio | Compress | Decompress |
  |---|---|---|---|
  | deflate (gzip) fastest, 8 bands in parallel | 1.3 to 2.1x | ~430 MB/s | 400 to 1700 MB/s |
  | Brotli quality 1 | 1.6 to 2.45x | 185 to 280 MB/s | 160 to 290 MB/s |
  | deflate optimal | 1.5 to 2.5x | ~30 MB/s | ~350 MB/s |
  | lzip (LZMA), level 0 to 6 | 1.7 to 2.5x | 1 to 4 MB/s | 25 to 45 MB/s |

  A sky frame's noise floor does not compress, so no lossless codec gets much past 2x; ZWO's 14-bit data
  scaled to 16 bits carries two zero low bits, which shifting out buys a little. So **the local socket
  never compresses**: a copy moves the 18 MB in about 14 ms, less than any codec needs just to compress
  it. **Over TCP a client asks for it** through `Accept-Encoding` (Brotli q1, or banded deflate), which
  pays on WiFi or 100 Mbit (a 26 MP frame about 4.7 s raw against 2.6 s) and gains little on gigabit.
  **lzip is out for live frames** (5 to 25 s to compress one) and stays an archive format. And a saved
  frame travels as the bytes on disk, since a `.fits.gz` is gzip already. Rice (fpack), the astronomy
  standard for integer images, is the one candidate not measured, as nothing in the stack encodes it.
- **Fetching.** `GET /frames/{source}/latest?after=N` answers only when frame N+1 exists, and a
  `FRAME-AVAILABLE` push tells the client when to ask. The same endpoint serves a remote rig over TCP,
  so remote rigs get linear frames too. A SAVED sub is fetched as its file instead (see "A saved
  frame is its FITS file" below).
- **Shared memory for a client on the same machine (P4b), DEFERRED past 10.0** (decision 11). 10.0
  carries local frames over the socket into recycled buffers; P4b waits for a measurement on a real
  26 MP frame showing that the socket costs a local client something it notices (polar refinement's
  rate, a planetary live view dropping frames). The design below stands for when it does. Measured
  2026-09-24 on the same box: a 26 MP
  float frame (104 MB) through a named, pagefile-backed map, opened a second time by name as a client
  process would, took 4.5 ms to write and 4.5 ms to read in the steady state (the first frame, while
  its pages fault in, 32 ms and 43 ms). That is about 9 ms against about 80 ms over the socket, and two
  memory copies instead of HTTP framing and kernel copies. Every read matched and none was torn. The
  design:
  - **One message, two carriers.** `FRAME-AVAILABLE` carries the frame's `ImageMeta` and where its
    pixels are. For a client on the local socket that is a shared-memory slot (map, slot, generation);
    for a TCP client it is the byte endpoint above. The client picks by transport, so a remote rig
    and the local GUI share one code path above the carrier.
  - **A frame the server SAVES needs no slot: the file is the shared memory** (user, 2026-09-24).
    - Right after the server writes a sub, its bytes are in the OS page cache, which every process
      shares. The announcement then names the file, and a local client reads it from there.
    - That memory is reclaimable, where a pagefile-backed slot is commit. It holds the sensor's own
      16 bits, half a float slot, and the server's copy into a slot disappears, since the FITS write it
      does anyway takes its place.
    - **The reader SHIPPED (#755, FITS.Lib 6.2's `FitsReader`, "perf(fits): a plain FITS file is read
      straight into its planes, through FitsReader"), as positional reads rather than the memory
      mapping first proposed, which measured slower than the old reader from disk.** 2 MB positional
      reads, each band decoded straight into the float plane: a 26 MP 16-bit sub, pooled, went from
      39.5 ms and 54.3 MB to 10.5 ms and 56 KB from the page cache (75 to 62 ms from disk; win-arm64,
      Release). So 10.0's saved frame is read through `Image.TryReadFitsFile` as it stands, and
      "the file is the shared memory" holds without a mapping.
    - Slots therefore remain only for frames that are never saved: previews, polar refinement, guide
      frames and the planetary live frame. For planetary, a memory-mapped SER may be the ring itself
      (the same plan, P2).
  - **Two slots per source** (each OTA's camera, the guide camera, the planetary live frame), sized
    for the largest frame that source can produce, so a smaller ROI or a higher bin fits the same
    slot. Two is the minimum a drop-to-latest seqlock needs (one being written, one readable), and
    enough when a copy (4.5 ms) is far shorter than the frame interval. Sections are pagefile-backed,
    so they count against the commit charge from creation even though only touched pages are
    resident: about 208 MB for a 26 MP camera.
  - **It plugs into the existing buffer mechanism at both ends, and not in the middle.** A slot cannot
    BE a camera buffer: a slot is unmanaged memory, and a plane is a managed `float[,]` and stays one
    (CLAUDE.md, "Image Mutability"). So:
    - **In the server, the slot writer is one more BORROWER** of the frame's `ChannelBuffer`:
      `TryAddRef`, copy into the slot, `Release`. That is the hosted guide preview's pattern
      (`GuidePreview`), and a lost race means "no frame now", never an error. The camera driver's free
      list (the DAL pattern: `onRelease` returns the array to `_freeBuffers`) is untouched.
    - **In the client, the reader is shaped like a camera driver.** It copies out of the slot into a
      `float[,]` from its own per-source free list and wraps it in a `ChannelBuffer` whose `onRelease`
      returns the array there. Everything above it (`Image`, `Release`, the DEBUG
      `ChannelBufferLeakTracker`, the viewer's `AcceptFrame`) sees a frame from the server exactly as
      it sees one from a local camera, and a 104 MB frame no longer means a 104 MB allocation.
      `Array2DPool` stays scratch only, as it is today.
  - **Memory and copies, one 26 MP mono frame from a 16-bit sensor, camera to screen** (decimal MB;
    traced through `DALCameraDriver`, `Session.Imaging`, `Image.Fits`, `Image.StarDetection`,
    `LiveFramePreviewSource.AcceptFrame` and `VkFitsImagePipeline`, 2026-09-24). The session's preview
    slot holds the frame on show until the next one replaces it (P0b item 15), so a camera has TWO arrays
    in use, the one on show and the next download. The slot's lease is a longer hold, not a copy. When
    this table was first drawn the slot kept only the released `Image`, one array was in use, and
    the frame-on-show row below did not exist.

    **Buffers that hold the frame** (resident, reused frame to frame):

    | Buffer | Today (one process) | Planned: server | Planned: GUI | Reduced: server | Reduced: GUI |
    |---|---|---|---|---|---|
    | SDK native buffer (uint16) | 52 | 52 | | 52 | |
    | camera `float[,]` (`ChannelBuffer`) | 104 | 104 | | 104 | |
    | camera `float[,]`, the frame on show (P0b item 15) | 104 | 104 | | 104 | |
    | shared-memory slots | | 208 (two, float) | mapped | 52 (one, uint16) | mapped |
    | reader `float[,]` | | | 104 | | none: normalised straight into the viewer |
    | viewer's normalised copy (`AcceptFrame`) | 104 | | 104 | | 104 |
    | Vulkan staging buffer | 104 | | 104 | | 52 (16-bit) |
    | GPU texture | 104 (`R32Sfloat`) | | 104 | | 52 (`R16Unorm`) |
    | **total** | **572** | **468** | **416** | **312** | **208** |
    | **copies of the frame** | **5** | **3** | **4** | **3** | **3** |

    Planned as first drawn is 884 MB and 7 copies, against today's 572 and 5: the slots and the reader's
    array are the price of the rig outliving the window. **Reduced is 520 MB and 6 copies, LESS memory
    than today's single process.** The texture is device memory, and on the Adreno laptop, as on any
    integrated GPU, device memory IS system RAM.

    **The frame on show is P1's to decide.** It is what lets a preview, or a client attaching mid-sub,
    read the last frame at any moment (P0b item 15). Once the server holds its own copy of a frame to
    show (a slot, or the saved file), the preview can encode from that copy, and the camera can have its
    array back after the write as it did before item 15. That takes 104 MB off both server columns.

    **Garbage per sub** (in the one process, as the sweep found it; all three frame-sized items are gone
    since #349, which is what lets the split start without them):

    | Allocation | Mono | Colour | Remedy |
    |---|---|---|---|
    | DAL `short[]` read copy | 52 | 52 | FIXED (#349), read in place |
    | FITS write, `QuantisePlane` `new short[h, w]` | 52 | 52 | FIXED (#349): streamed through FITS.Lib 6.1's `FitsWriter`, a 2 MB band at a time, so no quantised copy exists |
    | star detection, the mono debayer (`CreateChannelData`) | | 104 | FIXED (#349): debayered into an `Array2DPool` lease |
    | star detection, `BitMatrix` star mask | 3 | 3 | minor |

    That garbage is why every FITS write was followed by `GC.Collect(2, Forced, blocking: true)` and
    `WaitForPendingFinalizers` ("Add forced GC after FITS write to keep working set bounded": without it
    the working set climbed to 2 to 3 GB between natural collections).
    - A forced blocking collection suspends every managed thread, the render thread included. On a
      643 MB synthetic heap (3 million small objects and four frame planes) it took 29 ms: about two
      dropped frames once per sub.
    - With the three allocations gone, it went too (#349). Measured with the commit's own method, the
      working set logged after each write over 20 simulated 26 MP colour subs: 302 to 335 MB with ONE
      natural gen2.

    **The levers, in order of value for effort:**
    1. **Pool the FITS quantise plane and the star-detection debayer, then drop the forced GC.** DONE in
       #349: it removed the last 52 MB (mono) or 156 MB (colour) of frame-sized garbage per sub, and a
       whole-process pause per sub. Every finding the capture-path sweep made beyond it is in
       [frame-path-allocations.md](frame-path-allocations.md).
    2. **A 16-bit texture for a 16-bit frame.** Today, `VkFitsImagePipeline`, medium.
       - `R16Unorm` is exact for integer data. The pipeline already swaps in `R8Unorm` for 8-bit
         sources, "a quarter of the device memory, lossless".
       - What it needs: a per-channel scale uniform (a live frame is normalised by its own peak, not by
         65535), a re-bake of `image.frag`, and an `R16Unorm` sampled-and-linear-filter query with the
         `R32Sfloat` fallback that already exists for float.
       - Saves 104 MB in the viewing process: half the staging buffer and half the texture.
    3. **One uint16 slot for an exposure source** (two only for the video-rate planetary frame). Split
       only. Frames seconds apart need no second slot, since the reader copies within milliseconds of the
       announcement, and uint16 costs nothing on the way out: the widening IS the copy (3.5 ms for
       26 MP, measured). Saves 156 MB of shared memory per 26 MP camera.
    4. **Normalise while copying out of the slot, straight into the viewer's buffer.** Split only.
       - In the split the GUI only DISPLAYS. Saving the file and solving it happen in the server, which
         holds the full `Image` anyway.
       - So the reader's `float[,]` has no consumer. Saves 104 MB and one copy.

    Two further steps take the split to about 260 MB and 4 copies:
    - **The SDK writes straight into the slot** (DAL only). The native buffer is unmanaged memory
      already, so it can BE the slot: 52 MB and one copy off the server. It couples the DAL driver to
      the slot provider, and every other driver keeps the borrower path.
    - **The GUI uploads straight from the slot and takes its statistics from the uint16 samples.** That
      drops the viewer's float copy entirely, but it means a uint16 `IPreviewSource`, which is the
      larger refactor.

    **Considered and not proposed: `Image` planes as `ushort`.** That would halve every float buffer
    above, but "a plane is `float[,]` and stays one" (CLAUDE.md) is what every processing stage is built
    on. The savings above come from the edges of the pipeline and leave its middle alone.
  - **A seqlock per slot, never an acknowledgement.** The server makes the slot's generation odd,
    writes, then makes it even. The client reads the generation, copies, and reads it again, dropping
    a torn read and taking the next frame. A dead or stalled client can therefore never block the
    server, which is the whole reason this plan exists; a slot a client must release would let a
    crashed GUI hold the rig's frames.
  - **Per OS.** Windows: a named section in the `Local\` namespace. Linux and macOS: .NET opens a map by
    name only on Windows (CA1416 on `MemoryMappedFile.OpenExisting`), so the server creates the object
    with `shm_open` (a LibraryImport), the client opens it by name the same way, and both hand the
    descriptor to `MemoryMappedFile.CreateFromFile(SafeFileHandle, ...)`.
  - **Security.** The name is unguessable and travels only over the per-user socket. On Windows the
    section needs an explicit DACL for the current user, which means `CreateFileMappingW` with security
    attributes, because .NET's `CreateNew` takes none. On Unix, `shm_open` mode 0600.
  - **One copy on each side, measured.** The server copies in and the client copies out, 4.5 ms each.
    The client's copy is a single span copy into the `float[,]`, viewed flat through
    `MemoryMarshal.GetArrayDataReference` (the idiom `SyntheticStarFieldRenderer.FillBackground` already
    uses): about 4 ms for 104 MB, the machine's memory bandwidth, and no faster row by row. A FRESH
    array costs 22 ms on its first frame, while the OS commits its pages, which is one more reason the
    reader recycles its arrays. Against the socket this saves at least one copy and usually two
    (Kestrel's buffer, the kernel's send and receive copies, `HttpClient`'s buffer), plus every system
    call and all the HTTP framing. A
    later step could upload a display frame to the GPU straight from the mapped view and build an
    `Image` only when statistics, a solve or a save need one; not before a measurement asks for it.
  - **Where it pays.** The socket's ~80 ms is fine for a sub every 2 to 300 s. Shared memory matters
    for polar refinement (a full frame about once a second), the planetary live frame, a future live
    view, and several local clients watching one camera.
  - **Shipped: shared memory for a local client** (2026-09-28, #932, in #1035 and the PR after it; the user asked for it,
    which is the measurement decision 11 waited for). Every frame a client on this machine's socket takes goes through
    shared memory: a planetary capture's two streams (`planetary/live`, `planetary/master`), and every fetch of
    `/frames/{source}/latest` (each OTA's frame, the guider's, and the planetary sources fetched that way). Where it
    differs from the design above, and why:
    - **A stream's section is its own; a fetched source's is shared.** A stream's own loop is its one writer, and it
      writes only when its client asks, so there is no writer race and the section's life is the stream's. A source
      fetched on request has one section for every local client (`NodeFrameSlots`), written under a lock, since two
      clients may ask at once; it lives as long as the node from the first ask. A fetch of a frame a slot already holds
      is answered from there, unwritten.
    - **The answer names the slot.** The client asks with `?carrier=shared-memory` (a stream as it opens, a fetch on
      every request); the node answers with a `FrameSlotDto` (map, capacity, slot, generation, length) in place of the
      frame's bytes: a text message on a stream, a `application/x-tianwen-frame-slot+json` body on a fetch. Only over the
      socket (`NodeEndpoints.CameOverTheSocket`), and only a node that holds its lock registers the sections
      (`AddNodeSharedMemory`); anything else, or a section the system refuses, answers with the bytes as before, so a
      client reads whichever answer comes. A fetch that cannot open the section it is named (a node that restarted)
      takes that frame as bytes.
    - **`FRAME-AVAILABLE` stays a hint and names no slot** (decided while building it). A push naming a slot would have
      the node copy every frame it publishes into shared memory whether or not a local client wants it, and a push goes
      to every client, a TCP one included. The fetch the push prompts names the slot instead, so a local client pays one
      small request per frame and no copy crosses the socket. A mirror asks for shared memory only for this machine's
      node (`RemoteSessionMirror.IsOnThisMachine`); a rig's frames come as bytes, compressed as before.
    - **A slot holds `FrameWire`'s own bytes** (`FrameWire.Prepare` / `Write` into a span, `FrameReader.Read` from one),
      so a frame is pixel-exact through either carrier by construction and the reader recycles its planes as it does
      off the socket. The section is sized from the frame and made again, larger, for one that does not fit.
    - **Unix is a file, not `shm_open`.** A 0600 file under `/dev/shm` (the tmpfs `shm_open` itself uses), else the
      socket's own directory (macOS), mapped by path through `MemoryMappedFile`, which needs no LibraryImport (and
      `shm_open`'s variadic mode argument is not portable to a LibraryImport on Apple arm64). The node unlinks its
      files; one that died leaves them, and the next lock holder removes everything under its prefix (a hash of the
      socket path, so a test's node never touches the user's).
    - **Measured, a 26 MP frame from a node in another process** (6248x4176 float, 104 MB; a fake IMX571C's preview
      exposure fetched both ways, bit for bit, Release, this desktop). The first frame through shared memory makes the
      section and faults its pages in, and each slot's first write faults its own, so the third frame is the steady state:

      | OS | the socket | shared memory, first / second / steady |
      |---|---|---|
      | Windows 11 x64 | 38 to 368 ms | 83 / 61 / 24 ms |
      | Linux x64 (WSL Ubuntu, kernel 6.6) | 103 to 114 ms (389 first) | 194 / 109 / 16 to 17 ms |

      Windows is `SharedMemoryProcessTests`; Linux is the same measurement from a linux-x64 publish of the node and of a
      small client run under WSL, since the functional suite cannot be published for a RID (`SdlVulkan.Renderer`'s
      Android target). **macOS is not measured** (no Mac here), and its section is the one branch no run has exercised:
      a file in the socket's directory, as there is no `/dev/shm`. #1042 holds it.
    - **Measured, the live frame.** A 4144x2822 float live frame (46.8 MB), the carry alone (a frame waiting, the ask
      timed): shared memory p50 16.5 ms, min 8.4; the socket p50 26 ms, min 16 to 19 (`SharedMemoryCarrierProbe`, two
      rounds each, node in the test's process). Cross-process with the ZWO ASI462MC (`LiveFrameRateProbe`, a real node):
      every frame from a slot and none torn at 640x320, 1920x1080 and 1936x1096, and the client took every frame the node
      published through either carrier, so neither carrier limits this camera: its live frame is capped at display rate
      (30 a second at 640x320). Its full 2 MP frame came at 8 to 17 a second, which was the capture loop's intake, not
      the camera's: the camera read out 30 a second at the 16 bits and USB bandwidth 50 a stream then took, and the
      recentering took the rest (live-planetary-capture.md, "Frame rate at full frame", #1044).
    - **Pinned by** `FrameSlotTests` (bit-exact through a slot, the bytes the socket carries, a slot written again is
      dropped without a copy, a stalled reader never holds the writer, a larger frame makes the section again),
      `NodeFrameStreamTests` (over a node's socket every frame from a slot and bit for bit the frame the node holds; a
      stream that does not ask gets bytes; a client gone mid-read leaves the node streaming to the others), `NodeFrameTests`
      (an OTA's frame and the guider's through a slot bit for bit, nothing sent while the client holds the frame shown),
      `RemoteSessionMirrorDriveTests` (a local mirror asks and copies the slot out; a rig's asks for bytes) and
      `SharedMemoryProcessTests` (the cross-process 26 MP frame above).
- **A saved frame is its FITS file, for a remote client as much as a local one, and where the
  original lives is a NODE policy** (user, 2026-09-24: "usually a mini pc will have plenty of storage
  and its always good to have the original at hand in case something goes wrong"). **Only the local
  half ships in 10.0** (decision 11): the GUI reads the file its node wrote, and the node keeps every
  original. The remote fetch, `FreeWhenCopied`, the client's copy policies and the pre-night space check
  follow in a 10.x minor, since each only adds.
  - **The node that runs the session writes the original, once, on its own disk, and keeps it.** That
    is already where a remote rig's subs land, since `Session` writes where it runs. What is missing is
    a way for a client to reach them: `LastFramePath` names a path on the node, and no endpoint serves
    the file.
  - **A client re-hydrates a saved frame from that file and from nothing else.** A local client maps
    it (above). A remote client fetches the same bytes through a new `GET /frames/{id}/fits`, which
    resumes by `Range` and carries the digest the node took as it wrote, then reads its copy through
    the same reader. So a remote frame arrives bit-identical to the original, headers included,
    which the float wire format does not promise, and the float format is left to frames that are
    never saved (previews, polar refinement, guide frames, the planetary live frame). It also disposes
    of decision 6's objection for saved frames: the client reads a file it holds, so nothing seeks a
    socket.
  - **Each machine decides about its own disk, so the policy has two halves:**
    - **The node's retention**, set on the node, since it depends on the machine. `Keep` never
      deletes an original. `FreeWhenCopied`, for a node short of storage (a Raspberry Pi on its SD
      card), deletes the node's OLDEST frames that a client has fetched and verified by digest, and
      only once free space falls below a floor; it never deletes a frame with no verified copy
      elsewhere, and with nothing eligible it warns and deletes nothing.
    - **The client's copy**, set on the client's binding to that node (`RemoteRigBinding`), since it
      depends on the client. `OnOpen` fetches a frame when the user opens it, into a cache. `Mirror`
      pulls each saved frame as it lands, into the client's own archive. `AfterSession` pulls the
      night once the run has ended.
  - **Before a night, the node compares the schedule's frames with its free space** (frame count
    times frame size, less the retention floor) and warns at the start, rather than filling the disk
    part-way through.
  - **The local node has nothing to choose**: the file is on this machine already, in the output
    folder, and the GUI reads it.
  - **What the copies buy.** The node's original survives a client crash, a link dropped part-way
    through a transfer (the fetch resumes) and a failed client disk. A mirror survives the node's own
    disk failing. The defaults are decision 9.
- **Ownership.** `Image` ownership does not cross the boundary: the client owns what it decoded, and
  the server releases its buffer once sent.

**Planetary stacks in the server** (decision 5, decided). The ring, the stacker and the centre-of-mass
recentering, which needs the frame AND the mount, all move with the capture loop (survey ranking:
hardest item; today the stack even runs on the GUI's render thread). Only the rolling master and a
display-rate live frame cross the socket, drop-to-latest over a binary WebSocket channel. This is also
the only placement where a GUI crash keeps the stack. There is no Canon live view in the GUI to move
(the review): "live stacking" is this planetary stack, so the open question about both is closed.

**Render-thread reads go away.** The GUI reads hub and driver properties synchronously on the render
thread in more places than first counted: `IsConnected` / `TryGetConnectedDriver` in the status bar's
Connect All check every frame, the Home board's device counts every iteration and the Equipment rows;
`hub.ConnectedDevices`, an allocating list, every iteration; and the camera's pixel size and sensor size
on every sky-map frame, each an IDispatch call on an ASCOM camera. Async signal handlers also run inline
on the render thread up to their first `await`, so the cooler handlers make COM calls there. After the
cut all of it reads the snapshot, which retires a standing breach of "no blocking I/O on the render
thread".

### Which rig the GUI shows

The local node is a node like any rig, found on the socket rather than by LAN discovery. The GUI's
**Local** view context binds to it; nothing else about view contexts changes. It must not show up as
"(remote)" in the peer table or as a second card of itself on the Home tab. Its identity is the node's
stable id, which is what `RemoteRigBinding` already persists against.

What that takes, from the review:
- **The Local context has no node id today** (`NodeId` is null), the GUI announces itself as
  `tianwen-gui` with no stable id, and the rig picker lists every `tianwen-server` peer. So a shared
  local node would be offered as a remote rig and, if picked, become a second context, a second mirror
  and a second card. The Local context takes the node's id from `GET /api/v1/node`, and the peer table
  and the picker leave that id out.
- **The Home card for Local reads node state**: its device count from the node (today the GUI's own
  hub) and its last note from the node's feed. It goes offline when the node dies; today a mirror keeps
  its last snapshot and the card goes on claiming "online".
- **The local rig's data stays where it is.** Planner pins stay under `Planner/<profileId>/` and the
  session setup in `Session/<profileId>.json`; the local node is never persisted as a
  `RemoteRigBinding` (whose pins live under `Planner/rigs/<bindingId>/`), so no user's pinned targets
  move at the cut.
- **The sky map reads the Active rig's own camera and profile.** Its sensor rectangle comes from the
  GUI's hub and local profile even when the reticle is a remote rig's, and its active-target highlight
  compares `Target` records, which a mirror breaks (its `CatalogIndex` is null). Both follow the rig
  shown (P5b).

## P5b: mirror parity, part by part (#935)

A survey of 2026-09-27, against `main` once P5 had landed, listed every place a session seen through
`RemoteSessionMirror` renders differently from one run in-process. At the cut (P6) the local rig
becomes a mirror too, so each of these becomes a gap on the user's own rig. The worst five:
- **A mirror never sets `IsRunning`.** Its only writer is `SessionBootstrapper`. So a watched rig draws
  the Live Session tab's idle layout (no phase strip, no exposure log, no ABORT), and the Guider tab
  shows only its placeholder, although the mirror downloads the guide frame.
- **A remote prompt holds the night with no way to answer it.** The GUI beats as present to every node
  it watches, so the node holds a prompt for it (invariant 2), but nothing subscribes to the mirror's
  `PromptRequested`. **A local full session never shows its prompt either**: `SessionBootstrapper`
  subscribes nothing, so the one prompt a session raises (a manual flat panel at the end-of-session
  flats) is declined unseen. Only `FlatsBootstrapper` wires prompts.
- **No control reaches a rig's node.** Abort and a prompt answer act on the LOCAL session with no
  context check. Only `IsRunning` being false keeps ABORT off a rig's panel today; once a mirror sets
  it, ABORT on a rig would abort this computer's night.
- **NaN crosses as 0** (`JsonNumber.ForWire` under the strict contract), so every unknown arrives as a
  real-looking zero. A node's pre-poll mount arrives at RA 0, Dec 0 and snaps the reticle there.
- **The sky map is this computer's** except for the mount's pointing: the sensor rectangle is the local
  profile's and camera's, and the schedule and its active target are the local ones.

The rest, grouped by the part that closes it:
- **Lossy state.** A `Target` is rebuilt without its `CatalogIndex`, with the filter plan flattened to
  one entry and gain, offset and priority lost. The mount has no J2000 position, altitude or axis angle.
  Frame metrics lose their exposure, gain and filter. Guide stats lose the last errors and pulses. The
  settle progress, the star profile and the calibration overlay are always null. Each camera's cooling
  on the wire is ignored. A camera's `ExposureStart` is on the node's clock while the countdown runs on
  this computer's.
- **Two notification mappings.** The node words each phase, scout and guider change its own way
  ("Initialising -> WaitingForDark") and the GUI its own ("Waiting for astronomical dark…", filtered),
  and a mirror shows only the node's latest note.
- **Events.** Of the thirteen the node sends the mirror acts on two (`FRAME-WRITTEN`,
  `PLATE-SOLVE-COMPLETED`), and nothing subscribes to either. A remote context never sets
  `NeedsRedraw`, so it repaints on the 1 s tick. The Live Session and Guider tabs have no sign that a
  rig went stale.
- **Polling.** Every 500 ms the state carries the whole night's histories (the exposure log is
  unbounded: about 0.8 MB a poll at four OTAs over ten hours), and the frames are fetched on every tab.
- **The rig's own context.** The site's time zone and the twilight are this computer's. The running
  configuration is not on the wire. A flat run on a node has no run kind, so the Flats panel never shows.

### P5b part 1: the parity harness

The instrument before the fixes: ONE real session on fake devices, run by an in-process node on a
pumped fake clock, is read at every phase through both paths the GUI has. The in-process path reads the
`Session` directly. The mirror path polls that node over HTTP. The two `LiveSessionState`s are compared
member by member. **The known gaps are an explicit list, each naming the part that closes it**. The
test fails on a divergence the list does not name, and on a listed one that has come to agree, so every
later part deletes its lines and the list reads as the work left. The last part leaves only P6's.

`MirrorParityTests` runs a whole night (every phase and three frames) in about 12 s. The session's
clock is an external pump, so a hold freezes every loop on it. Its first run named twenty members:
- **part 2:** the mount's name adopted from the 0/0 pointing;
- **part 3:** the whole observation, including a guessed filter plan (two filters of 20 s and 30 s
  came back as one of 26 s); the mount's J2000 position, altitude and axis angle; frame metrics' exposure
  and filter; the last guide errors; and the settle progress and star profile, which are null;
- **part 4:** `IsRunning`;
- **P6:** a frame the session let go that the mirror keeps. That is by design: `ReleaseCapturedImages`
  leaves a client its picture. P6 decides it, since the frame on show from the node's own copy is P6's.

The night it runs is set so a dropped field shows: a catalogued target, a priority, a gain and an offset,
and two filters at different sub-exposures. Gaps the survey found that this night cannot show (a prompt,
a focuser with no thermometer, a calibration overlay) are added to it by the part that closes them.

### P5b part 2: NaN crosses as null

An unknown number is `null` on the wire, never 0, and the mirror reads a null back as NaN. This is a
contract change, so it bumps `NodeWire.Version`.

### P5b part 3: lossless state

The fields above cross whole: each observation's `CatalogIndex`, filter plan, gain, offset and
priority; the mount's J2000 position, altitude and axis angle; frame metrics; guide stats; the settle
progress, star profile and calibration overlay; each camera's cooling; and the exposure start measured
against the node's clock.

### P5b part 4: a run is on, and says so

A mirror sets `IsRunning`, the run's kind and the mode from the node's run, and surfaces the failure
reason. The notification words are ONE mapping in Lib, used by the node's feed and the local
bootstrappers alike. A rig's feed is the node's ring, then its `NOTIFICATION` push.

### P5b part 5: control goes to the rig's own node

Abort, a prompt's answer and a flat run's cancel act on the Active context's own node: this computer's
session for Local, the rig's node through its mirror for a rig. A prompt is shown and answered through
ONE wiring for a local session, a flat run and a mirror, which also gives a local full session its
prompt back.

### P5b part 6: every event, and a redraw

The mirror handles every event the node sends. A remote context redraws when its mirror changes.
Frames are fetched on `FRAME-AVAILABLE`, only for a tab that shows them. A stale rig says so on the
Live Session and Guider tabs.

It lands in two PRs:
- **6a, events, frames and a redraw.** `RemoteSessionMirror.Dispatch` puts each event the node broadcasts into one of four kinds:
  - a change to the state (a phase, the guider's state, a prompt, a frame written) wakes the poll now, while the poll
    still owns the events it derives, so none is raised twice;
  - `FRAME-AVAILABLE` fetches that frame alone, between polls;
  - an occurrence the state does not carry is raised as its event. `SCOUT-COMPLETED` now carries its target whole, so a
    mirror raises `ScoutCompleted` too, and a note is raised as `NoteReceived`;
  - a device's state, a profile, a job and an enhance are named as another client's.

  The state carries the node's token for each frame source (`Frames`), and a source is fetched only when its token is
  not the frame held: a poll with nothing new asks for no frame at all, where each OTA was asked every 500 ms. A node
  from before this sends no tokens and is asked every poll, as before. The rig on screen pulls only the frames its tab
  draws (`ViewContexts.FramesShownOn`), and its view redraws on the mirror's `Changed`.
- **6b, a stale rig and its feed.** Whether a node is answering is one rule, `ISessionTelemetry.Contact`: Connecting
  until the first poll comes back, then Answering or NotAnswering as the latest poll found it, with when it last answered.
  It is said in one wording (`RemoteRigActions.DescribeContact`: "Connecting", "Not answering (last seen 3 min ago)") by
  the rig's Home card, its Live Session tab (in place of the activity, GUI and TUI) and its Guider tab (in the header).
  The card used to call a rig still connecting "Not answering". A rig's notes are its node's feed
  (`RemoteSessionMirror.Notes`): the ring, read at the first answer and again after the socket reconnects, since a note
  pushed while it was down is in the ring alone, then each `NOTIFICATION`, one of each note. The Notifications tab shows
  the feed of the view on show (`NotificationFeed`), and the card's last note comes from it, so a rig's "session ended"
  note stays on the card after its node goes idle, where the state's own note went with the state.

### P5b part 7: incremental polling

The histories are fetched from a cursor, not whole every 500 ms.

As built: a poll names where the client's copy ends (`SessionStateCursor`: the session, by the id the node gives each run's
session, and a count for each of the exposure log, the focus history, the cooling samples and the phase timeline, which
are append-only for a session's life). The node sends each from there, and `HistoryFrom` says where each part starts; the
mirror appends (`RemoteSessionMirror.Histories`), mapping each entry once, where it used to map every history again on
every frame it was read. The guide steps are the session's ring of the latest 300, so their cursor is the number of steps
ever taken (`CircularBuffer.Window` reads the items and the count together): the node sends the steps after it, and the
mirror keeps the ring's size. A cursor naming another session, or longer than a history, gets it whole; a part that does not
continue the copy leaves the next poll naming no session, so it gets everything. Older nodes and clients send or ask for
everything, which both sides read, so `NodeWire.Version` stays 2. `IncrementalStateNodeTests` shows a node really sends
less; `MirrorParityTests`, polling with cursors at every phase, shows the copy built this way is the session's own.

### P5b part 8: the rig's own site, configuration, camera and schedule

The site's time zone and twilight, the running configuration, the sensor rectangle and the schedule
all follow the rig shown, and the sky map's active target matches by catalogue index.

As built, the rig's own profile carries them, since `ProfileDetailDto` already holds the site and the whole
`ProfileData` and the client derives the rest exactly as the local GUI does (the decision recorded on the DTO):
- **The profile reaches the rig's view.** The connection's two-minute refresh, which only learned the profile's name, now
  reads the profile whole (the binding's `RemoteProfileId`, else the one the rig runs) onto `ViewContext.RigProfile`.
- **The planner plans with the profile on show** (`AppSignalHandler.ProfileOnShow`). A rig's nights, and so the
  app's site time zone, the twilight bands on its Live Session timeline and the sky map's site, are its own; a rig whose
  profile has not been read yet is not planned at all rather than at this computer's site. A switch of view is noticed
  by the planner itself, whatever caused it, and replans in full, dropping the other view's pins first: a load replaces
  the plan only when the view has pins saved, so a rig with none showed this computer's and the next save wrote them
  into the rig's file. This computer's site edits reach the planner only while its own view is on show.
- **The sensor rectangle** at a rig's reticle is the rig's profile's captured sensor, never this computer's camera.
- **The schedule** on the sky map is the view's own (a rig's `Observations`, mapped once per polled state), and the one
  being imaged is matched by `PlannerActions.IsSameObject`: by catalogue index for a planet, the Moon or a comet, whose
  coordinates are an instant's, and by the whole target otherwise, since mosaic panels share an index.
- **The running configuration** needs no wire of its own: no view reads a run's configuration (the survey of 2026-09-27
  found none), and the profile carries what the views take from it.
- A node fix on the way: `GET /session/profile` read the discovery registry and answered "no longer exists" for a profile
  saved since, while `GET /profiles/{id}` read the file; it reads the file too (`NodeProfiles`).

The TUI's preview rows for an idle rig, built from this computer's profile with this computer's controls, belong with
part 9's idle layout.

### P5b part 9: the tabs lay out the same

The Live Session, Guider and Home tabs lay out identically for the two states the harness compares.
The known-gap list holds only what P6 decides (the frame on show).

It lands in two PRs:
- **9a, the tabs drawn both ways.** At every comparison the harness also draws each view's Live Session and Guider tabs
  offline, over the CPU renderer, and compares what they drew: each run of text with its place, size and colour, each
  region laid out, and where those agree, the pixels (`TabPictures`). A tab reads more than the state's members: the
  session's own mount name, its OTAs (`TelescopeDisplays`) and its schedule's length, which only a drawing compares. The
  Home tab draws only from its cards, and what a card says of a run is now ONE rule for this computer's card and a rig's
  (`HomeBoard.RunCard`), which the harness compares. No preview viewer is attached: the frame on show is P6's. The first
  run found nothing: the running states lay out the same.
- **9b, an idle rig.** With no run, the Live Session tab lays out the OTAs and the mount from `PreviewOTATelemetry` and
  the mount's state, which only this computer's device poll wrote: a rig with no run showed "No OTAs configured", its Home
  card said "NotStarted", and the TUI's preview rows listed this computer's OTAs. As built:
  - **An idle rig reads its devices from its node** (`RemoteRigConnection.MaybeRefreshDevicesAsync`, `GET
    /api/v1/devices/state`) while it is on show on the Live Session or Sky Map tab, at this computer's idle cadences. The
    node reads them through the same readers as this computer's hub, and `RigDevices` lays them out by the rig's own
    profile's OTAs and mount (P5b part 8's `RigProfile`), matched by the hub's identity rule. A device state now reads back
    as the reading it was made from (`ToReading`, an unknown NaN again), and a camera's ROI rules cross with it.
  - **One builder for an OTA's telemetry** (`PreviewOTATelemetry.From`), for this computer's hub and a rig's node alike;
    a camera is named by its URI, as every device is (`DeviceBase.DisplayNameOf`, public now), else by its OTA.
  - **A mount no one holds points nowhere known** (`MountState.Unknown`, NaN), on both views; this computer's idle view
    used to write `default`, RA 0 and Dec 0, which kept the mount's name and could put the sky map's reticle at the
    origin. The mount sections print dashes for an unknown pointing, as the Equipment tab did, since the sexagesimal
    formatters throw on NaN.
  - **A session's limit verdict moves with its pointing** (`PollSession` adopts both only once the pointing is real), so an
    idle rig keeps the verdict its node's watcher gives its mount, as this computer's idle view keeps its own watcher's.
  - **A node that serves no session is idle** (`ReportedRun.NoSession`): holding a rig's mirror is not having a session,
    so its card says "Idle" as this computer's does, while a finished session is still held on both sides.
  - **The TUI runs the GUI's per-frame poll** (`PollPreviewTelemetry`, where it ran only the limit transitions): its idle
    rows now have readings, a rig on show gets its profile read (part 8's planning at the rig's site had never reached the
    TUI) and its devices, and the rows are the view's own profile's (`ViewContexts.ProfileOnShow`, one rule for both hosts).

  The controls drawn on an idle rig's panels still refuse outside this computer's view (P0b item 9); P6 routes every
  device control through a node client, this computer's included. `IdleRigDevicesTests` draws an idle rig and this
  computer's view of the same devices on a real node, and they lay out the same.

## P6: the cut (#936)

A survey of 2026-09-27, against `main` once P5b had landed, listed every place the GUI (`TianWen.UI.Gui`, with the
logic it shares in `TianWen.UI.Abstractions`), the TUI and the CLI (`TianWen.Cli`) still touch hardware in-process, and
the node route that takes each over:

| Group | Sites | Examples |
|---|---|---|
| Composition | about 45 lines in six files, and the two `TianWen.Devices.Native` references | the device sources in `Program.cs` (GUI and CLI), `GuiAppState.DeviceHub`, `RigShutdown(hub)`, the GUI's and the TUI's `MountLimitWatcher` |
| Connect, disconnect, discovery | 15 | the Equipment tab's connect, Connect All, disconnect, warm and disconnect, the CLI's `device` verbs and `darks` |
| Telemetry outside a run | 8 | `PollPreviewTelemetry`, `PollCameraTelemetry`, the mount poll, the Equipment tab's cooler and mount sections |
| Actuation | 15 | preview, snapshot, solve, focuser, planetary, goto, solve and sync, cooling |
| Runs | 10 | `SessionBootstrapper`, `FlatsBootstrapper`, polar, the CLI's `darks` and `flats`, the first-run wizard, `RigShutdown` and `AppQuit` |
| Profile writes | about 40 | the Equipment tab's edits (the `UpdateProfileSignal` funnel), the CLI's `profile` verbs, the backlash mirror |
| Gates | five, with about 20 callers | `EnsureDeviceControllable`, `DeviceOwnershipGate`, `ProfileSwitchGate`, `EnsureSessionIdle`, `EnsureLocalContext` |
| Other hub reads | about 15 | reachability and labels on the render thread, `SessionTabState.InitializeFromProfile(hub)`, the inspector's mount fields |

Nearly every site has its route already (P2 to P5). Decision 7 fixes the shape of what is left: this computer's
devices cannot move to the node one subsystem at a time, since the node and the GUI would then both hold them. So P6 is
two parts.

### P6 part 1: the server surface's last gaps

The survey named six places with no route; three need one, each built and tested end to end as P2 to P5 were:
- **A guide-rate nudge of the mount** (the planetary panel's coarse recentre, `MountNudge.PulseArcsecAsync`), a short job
  refused on a leased or busy mount like every actuation route.
- **What a camera IS: its named gains and whether it can cool**, on the node's device listing (`DeviceDto.GainModes`,
  `CanCool`; `SessionTabState` read both off the GUI's device registry to offer a gain and a setpoint), known with nothing
  connected since they are what the device is, not what it reads.
- **`TianWenNodeClient.ShutdownNodeAsync`** for `POST /api/v1/node/shutdown`, which only the launcher posts today.

The other three need none:
- **The weather forecast stays in the clients.** Its sources are HTTP clients that own no hardware, and the API key they
  read is in the per-user credential store, which the node writes (P3 part 4) and every client of the same user reads.
  The web build fetches the same way.
- **"Include fake devices" is a client's filter**, not a discovery parameter: a node lists the fake devices it
  registers (every node does), and the GUI's Shift+Discover and the TUI's `--fake` choose whether to show them.
- **A camera's pixel size** is not needed: the sky map's sensor rectangle comes from the profile's captured sensor for
  every view at the cut, as it does for a rig since P5b part 8. The polar demo's misalignment nudge stays a fake
  device's own query key.

Pinned by `MotionOperationTests` (a nudge's job ends when its pulse does, a held or busy mount refuses it, an axis with no
guide rate fails it saying so), `DeviceListingCapabilityTests` and `NodeShutdownClientTests` (refused over TCP, a stop
over the socket).

### P6 part 2: the cut, in one wave

One PR, reviewable commit by commit, which switches the GUI, the TUI and the CLI together (decision 7):
- **Local is the local node.** `ViewContexts.Local` holds a mirror of the machine's node, found or started through
  `LocalNodeLauncher` over its socket, with the node's id from `GET /api/v1/node`; the peer table and the rig picker
  leave that id out ("Which rig the GUI shows").
- **Each view reads its devices from a client-side model**, seeded from `GET /devices/state` and kept by `DEVICE-STATE`
  (part 9b's `RigDevices` reads for an idle rig): every per-frame read (connected, reachable, a label, a telemetry row)
  answers from it, never an HTTP call per frame.
- **Each action goes to the view's node** through `TianWenNodeClient`. Run controls do already (P5b part 5); device
  actions go to the local node, and a remote rig's panel still refuses them (the overlay model), since the node's
  actuation routes accept a LAN caller and whether a rig's panel may command its devices is the user's call. Decided
  (decision 13): it may once it has been granted control, and until then it sees and does not command (P6b).
- **Profile edits carry the revision they were read at**, and a 412 re-reads the profile and applies the edit again.
- **A run starts on the node**: `SessionBootstrapper` and `FlatsBootstrapper` go, and polar, planetary and the CLI's
  `darks` and `flats` start their node runs.
- **The gates come from the node**: a lease or a job holds a device there, and the node answers the refusal. A gate
  that asks a hub which is not there would answer "free", so every one is deleted, not left.
- **The frame on show comes from the node's copy** (P4 part 5), which deletes the parity harness's last known gap.
- **Quitting follows decision 1**: the last client asks, a run left running is the default, and "Stop the rig and quit"
  is the node's abort, `Finalise` and warm-up, with progress.
- **Deleted**: `GuiAppState.DeviceHub`, every device source but the weather's in the GUI's and the CLI's composition, the
  `TianWen.Devices.Native` references, the GUI's and the TUI's `MountLimitWatcher`, `RigShutdown`'s hub path, the
  inspector's in-process mount fields.
- **Packaging**: `tianwen-server` and `tianwen-ascomhost` inside the GUI's and the CLI's archives and the GUI's `.app`.

Risks the survey named, each owed a test in the cut:
- A render-thread read of a live driver (the sky map reticle's FOV, `VkGuiRenderer`), and the per-frame hub reads.
- The planetary tab pushing its recenter settings every frame: over HTTP it sends only a change.
- `StartVideoCaptureSignal` claiming the camera in a synchronous render-thread subscriber.
- The preview, polar and solve-and-sync slots holding an in-process `Image`.
- The CLI's `flats` counting its output by the local folder's change.
- A cooler setpoint becoming a ramp job, which changes what the Equipment tab's setpoint does.

#### How the cut was made

One commit per step, each building and each leaving both suites green:
- **A node's devices on its connection.** `NodeConnection` is the base of this computer's connection
  (`LocalNodeConnection`) and a rig's: the device model seeded from `GET /devices/state` and kept by `DEVICE-STATE`,
  the listing (`NodeDevice`, a camera's named gains and whether it cools), the profile with its revision, and every
  per-frame read answered from them.
- **This computer's node as Local.** Found or started at start-up (`AppSignalHandler.ConnectLocalNodeAsync`), its id
  kept out of the rig picker and the Home board; the active profile, the planner's start and the session setup
  follow once it answers.
- **The Equipment tab, the Live Session's device actions and the sky map's** go to the node as its jobs: discovery,
  connect, disconnect with its safety read, warm-up, the cooling ramp, preview, snapshot, solve, focuser, nudge, goto
  and solve and sync. A refusal is the node's, naming the run. An edit of the profile names its revision, and a 412
  is made again onto the profile as it is now (`NodeProfileWrites`, one helper for the GUI, the TUI and the CLI).
- **Every run is the node's.** A session with the schedule planned here and the session tab's configuration
  (`SessionStartPlan`), a flat run from that configuration, polar alignment followed by its watcher, and planetary
  (`PlanetaryCaptureController`: the node stacks, the view shows the masters it streams through the same
  `LiveStackPreviewSource` a SER playback uses, and sends the panel's controls only as they change). An abort goes to
  the node of the view on show. `SessionBootstrapper` and `FlatsBootstrapper` are gone.
- **Quitting follows decision 1** (`AppQuit`, `QuitDialog`), and "Stop the rig" is the node's order
  (`RigShutdown`): the run through its own ending, then the devices. A dead display stops nothing; the window
  leaves.
- **The CLI's `profile`, `device`, `darks` and `flats` are node clients** (`ConsoleHost`): the node is found or
  started on the first verb that needs it, never for a stack or a solve.
- **The tests stand on a node**: `GuiNodeHarness` is the GUI's handler over a real node on its socket; the
  in-process harness is gone.

The risks above, answered: the reticle's FOV reads the profile's captured sensor, never a live driver
(`VkGuiRenderer`); the per-frame reads come from the device model; the recenter settings are sent on change
(`NodePlanetaryViewTests`); `StartVideoCaptureSignal` only posts the start, whose claim is the node's; the preview,
polar and solve slots show the node's frames; `flats` counts by the output folder the node writes into, on this
machine, with the run followed by the node's run record; and the Equipment tab's setpoint is the node's ramp job,
whose intent the node keeps.

### P6b: control over the LAN by grant

Issue #1021, decision 13, which answers the question P6 part 2 left open. Until it, a node's commands were open to
anyone who could reach TCP 1888 (plain HTTP, no authentication), while a TianWen window viewing a rig refused to
command it: the app was the stricter of the two, and a rig was only as safe as the LAN it sat on. Decision 13 (the
user, 2026-09-28): **a remote client can see, but not manipulate, by default.** It asks, a client on the rig's own
machine accepts or declines, and on acceptance it gains the capability.

**Seeing is free; everything else needs control.** Over TCP every read stays open. Every command needs control: a
run's start and its abort, a prompt's answer, a device action, a job, a profile's read-write routes aside (those stay
socket-only, decision 4). Over the node socket nothing changes: a client of this machine never needs a grant.

**A TianWen client asks, and the rig's machine answers.** The rules are chess's LAN invite, lifted into LAN.Lib 2.1
as `LanInvites<T>` rather than copied: one request waits at a time, it lives only while its asker keeps polling (a
presence lapse, so nothing is granted to a window that has gone), and an answer names the request it answers. A client
without control shows "Ask to control" where a command would be, and a refusal offers it. Every client is told
something changed (`ACCESS-CHANGED`, a hint with no content), only those that may answer can read the request, and the
rig's GUI and TUI draw it over everything, like the quit question: "'Laptop'
asks to control this rig: Allow / Decline". Allowed, the node mints a grant (`LanGrants`: a 256-bit token handed to the
asker once, kept only as its hash in `node-grants.json`), and the asker keeps the token in its credential store, keyed
by the rig's `NodeId`, and sends it as `Authorization: Bearer` on every request and on its event socket's upgrade. **A
grant is remembered until revoked**, so a headless rig needs one acceptance per laptop, from the TUI or `tianwen node`
over SSH. With it, the rig's view sends its device actions, run starts, aborts and prompt answers to the rig's node,
which is where `EnsureLocalContext` stops refusing: it is gone, and every such handler resolves its node, its view and
its profile through `CommandTargetOrSay`, which refuses a rig this client only watches and says how to ask.

**Other apps are refused, and the refusal is their request.** The Alpaca device plane and the ninaAPI routes serve
applications that cannot ask (N.I.N.A. over Alpaca, a ninaAPI client). A command from one of them over TCP, from an
address not allowed, is refused and recorded, **and on the Alpaca plane `Connected = true` is a command**: it connects
the hardware through the hub, so an app not allowed is refused at its connect, with a message saying where the rig's
owner allows it. Reads stay open (the management API and each device's properties), which is what lets an app find the
rig's devices at all; most of a device's properties answer NotConnected until it connects anyway. The node serves the
management API on its TCP port but does not answer Alpaca's UDP discovery (port 32227), so an app finds a rig by its
address. The record is one per address, a retry updating it rather than adding another (an app retries on its own),
holding its host name, what it is (its `User-Agent`, an Alpaca `ClientID`), what it tried and when, and it is put to the
rig's machine and its granted clients as a question, as a TianWen client's request is: Allow, Always allow `<host>`, or
Ignore. Nothing waits on the node's side, since the app does not know to: its next attempt (the user pressing Connect
again) is let in. From that record:
- **Allow** lets that address command until the node restarts (the user: per session, which is fine).
- **Always allow `<host>`** persists. The host name comes from a reverse lookup of the address, counted only when the
  name resolves back to the same address (forward-confirmed, `LanHostNames` in LAN.Lib 2.1), so a DHCP renewal does not
  break it and a stray PTR record cannot claim it. An address with no confirmed name can only be allowed until restart.

**Who manages access: this machine's clients, or a client granted control.** Answering a request, revoking a grant,
allowing or revoking an app, and switching "Share this rig on the LAN". A granted client can already drive the whole
rig, so letting it manage who else may is no escalation, and it is what lets a laptop manage a headless rig from its
own dashboard once it has been granted.

**Where: a Sharing panel on the rig's Home card.** The LAN share switch (decision 3's, which no client could set until
now: only the socket route existed), the requests waiting, the granted clients with revoke, the apps allowed with
revoke, and the recent refusals with Allow and Always allow.

**Presence and "the last client" count only clients that can command.** A see-only watcher is present in the sense
that it draws, but it can answer nothing: counted for a prompt, it would hold one nobody can answer, and counted for
the quit question (decision 1), it would stop this window asking. Both counts are the event socket's clients that came
over the socket or carry a live grant, re-evaluated as they are read, so a revoke takes effect at once. The
interactive-run grace (`NodeRunWatch`) keeps counting every watcher: a polar alignment someone is watching is not
unwatched.

**What it does not stop.** Plain HTTP: a token can be read off the wire, and a host name or an address can be spoofed.
It closes "anyone who can reach the port", not a hostile LAN, which is TLS's job and a plan of its own.

#### P6b parts

1. **LAN.Lib 2.1**: `LanInvites<T>`, `LanGrants` and `LanHostNames`, each with no transport, and chess's lobby moved
   onto `LanInvites` in chess's own change.
2. **The node**: the gate over TCP (the refusal a client can tell from a socket-only one), the request, answer, grant
   and revoke routes, the refused-app record with Allow and Always allow, management over the socket or a grant, both
   presence counts, and one read of all of it for the Sharing panel. The wire version moves. **Done** (wire version
   3): `NodeAccessGate`, `NodeAccess` and `AccessEndpoints`, described in
   [../architecture/hosting-api.md](../architecture/hosting-api.md), "Who may command the node over TCP".
3. **The clients**: the Sharing panel on the Home card, "Ask to control" and the request drawn on the rig's machine,
   a granted view's commands sent to its rig's node, the token in the credential store, and `tianwen node requests`,
   `allow`, `decline`, `grants`, `revoke` and `share` for a headless rig. **Done**: `NodeGrants` and the connection's
   access (`NodeConnection.MayCommand`, `Access`, `AskForControlAsync`), `CommandTargetOrSay` in place of
   `EnsureLocalContext`, `ControlRequestQuestion` over this computer's Live Session view, `RigSharing` and the Sharing
   panel under the Home board, and `tianwen node` (with `ignore` beside the verbs named here). The Sharing panel sits under
   the board for the rig on show rather than inside its card, whose line says who may command it, since a card is sized
   to the busiest one and a list of grants and refusals would grow every card on the board.

## Phasing

The cut is **one wave** ("cut an API in ONE wave"; "one path, designed first"). Two processes cannot
share hardware: a server probing serial ports while the GUI holds them is contention, not redundancy.
So the server surface is built and tested first while every host keeps its in-process hub. Remote rigs
gain each piece as it lands. Then the GUI, the TUI and the CLI switch over in one step (decision 7),
and 10.0 ships it (P9).

| Phase | Scope | Proves it |
|---|---|---|
| **P0a** (#743), **DONE 2026-09-25** | A dead GPU keeps the night alive (above), with the quit path's own bugs: flat and polar runs cancelled, polar's hang, `QuitRequested` cleared, the invisible confirmation. Needs SdlVulkan.Renderer 7.49 (SharpAstro/SdlVulkan.Renderer#112) | live `gpu_fault reject` over a fake session: the session ends at its own time, `Finalise` runs, the window stays closable throughout |
| **P0b** (#752) | Server lifecycle and wire bugs 1-18, the GUI context-gating bug 9 among them | a test per item that fails first; a server test that aborts a session and sees park and warm-up; a session started over HTTP with no body runs on the declared defaults and never syncs the site to 0°, 0° |
| **P0c** (#788) | Host bugs the split would carry over: the TUI's quit, leases for polar and planetary, the cross-process AppData write, the Linux output folder | a test per item that fails first; two processes each writing one profile a thousand times beside a reader, with no loss and no exception |
| **P1** (#917), DONE (2026-09-26) | **Shipped:** the socket (`--socket`, `NodeSocket`, length-checked), `--node-socket` / `TIANWEN_NODE_SOCKET`, the lock taken by every server with the stale-socket clean-up under it, `GET /api/v1/node` and the wire version, and `TianWenNodeClient` and the event stream over the socket (`NodeTransport`), in #918; `tianwen-server` built and published beside the GUI, the CLI and the functional tests, and `tianwen-ascomhost` beside the server (`src/ExeBeside.targets`), in #919; the keeper (`--keeper`, restart after a crash, stop after a crash loop), `POST /api/v1/node/shutdown` (socket only), `HoldsHardware`, `setsid()` and null stdio on Unix, file-only logging for a spawned node, logs rolling at local midnight, `--fake-devices` and `TIANWEN_DATA_ROOT` for a node that touches neither hardware nor the user's data, in #920; the client's launcher (`LocalNodeLauncher`: find, else start the keeper from the client's own directory with job breakaway on Windows, readiness, the version handshake, the `localhost:1888` probe, the clock hand-off through `TIANWEN_CLOCK_OFFSET`), in #921; the LAN share (`PUT /api/v1/node/share`, socket only: the machine setting, the logon entry on each OS, a spawned node listening on the LAN only while shared, an idle one restarting to apply it), in #922; the active profile as persisted node state and the node's pinned serial ports from it (`NodePinnedSerialPorts`, the provider asynchronous so it reads the profile as it is now), in #923; the presence beat for prompts (a client beats from the loop that draws it, and the node holds a prompt only for a client whose beat is fresh, so a frozen window no longer holds the night), in #924; the crash journal written and reported (`node.journal` of the devices, the run and each camera's cooler intent, which the hub keeps where a cooler is commanded; `--after-crash <pid>` from the keeper; the stale guard from the OS boot time, Kernel-Boot event 27 on Windows; `GET /api/v1/node`'s `Recovery` and `DELETE /api/v1/node/recovery`), in #925; acting on it (the devices reconnected, the mount first, each named in the journal before its connect; each camera re-cooled from its intent through the session's own ramp, lifted into `CameraCoolingRamp`; the crash-loop guard naming the device the last node was reaching for), in #926; the job model shipped in P0b (#916). **The rest:** Local node transport and lifetime: `--socket` and `--node-socket`; the lock, taken by every server; `GET /api/v1/node` and the wire version; the localhost probe for another account's node; spawn with breakaway or `setsid`, stdio and working directory; the keeper; stay-until-logoff; the version handshake; the clock hand-off; the persistent LAN share and its logon start; pinned serial ports from the persisted active profile; the job model; a client presence heartbeat for prompts; the crash journal and stale-socket clean-up; `TianWenNodeClient` and the event stream over the socket; `tianwen-server` and `tianwen-ascomhost` built and published INTO the GUI's and the CLI's own output directories (a build-only reference), so "spawned from the client's own directory" holds in a checkout as well as in a release | functional tests spawn a real server on a temp socket with fakes, kill it mid-run, and see the keeper start the next one, which reconnects from the journal; AOT `publish` for the six release RIDs (win-x64 on this desktop, the rest in CI), then run it. **Done** (2026-09-26): the keeper-kill tests are `NodeJournalProcessTests`, killed holding a cooled camera and killed twice for the crash loop; win-x64 AOT-published with no trim or AOT warning of ours, and `NodeKeeperProcessTests`, `NodeJournalProcessTests` and `NodeActiveProfileTests` run against the published binary (`TIANWEN_SERVER_UNDER_TEST`). The other five RIDs are left to the next release's `publish-apps` run: the owner closed #917 on the win-x64 proof rather than dispatch one now, which would also sign and notarize the macOS build |
| **P2** (#929), DONE (2026-09-27) | **Shipped:** the read side (part 1): `GET /api/v1/devices/state` and the `DEVICE-STATE` push, every connected or held device with its lease owner (the lease table on the wire), its reading (a camera with its cooler intent, a focuser, a filter wheel, a mount with its limit verdict, a cover) at the GUI's cadences while anyone watches, never a device a run holds, read through the same `DeviceHubReadingExtensions` as the GUI, in #956; connect, disconnect and warm-and-disconnect as jobs (part 2: `POST /api/v1/devices/connect`, `/disconnect`, `/warm-and-disconnect`, the `disconnect-safety` read before them, a lease refused first and a cold camera unless the client skips the warm-up, one job per device), in #957; a camera's cooling and settings (part 3: cool and warm as ramp jobs recording the cooler intent, cooler off and gain, offset, bin and frame at once and refused while a job holds the camera, `CameraFrameExtensions.SetFrame`), in #958; moving a focuser, a filter wheel or a mount (part 4: jobs that end when the device has settled, a second move refused, a stop that ends its job, the goto lifted into Lib as `MountGoto` so the GUI's and the node's are one, and the session-scoped mount and OTA routes re-pointed at the device plane), in #960; the leased move-axis (part 5: a motion that stops by itself `NodeWire.MoveAxisLease` after the last request asking for it, so a client that dies mid-move no longer leaves the axis running), in #961; the proof (part 6: `EquipmentFlowProcessTests`, the Equipment tab's flows driven through `TianWenNodeClient` over the socket against a spawned node, which found a filter wheel's reading reporting its filter's focus offset as the slot, fixed there), in #962. **The rest:** Session-less device plane: connect / disconnect / warm-and-disconnect as jobs, cooling and camera settings, focuser, filter, mount actions, leased move-axis, snapshot plus `DEVICE-STATE`, the lease table on the wire, mount-limit verdict, `DeviceOwnershipGate` on every actuation; ONE driver per device in the node (P0b 11) | the server functional suite drives the Equipment flows the GUI runs today, end to end, against fakes. **Done** (2026-09-27): `EquipmentFlowProcessTests` |
| **P3** (#930), DONE (2026-09-27) | **Shipped:** the one profile writer (part 1: `NodeProfiles`, each write reading the file under its lock, a revision that is the hash of the stored bytes and a 412 for a stale one, `GET /api/v1/profiles/{id}` carrying the whole profile, `PUT` taking it back, create, edit and delete on the socket only, `PROFILE-CHANGED` for every write), in #963; what the node writes on its own (part 2: the discovery job reconciling every stored profile, the legacy site moved off the mount URI with it, the active profile's mount reconciling its site and its camera recording the sensor on connect, an edited site given to the connected mount when the profile wins and no run holds it, the rules lifted from `EquipmentActions` into Lib for the GUI and the node alike), in #964; the backlash mirror (part 3: a node run's inferred backlash left on its profile's focuser URIs as it ends, and a delete that refuses only the active profile, which a run's always is), in #965; a device's setting and the credential store (part 4: `PUT /api/v1/devices/setting`, socket only, a masked setting into the node's store and never echoed, any other onto the device's URI in the profile, and a data root kept apart keeping its secrets in a file under it), in #966; the proof (part 5: every profile write site in the GUI, TUI and CLI with the node route that carries it, the checklist P6 cuts over by, and parity and end-to-end tests), in #967. **The rest:** Profiles and discovery on the server: the discovery job, whole-profile edits (socket only), `PROFILE-CHANGED`, site reconcile, sensor capture, the backlash mirror, credential store; the server as the one profile writer; the active profile as persisted node state | reconcile and edit parity tests against today's `EquipmentActions` results. **Done** (2026-09-27): `ProfileEditParityTests` (each edit through the node stores what the host's own save stores, by revision), `ProfileFlowProcessTests` (a profile's life through a spawned node) |
| **P4** (#931) | **DONE.** The wire format and its reader (part 1, #969: `FrameWire`, a frame's planes as they are held with its `ImageMeta` in a JSON header, packed to 16 bits exactly when that loses nothing; `FrameReader`, reading into planes it recycles), and the route and the push (part 2, #970: `GET /frames/{source}/latest?after=N` for each OTA and the guider, `FRAME-AVAILABLE`, a 26 MP frame over the socket measured at 37 to 42 ms), compression over TCP only (part 3, #971: Brotli, or gzip, when the client's `Accept-Encoding` asks; never on the local socket), and the client side (part 4, #972: `RemoteSessionMirror`'s slots hold the node's linear frames under a local session's lease contract, so the unchanged `LiveFramePreviewSource` is network-backed; frames follow the rig on screen; a saved sub is its FITS file over the local socket only). **Part 5, the frame on show from the node's own copy, moved to P6**: that copy was to be the P4b slot, deferred, and until the cut the in-process GUI reads the session slot's `Image` directly | a round-trip test that is pixel-exact against the camera's own buffer; a measured 26 MP transfer over the socket on this desktop |
| **P4b** (#932) | **DONE (2026-09-28, the user's call: "get shared memory video working", then "finish 932")**: every frame a client on this machine's socket takes comes through shared memory, a planetary capture's streams (#1035) and every `/frames/{source}/latest` fetch, an OTA's, the guider's. Two slots behind a seqlock, per stream or per fetched source, the frame written as `FrameWire`'s own bytes so one reader serves both carriers (`FrameSlotWriter` / `FrameSlotReader` / `FrameSlotReaders`, `NodeFrameSlots`, `SharedMemorySection`: Windows sections with a per-user DACL, a 0600 file under `/dev/shm` elsewhere); `FRAME-AVAILABLE` stays a hint. A cross-process 26 MP frame measured on Windows and Linux; macOS is #1042. How it differs from the design below and what it measured: "Shipped: shared memory for a local client" | the same pixel-exact test through the slot; a client killed mid-read leaves the server writing; a measured cross-process 26 MP frame on each OS |
| **P4r** (#933) | DEFERRED to a 10.x minor (decision 11): the remote half of "a saved frame is its FITS file": `GET /frames/{id}/fits` with `Range` and the write-time digest, `FreeWhenCopied`, the client's copy policies, the pre-night space check | a remote fetch byte-identical to the node's file, resumed after a dropped connection; a `FreeWhenCopied` node that never deletes a frame with no verified copy |
| **P5** (#934), DONE (2026-09-27) | **Shipped:** the node's run is any kind (part 1, #973: `INodeRun`, started, journaled and refused like a session; a refusal names the run going on), with a dark library the first (`/api/v1/darks`, the camera leased and given back as the run ends); a preview exposure and a snapshot outside a session (part 2, #974: `NodeFrames`, the frame each source shows whoever took it, under one node token per source; the preview a job that leases its camera); a plate solve and a solve and sync as jobs (part 3, #976: the solution kept with the token of the frame it is of, whole on the wire; solve and sync one job holding the mount and the camera); polar alignment as the node's run and the detach grace (part 4, #978: `PolarAlignmentRun` lifted into Lib for the GUI and the node alike, `/api/v1/polar` with its frames the OTA's and its state whole on the wire, an interactive run stopped once no client has been present for 60 s by `NodeRunWatch`, and a stop that names the run it means); the planetary capture loop lifted into Lib (part 5a, #979: `PlanetaryCapture` for the GUI and the node alike, and its recenter's mount nudge asking the ownership gate); a live planetary capture as the node's run (part 5b, #983: `/api/v1/planetary` with its controls, the start in two halves so the camera is claimed as the request is answered and the loop streams only on the node's token, the rolling stack taken on the node, the live frame (`FrameSampler`, display rate) and the linear master served as frames, and a new window snapped to the camera's rule for every host); both frames as drop-to-latest streams (part 5c, #987: a WebSocket per source on which the client ASKS for each frame and the node answers with the newest, since a socket buffers dozens of frames and a node that sent on its own fed a paused reader every stale one; the live frame dated as it arrived and shown at a display's rate); a recording to disk (part 5d, #988: `/api/v1/planetary/record` into a SER file beside the snapshots, `SerRecording` in Lib converting on the capture loop and writing on a task of its own so a slow disk drops counted frames and never the capture's rate, and a capture that records not interactive, so the recording finishes its duration unwatched and the live view after it gets a grace of its own; #814's memory-mapped ring is the optimisation still ahead). Found on the way: an attached client's event socket held every node stop for the whole 30-minute shutdown budget, the run's `Finalise` and the warm-up included (#985, fixed in #986: a request that lasts ends as the host starts stopping). Polar alignment's start now refuses a device a job is working on, as the planetary, dark library and preview starts do (#981: a job holds its device in the node's jobs, not the lease). **The proof** (#991): every kind over a spawned node's socket with its client gone mid-run, the grace shortened for it by `--detach-grace`; a planetary live view and polar alignment stop cleanly once the grace is spent and a client back within it keeps them, a dark library and a session go on. It found that no client could read a session's state before its first frame (a required filter name, null in a default camera state; fixed in #990). **The rest:** run kinds: polar alignment, planetary capture with the rolling stack and recentering, preview / snapshot / solve and sync, a dark library (the CLI's `darks`). Each states what it does when its LAST client detaches: a session and a flat run go on; polar and a planetary live view stop after a grace long enough for a respawned window (P7) to re-attach; a planetary recording to disk finishes its duration. (P0a stops polar and planetary on a dead GPU for the same reason: interactive, meaningless unseen.) | each mode over a fake rig through the socket, with a client killed mid-run: the session goes on, polar stops cleanly after the grace, and a client back within the grace keeps it |
| **P5b** (#935), DONE (2026-09-27) | **Shipped** in nine parts (P5b above): the parity harness (#993); NaN crosses as null (#995); lossless state (#996); a run is on and says so (#997, #998); control goes to the rig's own node (#999); every event and a redraw (#1004), a quiet rig says so and its feed is its node's (#1010); incremental polling (#1011); the rig's own site, sensor and schedule (#1014); the tabs drawn both ways in the harness (#1015) and an idle rig's devices from its node (part 9b). The known-gap list holds only P6's (the frame on show). **The rest:** Mirror parity: the mirror sets `IsRunning`, the abort route, the pending prompt and the notification feed; handles every event the node sends (it handles two of seven today); the WHOLE `SessionConfiguration` on the wire; lossless mount state (J2000, altitude, axis angle), frame metrics, guide stats, settle progress, star profile, calibration overlay, backlash, scouts and each observation's `CatalogIndex`; NaN crosses as null, never 0; prompts and abort routed to the Active context's own node; incremental polling instead of the whole night's log and guide ring every 500 ms | ONE fake session rendered in-process and through a mirror over a real server gives equal `LiveSessionState` snapshots at every phase (a golden parity test), and the Live Session, Guider and Home tabs lay out identically both ways |
| **P6** (#936) | **The cut, GUI, TUI and CLI in one wave** (P8 folded in: the TUI builds the same `AppSignalHandler`, so two backends would otherwise live on). Each drops `IDeviceHub`, every device source and `TianWen.Devices.Native`; Local means the local node; the quit dialog (decision 1, last client only); the GUI's and the TUI's `MountLimitWatcher` deleted; the gates re-sourced from the node; the CLI's `darks`, `flats`, `device` and `profile` verbs and the first-run wizard become node clients; inspector snapshot fields read the mirror; `unattended-ui-driving.md` and the E2E harness spawn a server; CLAUDE.md's telemetry ground truth moves to `Server_*.log`; packaging ships `tianwen-server` and `tianwen-ascomhost` inside the GUI's and the CLI's archives and the GUI's `.app` (every file in `Contents/MacOS` signed); the frame on show from the node's own copy (moved from P4 part 5: a saved sub's is its file, and the camera gets its array back after the write) | the unattended-driving flows pass unchanged against a spawned server; killing the GUI mid-session leaves the session running; a new GUI re-attaches to it; a CLI `device warm` during a GUI session is refused by the lease, not raced |
| **P6b** (#1021), DONE (2026-09-28) | **Shipped** in #1026. Control over the LAN by grant (decision 13): seeing is free and every command over TCP needs control; a TianWen client asks and the rig's machine answers (`LanInvites`), a grant remembered until revoked (`LanGrants`); another app's refused command is its request, allowed until the node restarts or always by forward-confirmed host name; managed from this machine or a granted client, in a Sharing panel on the rig's Home card; presence and "the last client" count only clients that can command | a laptop's window over TCP is refused a command, asks, is allowed from the rig's machine, commands, keeps control across restarts of both, and is refused again once revoked; an Alpaca PUT over TCP is refused, allowed, and refused again after the node restarts, unless always allowed by its host name |
| **P7** (#937) | **Shipped:** GPU recovery by respawn (`GuiSuccession`): a lost display (`OnGpuWedged`) or a failed loop starts a successor GUI, the same executable and arguments marked by the environment, and the window leaves with no Vulkan teardown; the successor waits for its predecessor to exit before it claims `InstanceGate`, and a boot loop stops after three replacements in a row, reset by five minutes of healthy drawing; `gpu-device-recovery.md` says in-process recreation is now optional. **The rest:** the live proof, with P6's | `gpu_fault lost` mid-session: a new window appears on the same session within seconds |
| **P8** | Folded into P6 (#936) on 2026-09-25 | |
| **P9** (#938) | **Release 10.0**: `VersionMajorMinor` 9.0 to 10.0 with its `CHANGELOG.md` entry in the same commit (`/bump-version`), saying what breaks: closing a window no longer stops the rig; every host needs `tianwen-server` beside it; a spawned node stays off the LAN until shared; profiles are edited through the node; and the `TianWen.Lib` API that moved (`WarmAndDisconnectAsync` into Lib, the job and node DTOs). Then `/release-tianwen` | each release archive carries the server and runs a fake night end to end on win-x64 |

## Decisions (all made by the user; 1 to 7 and 9 to 12 on 2026-09-25, 13 on 2026-09-28)

1. **Closing a window: DECIDED, it asks, and only the last client asks.** With a run active (a session,
   flats, polar or planetary), the dialog offers "Leave the rig running" (the default) and "Stop the rig
   and quit" (abort, `Finalise`, warm-up, disconnect, with progress). With devices connected and no run,
   it offers "Warm up and disconnect" (the default, today's behaviour, finished in the server after the
   window has gone) and "Leave connected". **Refined 2026-09-28 (user): it speaks of a warm-up only while a
   camera is actively cooled, below ambient** (`CameraReading.NeedsWarmUp`, the rule the node's ramp asks
   too); otherwise the default is "Disconnect". Found quitting after a planetary capture with an uncooled
   ASI462MC, which the node's ramp then stepped toward +25 °C for its whole 15 minute cap. A client that is not the last one attached detaches without
   asking, so closing the RDP window never warms a rig the laptop is watching.
2. **When a spawned server exits: DECIDED, it stays until logoff** (the recommendation was 10 minutes
   idle). It still never exits while it holds hardware, and one started by hand never exits by itself.
3. **LAN exposure: DECIDED, off, with a share setting that the machine KEEPS** (user: "the share setting
   should be perm, so I can have my NUC controlled remotely without RDP'ing"). While it is on, the node
   listens on TCP 1888, announces itself, and **starts at logon by itself** (also decided 2026-09-25),
   so a NUC that logs on automatically is reachable after any reboot. A server run by hand keeps its
   current default. Mechanics under "Spawn and lifetime".
4. **Profile editing: DECIDED, local-socket clients only.** A LAN client reads profiles and never
   changes them, today's remote-rig rule.
5. **The planetary stack: DECIDED, in the server**, since only the master and a live frame cross.
6. **The wire format: DECIDED, float planes plus the `ImageMeta` header, packed losslessly to 16-bit when
   every sample allows it**, carried as bytes over the socket (and, once P4b ships, shared memory for a
   local client), compressed only over TCP and only when the client asks (measured: "Compression").
   Streaming FITS was the alternative; a socket cannot seek and parts of our FITS read path do. It covers
   only frames that are never saved: a SAVED frame is its FITS file for every client.
7. **Migration: DECIDED, one wave**, the server surface first, then the GUI, the TUI and the CLI's
   hardware commands cut over together (P6) and released together (P9). No startup flag keeps two
   paths alive.
8. **After a server crash: DECIDED 2026-09-24, the crash journal** (user: "yes we do need that crash
   journal", and it must re-establish the cameras' cooling). The next server reconnects what the dead
   one held, restoring mount-limit enforcement and each camera's cooling from its recorded intent. A
   client offers "Stop the rig safely" or "Start the session again", never a silent resume. Its design
   and its two guards are under "When the server dies".
9. **Frame storage: DECIDED, the node keeps the original (2026-09-24), and the defaults are the node's
   `Keep` and the client's `OnOpen` (2026-09-25).** `FreeWhenCopied` is only ever an explicit setting on
   a node short of storage, and `Mirror` is one setting away on each binding. They take effect with the
   remote half (P4r).
10. **A keeper restarts a crashed server: DECIDED** (new, 2026-09-25). Without one, a server that crashed
    while no window was open left the mount unguarded until a client next started.
11. **10.0's scope: DECIDED, shared memory (P4b) and the remote saved-frame half (P4r) are deferred**
    (new, 2026-09-25): 10.0 is the local split, and both only add. **Revisited 2026-09-28 for P4b** (user: "get shared
    memory video working", then "finish 932"): every frame a local client takes goes through shared memory. P4r stays
    deferred.
12. **No GPU work in the process that owns the rig: DECIDED** (new, 2026-09-25). Enhancing is done
    client-side ("ideally enhancing is done client side anyway"); the node keeps `/image/enhance` "for
    more advanced usages", and it answers 409 while the node holds any device or run.

13. **Control over the LAN: DECIDED, see by default, command by grant** (2026-09-28; user: "remote can see, but not
    manipulate by default. we use the existing LAN acceptance way where a locally pipe connected client can accept
    requests from other clients, which then gain the capability"). A grant is remembered until revoked; aborting a run
    and answering its prompts need one too (one rule); it lands before 10.0. Other apps (Alpaca, ninaAPI), which cannot
    ask, are allowed by their network identity (user: "we can allow right access to other alpaca etc clients based on
    their network identity"): until the node restarts, or always by a forward-confirmed reverse lookup of the host name
    (user: "we could do a reverse lookup and find the hostname"). Managed from this machine or a client granted control,
    in a Sharing panel on the rig's Home card. The handshake and the grants are LAN.Lib's, not copies (user: "possible
    to factor this out?"). Mechanics under "P6b: control over the LAN by grant".

Adopted without a question, as the review's defaults: a client probes `localhost:1888` for a node under
another account before spawning (and uses it as the Local rig when one answers); every server takes
the lock and binds the default socket however it was started; the socket and the journal live in the
AppData root, not `$XDG_RUNTIME_DIR`; the child calls `setsid()` itself instead of `posix_spawn`.

## Open questions (engineering, not the user's)

- **OS shutdown and logoff give a server seconds, not the 15 minutes a warm-up ramp takes.** What is the
  least-bad stop: cooler off at once, or leave the cooler running at its setpoint? With decision 2 the
  server lives until logoff, so this is its normal end, not a corner case. On Windows a process with a
  top-level (hidden) window can hold the shutdown with `ShutdownBlockReasonCreate`, which puts "TianWen:
  warming the camera" on the shutdown screen and leaves the choice to the user; a console process cannot.
- **ASCOM COM drivers in the server run on MTA thread-pool threads, as they do in the GUI today.** No
  change, but `tianwen-ascomhost` is still not shipped with any app (`ascom-oop-host.md`), and its
  kill-on-close job is now correctly scoped to the server. It is looked for beside the RUNNING exe, which
  after the split is the server, so it ships there (P6). There is no ASCOM setup dialog or chooser
  anywhere in the GUI (the review), so nothing needs the server to show a window.
- **`user.config`-scoped ASCOM driver settings do not travel between host processes** (the Gemini
  `MyComPort` bug, `ascom-oop-host.md`). Check each driver that keeps its own settings, because the
  process that opens it changes at P6.
- **Two processes each load the catalogues.** The GUI's sky map and planner and the server's plate
  solver and scheduler both hold the object database and Tycho-2. The memory table above counts frames
  only; measure both processes' working sets at P6 against today's one, and let the server load a
  catalogue only when a solve or a schedule first asks.
- **Two GUI windows on one machine** (the GUI is single-instance today through `InstanceGate`, opt-out
  `TIANWEN_GUI_SINGLE_INSTANCE=0`) would be two peers of one node, which the design allows. P7's
  successor window must wait for its predecessor to release the gate before claiming it. **Answered in
  P7 (#937)**: the successor waits for its predecessor's EXIT (`GuiSuccession.WaitForPredecessorAsync`,
  15 s), and the predecessor releases the gate before it exits.
- ~~Canon EVF and live stacking have no server endpoints and no row above.~~ Closed by the review: the
  GUI has no Canon live view, and "live stacking" is the planetary stack, which is P5.
