# ASCOM COM drivers: out-of-process CET-off host (plan)

**Status: Phases 1–4.5 DONE, Phase 5 PARTIAL** (branch `feat/ascom-oop-host`, 2026-07-04). The real
0xC0000409 driver (`ASCOM.GeminiFPLite.CoverCalibrator`) now connects through the helper without
fastfailing the process (see Phase 5 below). Supersedes the mitigation in
[ascom-com-sta-message-pump.md](ascom-com-sta-message-pump.md) — the STA message pump was the **wrong
fix** (see "Corrected root cause" below).

## The problem (recap)

A cluster of vendor ASCOM COM drivers (Gemini FlatPanel Lite, Gemini Focuser Pro, iOptron, some QHY)
busy-spin `Application.DoEvents()` in their `Connected = true` setter. Driving them from TianWen
(`AscomDevice` / `AscomDispatchDevice`) **hard-crashes the process on connect** with exit `0xC0000409`
(STATUS_STACK_BUFFER_OVERRUN, `ntdll+0x11f1e6`) — no catchable managed exception. NINA connects the
same drivers cleanly.

## Corrected root cause — CET, not the missing message pump

The crash is a **CET (Control-flow Enforcement Technology / hardware shadow-stack) violation**. The
legacy in-proc **.NET Framework 4.x CLR** (`mscoree.dll`, `RuntimeVersion v4.0.30319`) that these
drivers load in-process is **not CET-compatible**; when the driver's `DoEvents` unwinds through the
CLR's JIT-emitted frames, the shadow-stack check trips a native `RtlFailFast`. Windows 10 20H1+/Win11
enforce CET for a process whose main image opts in — and .NET AOT/modern binaries opt in **by
default**, which is why `tianwen-*` crash but NINA (a .NET Framework WPF app, CET off) does not.

Evidence that settled it:
- Four client-side experiments (raw IDispatch, WinForms `Application.Run` pump, `ASCOM.Com.DriverAccess`
  console, a bare-COM pwsh repro) **all crashed** the same way — proving no client-side message pump can
  catch it. The STA-pump plan cannot work.
- The fix is `<CETCompat>false</CETCompat>` on the **executable that loads the driver** — it removes
  shadow-stack enforcement for that process only. Confirmed: NINA ships CET off.
- Drivers that don't crash are either out-of-proc (`LocalServer32`, e.g. GS Server), native-inproc, or
  simply don't `DoEvents` — none load the CET-incompatible inproc Framework CLR on our hot path.

## Chosen architecture — a tiny CET-off helper, main app keeps CET

