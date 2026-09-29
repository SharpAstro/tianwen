# Device ownership (the hub lease)

The full write-up of the rules CLAUDE.md keeps in short form. The lease is `IDeviceHub.TryAcquireLease`; the verdict is `DeviceOwnershipGate`.

## The rules in full (moved from CLAUDE.md, 2026-09-29)

A run that is driving hardware **claims it from the hub**, and nothing else may disconnect or command a
claimed device. `IDeviceHub.TryAcquireLease` / `DeviceLeaseSet.Acquire` (all-or-nothing over a rig) /
`DeviceOwnershipGate.Evaluate` (the one shared verdict + `Describe()` message, mirroring
`ProfileSwitchGate`). `Session.RunAsync` and `RunFlatsOnlyAsync` claim `Setup.DeviceUris()` for the
whole run, released in the `finally` so a claim survives `Finalise` (parking + warming is exactly when a
stray disconnect hurts most). **Polar alignment claims the mount and its capture devices, and a
planetary capture its camera** (P0c item 2): the host takes the claim with `DeviceLeaseSet.TryAcquire`
(a refused start is an answer, not an exception) and the run OWNS it from there, the polar session
releasing it only after restoring the mount. Every new kind of run owes the same.

- **A run's drivers ARE the hub's, so a node holds ONE driver per device.** A session connects each
  device through the hub (`ControllableDeviceBase.ConnectAsync(hub)`, which calls `IDeviceHub.AdoptAsync`),
  and **reads `Driver` only after that connect**, which can switch it to the hub's instance. Every run
  leaves the mount and guider as entries whose driver is down, which is why `ConnectedDevices` lists only
  drivers that are up. `docs/architecture/hosting-api.md`, invariant 6.
- **Reads are never leased.** Telemetry, status and previews stay free for every observer; watching a
  rig must cost it nothing. A lease only refuses *taking the driver away* and *commanding it*.
- **Never guard hardware access on a UI flag.** The guards used to be five ad-hoc
  `LiveSessionState.IsRunning` checks, every one wrong the same way: `IsRunning` is **false during a flat
  run** (which is why `HasActiveRun` exists), so mid-flat-run the focuser could be jogged, the mount
  pulsed and slewed, and a planetary capture started on the camera being metered. A UI flag also cannot
  work for the hosted API or the Alpaca plane, which never see one. Ask `DeviceOwnershipGate`, which the
  node does: a client (the GUI, the TUI, the CLI) holds no hub since P6 (#936), so it asks the node and shows
  the node's refusal, which names the run.
- **Enforcement is asymmetric, deliberately.** Disconnect has one choke point, so `DisconnectAsync`
  throws `DeviceLeasedException` unless `force: true`; a caller that skips the gate gets an exception,
  not a stolen driver. Actuation has no choke point short of proxying every driver (an interception layer
  on the imaging hot path), so actuation call sites ask the gate. Both evaluate the same rule.
- **`force: true` is for process shutdown only.** Note that GUI "Force Off" does **not** force past
  ownership: it means "skip the warm-up", which is what the user confirmed; consenting to a cold
  disconnect is not consenting to kill the night.
- **Stopping the rig is ONE order, `RigShutdown`** (`TianWen.UI.Abstractions`, a node client since P6):
  the node's run first, through its own ending (a session's and a flat run's `Finalise`, polar's mount
  restore), and the devices warmed up and disconnected, as the node's jobs, only once the node says the run
  has ENDED (`RigShutdownOrderTests`). **Never queue a camera warm-up beside a run's cancel**: that is how
  `Finalise` and the quit once ramped one camera at the same time. A dead display stops nothing: the runs are
  the node's, so the window leaves (P7 starts a successor).
  **Quitting is ONE rule too, `AppQuit`, for the GUI and the TUI** (decision 1 of
  `docs/plans/hardware-in-the-server.md`): only the LAST client attached to the node asks (the node's
  `ClientsAttached`, less this one's own stream, which counts only clients that may command: a watcher over the LAN is
  nobody to leave the rig to); with a run going on, "Leave the rig running" (the default) or
  "Stop the rig and quit"; with devices connected and no run, "Disconnect" (the default, which the node finishes
  after the window has gone) or "Leave connected", and it says "Warm up and disconnect" only while a camera needs it
  (`CameraReading.NeedsWarmUp`: its cooler on and its sensor below the heat sink, the rule the node's ramp asks too, so
  an uncooled camera is never ramped). The question is `LiveSessionState.QuitDialog`
  on this computer's view, drawn by both Live Session tabs over everything (Enter the default, its letter the
  other, Escape stays). Every quit cancels the host's OWN background work first (a separate token from the
  loop's); the TUI once hung on Q, draining a tracker whose limit watcher nothing cancelled (P0c).
- **Escalation is explicit:** stop the run (abort the session / cancel the flat run) and the lease frees.
  There is no override on the actuation path by design.
- `GetDisconnectSafetyAsync` is a **hardware**-safety check (cooler on / mid-exposure) and returns `Safe`
  for anything that is not a camera; it is not, and never was, an ownership check. Ask the gate first.

Pinned by `DeviceOwnershipTests`, including three that drive a real `Session`/flat run end to end.
