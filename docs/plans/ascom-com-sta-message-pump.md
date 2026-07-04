# ASCOM COM drivers: STA + message-pump host (plan)

**Status: NOT STARTED (design + finding captured).** Found during the Gemini FlatPanel Lite bring-up
(branch `fix/gemini-flat-panel`) while trying to cross-check the panel via the vendor ASCOM driver.
TianWen's ASCOM-over-COM path (`AscomDevice` / `AscomDispatchDevice`) cannot reliably **connect** an
important class of vendor drivers headlessly — they hard-crash the process on connect.

## Root cause — vendor drivers busy-spin `Application.DoEvents()` on connect

Decompiling `ASCOM.GeminiFPLite.CoverCalibrator` (v6.6) showed its `Connected` setter calls
`PauseForTime` six times during connect, and `PauseForTime` is:

```csharp
public void PauseForTime(int s, int ms) {
    var end = DateTime.Now.Add(new TimeSpan(0,0,0,s,ms));
    while (end >= DateTime.Now) { Application.DoEvents(); }   // busy-spin the WinForms message queue
}
```

So the driver **busy-spins `Application.DoEvents()` for ~1.1 s on connect**. In a real WinForms/WPF
app (NINA) there's a UI thread + message pump, so `DoEvents` is harmless. In a **headless** process
(a `dotnet test` host, pwsh, `tianwen-server`, or a non-UI connect path) there's no message loop, and
`DoEvents` spinning in that context faults the process — observed as exit `0xC0000409`
(STATUS_STACK_BUFFER_OVERRUN) with no catchable managed exception. NINA's log connecting the same
driver is clean (v2.6, no errors), confirming it's the headless environment, not the panel.

The decompile also independently confirmed the native Gemini protocol facts (recorded in
[../architecture/gemini-flatpanel-lite-protocol.md](../architecture/gemini-flatpanel-lite-protocol.md)):
`*` response sigil, `Thread.Sleep(2000)` boot wait, DTR+RTS asserted, `DiscardInBuffer` before every
command.

## Survey — how widespread (drivers registered on the bring-up box)

`DoEvents` present in the driver assembly (grep of the registered `*.dll`):

| DoEvents | Driver | On connect path? |
|---|---|---|
| yes | `ASCOM.GeminiFPLite.CoverCalibrator` | **CONFIRMED** (PauseForTime in `Connected` setter) |
| yes | `ASCOM.GeminiFocuserPro.Focuser` | **CONFIRMED** — `PauseForTime(10,0)` (up to 10 s!) in connect |
| yes | `ASCOM.iOptron2017.Focuser` | suspected (same signature; not traced) |
| yes | `ASCOM.iOptron2017.Telescope` | suspected |
| yes | `ASCOM.QHYFWRS232.FilterWheel` | suspected |
| ? | `ASCOM.GS.Sky.Telescope` (GS Server) | **slow connect CONFIRMED** — ~5.4 s wall-clock on `Connected = true` (GUI log 2026-07-04, 18:10:33→18:10:39); DoEvents not source-verified but the blocking-connect signature matches |
| no | ZWO EAF/EFW, PlayerOne, ASI, QHYCCD cameras, DeepSkyDad, SynScan, qfoc, **OmniSim** | clean |

It's a **vendor cluster** (Gemini Astro + iOptron copy-paste the same `PauseForTime`), not universal:
our `AscomDeviceTests` connect the **OmniSim** camera/focuser/FW/cover green **headless**, and the
mainstream imaging drivers (ZWO/ASI/PlayerOne/QHYCCD) are clean. But it's enough drivers that TianWen's
Windows COM path can't be trusted to connect an arbitrary vendor driver from a headless host.

## Mitigation — dedicated STA thread with a real message pump

Host all ASCOM COM driver calls on a **dedicated STA thread that runs an actual message loop**
(`Thread` with `SetApartmentState(STA)` + `Application.Run` on an invisible `ApplicationContext`, or a
manual `GetMessage`/`DispatchMessage` loop), and marshal every `AscomDispatchDevice` call onto it. Then
a driver's `DoEvents` finds a pump, hidden-window `SerialPort` event marshalling works, and modal
`MessageBox` calls (some drivers) have a UI thread to post to — i.e. TianWen tolerates these drivers
the way a GUI app does. "Correctness over elegance, pragmatic" — the goal is that users never hit a
mystery `0xC0000409` on connect.

### Sketch

- A single long-lived STA "COM apartment" thread owned by the ASCOM device source; a
  `SynchronizationContext` / bounded work queue to `Invoke`/`InvokeAsync` COM calls onto it.
- `AscomDispatchDevice` routes its `Dispatch` invokes through that context (create the COM object ON
  the STA thread too — COM apartment affinity).
- Message pump runs between/around invokes so `DoEvents` and driver-internal window messages drain.
- Keep the existing `SafeGet`/`ResilientCall` error handling on top.

## Phasing (proposed)

| Phase | Scope |
|---|---|
| P1 | STA message-pump apartment host + route `AscomDispatchDevice` construction/calls through it; verify a `DoEvents` driver (Gemini FlatPanel or Focuser Pro) connects headlessly (extend `TianWen.Lib.Tests.Simulators` ASCOM leg, gated). |
| P2 | Confirm the suspected iOptron/QHYFWRS232 drivers; make it the default for all ASCOM COM. |
| P3 | Document + note in CLAUDE.md that ASCOM COM connect is STA-pumped (removes the "some vendor drivers crash headless" caveat). |

## Notes / relationship to other work

- **The native driver dodges all of this.** For the Gemini FlatPanel, TianWen's own serial
  `GeminiFlatPanelDriver` (this branch) connects clean, headless, cross-platform — no COM, no
  `DoEvents`. Same argument would apply if we ever do native Gemini Focuser / iOptron. So this plan is
  about making the *fallback* (vendor ASCOM COM on Windows) robust, not the primary path.
- Verifying a `DoEvents` driver headlessly is itself hard (it crashes the test host); the STA-pump host
  is exactly what lets a gated integration test drive it without crashing.
- **Interim mitigation shipped (2026-07-04, this branch):** the GUI no longer *freezes* on these
  drivers because device connect/disconnect is offloaded off the render thread
  (`AppSignalHandler.RunDeviceOpOffRenderThreadAsync` — see [ui.md](../todo/ui.md)). The busy-spin
  still burns ~1–5 s on a thread-pool thread, but the render loop stays live. This is a freeze
  mitigation, **not** the correctness fix — the STA-pump host is still needed because (a) an MTA
  pool thread has no message pump, so a driver relying on `DoEvents`/`Control.Invoke` to update its
  own UI still misbehaves, and (b) a no-pump host can still hard-crash on the worst offenders.
- **GS Server (GSS) form-refresh + hub-connection finding (2026-07-04).** GSS is an out-of-process
  COM *hub/server*, not a plain driver. A client's `Connected = true` attaches the **client** but
  does **not** force GSS's own link to the mount/simulator — so GSS's form can show *disconnected*
  even though our connect succeeded, until the user connects inside GSS (or enables its
  auto-connect-on-client option). Two consequences: (1) the STA-pump host would likely also fix the
  vendor-form-refresh symptom (GSS updates its form via a message pump the MTA pool thread lacks);
  (2) for a hub, "client connected" ≠ "mount live" — a session could start against a driver that
  reports connected but returns RA=0. That motivates the **post-connect mount liveness probe** open
  item in [../todo/drivers.md](../todo/drivers.md).