We do **not** want to disable CET for the whole TianWen product (CET is a real exploit-mitigation, and
we don't ship .NET Framework with the product). Instead: **one small out-of-process helper hosts the
CET-incompatible driver with CET off**, and the CET-on main app drives it remotely over the **same
JSON-RPC protocol PHD2 already uses**, carried on a per-user named pipe (local only).

- **`tianwen-ascomhost`** (`src/TianWen.AscomHost/`): AOT-native, `<CETCompat>false</CETCompat>`,
  Windows-only. Hosts a `DispatchObject` (the AOT-safe raw-vtable IDispatch wrapper) and exposes its
  typed surface over JSON-RPC. ~2.2 MB native, ships no .NET runtime.
- **Detection** (Phase 4): route a device to the helper only when its `InprocServer32` default value
  ends with `mscoree.dll` (a .NET Framework inproc COM server); native/comhost inproc and
  `LocalServer32` drivers stay in-proc, unchanged. Read the registry in the process (x64) bitness view.
- **Cameras stay in-proc** — the `ImageArray` SAFEARRAY is too big to marshal per frame, and no camera
  driver in the crash cluster is one we can't already reach natively.

### Named-pipe transport (Phase 4.5 — replaced loopback TCP)

The transport is a **per-user named pipe**, not a loopback TCP socket. Rationale: a loopback port is
connectable by any local process, shows up in `netstat`, and goes through the full TCP stack where
corporate AV/firewall/EDR products routinely inspect, delay, or block loopback connections — all real
liabilities on the managed Windows boxes this runs on. A named pipe has none of that: no network stack,
no port, no firewall involvement, and an ACL scoped to the current user (`PipeOptions.CurrentUserOnly`).

To avoid a create-vs-connect race, the **parent owns the pipe server**: it creates a
`NamedPipeServerStream` with a GUID name, passes that name to the helper as `argv[0]`, spawns it, then
waits for the helper (the pipe **client**, `NamedPipeClientStream`) to connect. No stdout handshake —
the name is a launch argument, and the server pipe exists before the child starts, so there is no race
and no TOCTOU. `JsonRpcServer` is transport-agnostic (`ServeAsync(IUtf8TextBasedConnection, ct)`); the
helper serves over a `NamedPipeConnection`, and PHD2 keeps its own TCP `JsonRpcOverTcpConnection` (it is
a real network client). Lifetime: when the parent exits the pipe breaks and the helper's serve loop ends
(the job object is the hard backstop).

### STA affinity

`JsonRpcServer` uses `ConfigureAwait(false)`, so handler continuations land on arbitrary thread-pool
(MTA) threads. A legacy COM driver's hidden window has thread affinity, so **every** call to one COM
object must come from **one** thread. The helper therefore owns a single dedicated **STA** thread
(`StaComThread`) and marshals every `DispatchObject` op onto it — the handle table needs no locking
because it is only ever touched there. (The STA thread is the *correct apartment for legacy COM*, not a
crash workaround; the crash fix is CET-off.)

## Wire vocabulary (`AscomComHost`)

Mirrors `DispatchObject`'s typed surface 1:1 so the parent's remote transport (Phase 4) is a mechanical
projection:

| Method | Params | Result |
|---|---|---|
| `create` | `[progId]` | handle (int) |
| `release` | `[handle]` | void |
| `get{Bool,Int,Short,Double,String,DateTime,StringArray,IntArray,Int2DArray}` | `[handle, name]` | value |
| `set{Bool,Int,Short,Double,String,DateTime}` | `[handle, name, value]` | void |
| `invoke{,Bool,Int,Double}` | `[handle, name, ...args]` | void / value |

`DateTime` is ISO-8601 round-trip (`"o"`). Method args are inferred from JSON kind (bool / integral→int
/ real→double / string); a tagged `DateTime` arg encoding is deferred (no crash-cluster driver needs
one). Sub-dispatch returns (`InvokeMethodDispatch` / `GetPropertyDispatch`, e.g. mount `AxisRates`) are
**not yet** wired — cover/calibrator/focuser/FW/switch don't need them; add handle-returning variants in
Phase 4 when generalizing to the mount.

## Phasing

| Phase | Scope | Status |
|---|---|---|
| P1 | Extract the JSON-RPC client buried in the PHD2 driver into a shared `JsonRpcClient` (spine for both PHD2 and the host); refactor `OpenPHD2GuiderDriver` onto it | **DONE** (`d1471d58`; unit + live-PHD2 smoke tests) |
| P2 | `JsonRpcServer` + server-side `JsonRpcOverTcpConnection`; loopback round-trip test | **DONE** (`8ffe2254`) |
| P3 | `tianwen-ascomhost` exe = `JsonRpcServer` + `AscomComHost` handler over `DispatchObject`; port handshake; STA thread; E2E test (spawn exe → handshake → drive real COM); AOT-publish + verify `CETCompat=false` in the PE header | **DONE** (this commit; E2E test green against `Scripting.Dictionary`; PE header confirmed CET-off) |
| P4 | Parent side: `IDispatchTransport` seam (in-proc `DispatchObject` / out-of-proc `RemoteDispatchTransport`); `mscoree` registry detection (`AscomComServerClassifier`); helper process lifecycle (`AscomHostProcess` spawn + connect + `AscomHostJob` kill-on-close); wire so the 8 `AscomXxxDriver` classes stay unchanged | **DONE** |
| P4.5 | Replace loopback TCP with a per-user **named pipe**: transport-agnostic `JsonRpcServer.ServeAsync(IUtf8TextBasedConnection)`, `NamedPipeConnection`, parent-owns-server + GUID-name launch arg (drops the stdout `PORT` handshake). AOT-publish re-verified. | **DONE** (this commit) |
| P5 | Prove cover-first on the **real Gemini FlatPanel Lite** through the helper (the actual 0xC0000409 driver); generalize to the mount (adds sub-dispatch handles — telescope `AxisRates`/`Item`, currently `NotSupported` on the remote transport); confirm win-arm64 publish + ship the helper beside the app | **PARTIAL** — real-driver survival proven (below); panel illumination (needs hardware) + mount sub-dispatch + win-arm64/packaging pending |

### Phase 5 — enemy contact (2026-07-04, dev box)

Tested against the **real ASCOM drivers registered on the bring-up machine**, not just a synthetic COM
object (`AscomOutOfProcessConnectTests`):

- **Classification is correct on real drivers (7/7).** `AscomComServerClassifier` routes
  `ASCOM.GeminiFPLite.CoverCalibrator`, `ASCOM.GeminiFocuserPro.Focuser`, `ASCOM.DeepSkyDad.FP.*`,
  `ASCOM.iOptron2017.*` (all `mscoree.dll`) **out-of-process**, and leaves `ASCOM.Simulator.*` /
  `ASCOM.GS.Sky.Telescope` (`LocalServer32`) **and** `CCDSimulator.Camera` (native in-proc
  `CCDSimulator.dll`, *not* mscoree) **in-proc**. The native-in-proc negative case is the subtle one and
  it passed.
- **The actual 0xC0000409 driver survives through the helper.** Connecting
  `ASCOM.GeminiFPLite.CoverCalibrator` via `AscomDispatchDevice` (→ factory → helper) runs its
  `Connected=true` `Application.DoEvents()` busy-spin — the exact CET shadow-stack tripwire — inside the
  CET-off helper and returns cleanly (`Connected=False`, no panel attached, **no exception, no fastfail**).
  In-proc this would have `RtlFailFast`'d the process before returning. The ~3 s runtime confirms the
  busy-spin actually executed rather than being a no-op, and a helper crash would have surfaced as a
  pipe-break `IOException` (test would fail) rather than the clean null result observed. No helper
  processes leaked.

Still hardware-gated: verifying the panel actually illuminates + meters a flat (needs the physical
Gemini panel), the mount sub-dispatch path (`AxisRates`), and the win-arm64 publish / ship-beside-app.

### Phase 4 design notes

- **`DispatchObject` *is* the local transport.** `IDispatchTransport` was extracted from its exact public
  surface, so `DispatchObject : IDispatchTransport` with no wrapper class. The `[DispatchInterface]`
  generator now emits an `IDispatchTransport _dispatch` field, so every wrapper is transport-agnostic.
- **Routing** happens in `AscomDispatchDevice`'s ctor via `DispatchTransportFactory.Create(progId, sp)`:
  classify → if `mscoree` in-proc and the helper is locatable, spawn `RemoteDispatchTransport`; else
  `new DispatchObject`. If the helper is needed but missing/unstartable, it **falls back to in-proc with a
  warning** (no worse than pre-Phase-4). The factory resolves an `ILoggerFactory` from the DI
  `serviceProvider` (a base primary-ctor param, in scope for the field initializer) to log the decision.
- **Synchronous transport.** The ASCOM COM surface is inherently synchronous/single-apartment, so
  `RemoteDispatchTransport` does a **synchronous, single-flight** pipe round-trip (a blocking COM call
  becomes a blocking pipe call) — it does *not* reuse the async `JsonRpcClient` (that exists for
  PHD2's event-pushing stream). The helper serves sequentially and never pushes, so the reply to request
  N is the next line; a `System.Threading.Lock` serializes the write/read pair.
- **HResult fidelity.** A COM failure on the helper is re-thrown as `JsonRpcException(msg, HResult)`; the
  transport reconstructs `COMException(msg, HResult)`. This keeps the driver's Platform-6 fallback
  (`catch (COMException) when (HResult == DISP_E_UNKNOWNNAME)`) working across the wire.
- **Helper location:** `TIANWEN_ASCOMHOST` env override → beside the running app → sibling build output
  (dev/test).
- **Not yet supported over the wire:** opaque `GetObject`/`InvokeMethodObject` VARIANTs and sub-dispatch
  (`InvokeMethodDispatch`/`GetPropertyDispatch`, i.e. telescope `AxisRates`) throw `NotSupportedException`
  — none of the crash-cluster devices (cover/focuser/FW/switch) use them; the mount is Phase 5.
- **Validated:** `RemoteDispatchTransportTests` drives the typed transport against the real helper +
  `Scripting.Dictionary` (get/set/invoke round-trip + a `COMException`-with-HResult on an illegal call);
  `AscomComServerClassifier` correctly leaves a native in-proc server in-proc. Full E2E against the real
  Gemini panel is Phase 5 (needs the hardware).

## Notes

- **The native driver still dodges all of this.** TianWen's own serial `GeminiFlatPanelDriver`
  (`AddGemini()`) connects clean, headless, cross-platform — no COM, no `DoEvents`, no helper. This plan
  makes the *ASCOM-COM fallback* robust, not the primary Gemini path.
- The interim GUI freeze mitigation (`AppSignalHandler.RunDeviceOpOffRenderThreadAsync`, merged via
  PR #77) keeps the render loop live during the busy-spin but does **not** fix the crash — this host does.
