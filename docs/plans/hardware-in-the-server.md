# Hardware in the server: the GUI drives every device through a local `tianwen-server`

**Status: PLANNED (2026-09-24, raised by the user); reviewed against `main` 2026-09-25, every decision
made; P0a DONE (2026-09-25, #743), the rest not started.** Issue #751. P0 is urgent on its own: it
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
    - What it leaves, both older than it: two connects of one device at once can still leave two
      drivers, and a driver that dropped is replaced rather than reconnected (#806, with P2's jobs).
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
    serial probe half-way. It becomes a job (P1's long-operation model).
18. **The ninaAPI shim and the profile endpoints skip their own gates.** `GET /v2/api/profile/switch`
    switches the active profile ungated (the native `PUT /session/profile` asks `ProfileSwitchGate`),
    and `DELETE /profiles/{id}` deletes a profile without asking whether it is active or running.
    **FIXED**: the ninaAPI switch asks `ProfileSwitchGate`, and a delete refuses the node's active
    profile and, while a run is going, any profile. The node does not record which profile a run was
    started from, so it cannot tell the one in use from the rest; refusing all of them for the length
    of a run is the safe answer.

## P0c: bugs in the hosts that the split would carry over (#788; review, 2026-09-25)

Present-day bugs outside the server. Each would reappear inside the server or in the cut, so they go
first; none needs a decision.

1. **Quitting the TUI hangs.** Q or Ctrl+C breaks the input loop, then `tracker.DrainAsync()` awaits
   every background task WITHOUT cancelling it. The TUI's `MountLimitWatcher` loops until its token is
   cancelled, and nothing in `TianWen.Cli` calls `Cancel`, so the alternate screen freezes; Ctrl+C is
   swallowed (`TreatControlCAsInput` is reset only at the very end); a running session is awaited, not
   aborted; and were the drain ever to return, disposing the host would force-disconnect every device
   cold. The TUI gets the GUI's quit rule (cancel, warm, disconnect, bounded), which P6 then replaces
   with the quit dialog for both.
2. **Polar alignment and planetary capture take no device lease.** Only `Session.RunAsync` and
   `RunFlatsOnlyAsync` acquire one, and the gate is lease-only, so nothing stops a jog or a second run
   from moving a mount polar is rotating, or reconfiguring a camera planetary is streaming, although
   `AppSignalHandler` assumes otherwise. Two smaller holes: the preview Capture button stays live
   during a flat run, and `WarmAndDisconnectAsync` ramps a LEASED camera's cooler before the disconnect
   is refused. Both run kinds claim their devices through `DeviceLeaseSet`, at the Lib level, so the
   server's P5 inherits the claim instead of re-inventing it.
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
   cache.
4. **On Linux, where lights land depends on the working directory.** `ImageOutputFolder` calls
   `GetFolderPath(MyPictures)` without the create option, so on a box with no `~/Pictures` it resolves
   to a relative `TianWen` folder, and a spawned server's working directory would decide where a
   night's subs go. It resolves to an absolute path (created if missing), and the spawner sets the
   child's working directory anyway (P1).

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
  job. **Spike this under `tools/start-app.ps1`, Visual Studio and the MSIX-less release build.**
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
  (the lock rule above). Once P4b ships (deferred, decision 11), a shared-memory section behaves per OS:
  - Windows keeps a section alive while the client still maps it, so the client drops its mappings when
    it reconnects.
  - A POSIX `shm_open` object persists until it is unlinked, so the new lock holder unlinks any object
    left under the node's prefix.
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
request keep its short budget: today `/devices/discover` runs inline on a 10 s client budget and is cut
off mid-probe (P0b 17).

**A prompt is held only while a client can SEE it.** Liveness today is "a socket is registered"
(P0b 13), and a frozen window keeps its socket open, so a GPU wedge that freezes rather than crashes
would hold a prompt, and with it the night, for ever. So each client beats from its FRAME loop, never
from its socket's own thread (the GUI from `OnPostFrame`, the TUI from its render tick); a client whose
beat lapses for a few seconds stops counting as an observer, and a prompt with no client that can see
it gets the session's unattended answer, exactly as with none attached. Every client sees every
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
  (104 MB), extrapolated. Measure a real frame before designing anything faster. The reader copies the
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
| **P1** | Local node transport and lifetime: `--socket` and `--node-socket`; the lock, taken by every server; `GET /api/v1/node` and the wire version; the localhost probe for another account's node; spawn with breakaway or `setsid`, stdio and working directory; the keeper; stay-until-logoff; the version handshake; the clock hand-off; the persistent LAN share and its logon start; pinned serial ports from the persisted active profile; the job model; a client presence heartbeat for prompts; the crash journal and stale-socket clean-up; `TianWenNodeClient` and the event stream over the socket; `tianwen-server` and `tianwen-ascomhost` built and published INTO the GUI's and the CLI's own output directories (a build-only reference), so "spawned from the client's own directory" holds in a checkout as well as in a release | functional tests spawn a real server on a temp socket with fakes, kill it mid-run, and see the keeper start the next one, which reconnects from the journal; AOT `publish` for the six release RIDs (win-x64 on this desktop, the rest in CI), then run it |
| **P2** | Session-less device plane: connect / disconnect / warm-and-disconnect as jobs, cooling and camera settings, focuser, filter, mount actions, leased move-axis, snapshot plus `DEVICE-STATE`, the lease table on the wire, mount-limit verdict, `DeviceOwnershipGate` on every actuation; ONE driver per device in the node (P0b 11) | the server functional suite drives the Equipment flows the GUI runs today, end to end, against fakes |
| **P3** | Profiles and discovery on the server: the discovery job, whole-profile edits (socket only), `PROFILE-CHANGED`, site reconcile, sensor capture, the backlash mirror, credential store; the server as the one profile writer; the active profile as persisted node state | reconcile and edit parity tests against today's `EquipmentActions` results |
| **P4** | Linear frames: the binary format, `FRAME-AVAILABLE`, guide frames, compression negotiated over TCP only; a network-backed `LiveFramePreviewSource`; a saved frame read as its FITS file through `FitsReader` (#755, shipped 2026-09-25) | a round-trip test that is pixel-exact against the camera's own buffer; a measured 26 MP transfer over the socket on this desktop |
| **P4b** | DEFERRED past 10.0 (decision 11): the shared-memory carrier (two slots per source, a seqlock per slot, the server writer as a `ChannelBuffer` borrower and the client reader as a driver-shaped recycling source, Windows sections with a per-user DACL, `shm_open` on Linux and macOS), behind a measurement that asks for it | the same pixel-exact test through the slot; a client killed mid-read leaves the server writing; a measured cross-process 26 MP frame on each OS |
| **P4r** | DEFERRED to a 10.x minor (decision 11): the remote half of "a saved frame is its FITS file": `GET /frames/{id}/fits` with `Range` and the write-time digest, `FreeWhenCopied`, the client's copy policies, the pre-night space check | a remote fetch byte-identical to the node's file, resumed after a dropped connection; a `FreeWhenCopied` node that never deletes a frame with no verified copy |
| **P5** | Run kinds: polar alignment, planetary capture with the rolling stack and recentering, preview / snapshot / solve and sync, a dark library (the CLI's `darks`). Each states what it does when its LAST client detaches: a session and a flat run go on; polar and a planetary live view stop after a grace long enough for a respawned window (P7) to re-attach; a planetary recording to disk finishes its duration. (P0a stops polar and planetary on a dead GPU for the same reason: interactive, meaningless unseen.) | each mode over a fake rig through the socket, with a client killed mid-run: the session goes on, polar stops cleanly after the grace, and a client back within the grace keeps it |
| **P5b** (new) | Mirror parity: the mirror sets `IsRunning`, the abort route, the pending prompt and the notification feed; handles every event the node sends (it handles two of seven today); the WHOLE `SessionConfiguration` on the wire; lossless mount state (J2000, altitude, axis angle), frame metrics, guide stats, settle progress, star profile, calibration overlay, backlash, scouts and each observation's `CatalogIndex`; NaN crosses as null, never 0; prompts and abort routed to the Active context's own node; incremental polling instead of the whole night's log and guide ring every 500 ms | ONE fake session rendered in-process and through a mirror over a real server gives equal `LiveSessionState` snapshots at every phase (a golden parity test), and the Live Session, Guider and Home tabs lay out identically both ways |
| **P6** | **The cut, GUI, TUI and CLI in one wave** (P8 folded in: the TUI builds the same `AppSignalHandler`, so two backends would otherwise live on). Each drops `IDeviceHub`, every device source and `TianWen.Devices.Native`; Local means the local node; the quit dialog (decision 1, last client only); the GUI's and the TUI's `MountLimitWatcher` deleted; the gates re-sourced from the node; the CLI's `darks`, `flats`, `device` and `profile` verbs and the first-run wizard become node clients; inspector snapshot fields read the mirror; `unattended-ui-driving.md` and the E2E harness spawn a server; CLAUDE.md's telemetry ground truth moves to `Server_*.log`; packaging ships `tianwen-server` and `tianwen-ascomhost` inside the GUI's and the CLI's archives and the GUI's `.app` (every file in `Contents/MacOS` signed) | the unattended-driving flows pass unchanged against a spawned server; killing the GUI mid-session leaves the session running; a new GUI re-attaches to it; a CLI `device warm` during a GUI session is refused by the lease, not raced |
| **P7** | GPU recovery by respawn: `OnGpuWedged` starts a successor GUI and exits; `gpu-device-recovery.md` updated (in-process recreation becomes optional) | `gpu_fault lost` mid-session: a new window appears on the same session within seconds |
| **P8** | Folded into P6 (2026-09-25) | |
| **P9** (new) | **Release 10.0**: `VersionMajorMinor` 9.0 to 10.0 with its `CHANGELOG.md` entry in the same commit (`/bump-version`), saying what breaks: closing a window no longer stops the rig; every host needs `tianwen-server` beside it; a spawned node stays off the LAN until shared; profiles are edited through the node; and the `TianWen.Lib` API that moved (`WarmAndDisconnectAsync` into Lib, the job and node DTOs). Then `/release-tianwen` | each release archive carries the server and runs a fake night end to end on win-x64 |

## Decisions (all made by the user; 1 to 7 and 9 to 12 on 2026-09-25)

1. **Closing a window: DECIDED, it asks, and only the last client asks.** With a run active (a session,
   flats, polar or planetary), the dialog offers "Leave the rig running" (the default) and "Stop the rig
   and quit" (abort, `Finalise`, warm-up, disconnect, with progress). With devices connected and no run,
   it offers "Warm up and disconnect" (the default, today's behaviour, finished in the server after the
   window has gone) and "Leave connected". A client that is not the last one attached detaches without
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
    (new, 2026-09-25): 10.0 is the local split, and both only add.
12. **No GPU work in the process that owns the rig: DECIDED** (new, 2026-09-25). Enhancing is done
    client-side ("ideally enhancing is done client side anyway"); the node keeps `/image/enhance` "for
    more advanced usages", and it answers 409 while the node holds any device or run.

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
  successor window must wait for its predecessor to release the gate before claiming it.
- ~~Canon EVF and live stacking have no server endpoints and no row above.~~ Closed by the review: the
  GUI has no Canon live view, and "live stacking" is the planetary stack, which is P5.
