# ASCOM COM drivers: out-of-process CET-off host (plan)

**Status: Phases 1–3 DONE** (branch `feat/ascom-oop-host`, 2026-07-04). Supersedes the mitigation in
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
JSON-RPC-over-TCP protocol PHD2 already uses** (loopback only).

- **`tianwen-ascomhost`** (`src/TianWen.AscomHost/`): AOT-native, `<CETCompat>false</CETCompat>`,
  Windows-only. Hosts a `DispatchObject` (the AOT-safe raw-vtable IDispatch wrapper) and exposes its
  typed surface over JSON-RPC. ~2.2 MB native, ships no .NET runtime.
- **Detection** (Phase 4): route a device to the helper only when its `InprocServer32` default value
  ends with `mscoree.dll` (a .NET Framework inproc COM server); native/comhost inproc and
  `LocalServer32` drivers stay in-proc, unchanged. Read the registry in the process (x64) bitness view.
- **Cameras stay in-proc** — the `ImageArray` SAFEARRAY is too big to marshal per frame, and no camera
  driver in the crash cluster is one we can't already reach natively.

### Port handshake (ephemeral, helper → parent)

`JsonRpcServer` binds `TcpListener(IPAddress.Loopback, 0)` — the OS assigns a free port. The helper
prints `PORT <n>` as its **first stdout line** once the socket is bound; the parent reads that line,
then connects a `JsonRpcClient` to `127.0.0.1:<n>`. The port flows **helper → parent** (not
parent-picks-and-passes), so there is no TOCTOU race between probing a free port and binding it.

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
| P4 | Parent side: `IDispatchTransport` seam (`LocalDispatchTransport` in-proc / `RemoteDispatchTransport` over `JsonRpcClient`); `mscoree` registry detection at `AscomDevice.NewInstanceFromDevice`; helper process lifecycle (spawn + JobObject to tie lifetime); wire so the 8 `AscomXxxDriver` classes stay unchanged | NOT STARTED |
| P5 | Prove cover-first on the **real Gemini FlatPanel Lite** through the helper (the actual 0xC0000409 driver); generalize to mount/focuser/FW/switch (adds sub-dispatch handles); confirm win-arm64 publish | NOT STARTED |

## Notes

- **The native driver still dodges all of this.** TianWen's own serial `GeminiFlatPanelDriver`
  (`AddGemini()`) connects clean, headless, cross-platform — no COM, no `DoEvents`, no helper. This plan
  makes the *ASCOM-COM fallback* robust, not the primary Gemini path.
- The interim GUI freeze mitigation (`AppSignalHandler.RunDeviceOpOffRenderThreadAsync`, merged via
  PR #77) keeps the render loop live during the busy-spin but does **not** fix the crash — this host does.
