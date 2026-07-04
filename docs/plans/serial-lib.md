# Serial.Lib — a serial-I/O sibling repo that does one job well (plan)

**Status: NOT STARTED (design captured).** Motivated by the Gemini FlatPanel Lite hardware bring-up
(branch `fix/gemini-flat-panel`), which proved that .NET's `System.IO.Ports.SerialPort` is not
trustworthy for our use. Serial is load-bearing for an astro app (mounts, focusers, filter wheels,
cover/calibrators, flat panels — OnStep, LX200/Meade, Skywatcher, iOptron, QHYCFW/QFOC, Gemini), so
"it mostly works" is not good enough. Extract serial I/O into its own SharpAstro sibling repo (like
`Lzip.Lib` / `SER.Lib` / `DIR.Lib`), do it properly once, and have TianWen depend on it.

## Motivation — what the hardware bring-up exposed

`SerialPort.BaseStream` **async** reads are unreliable:

- On a CH34x USB bridge (the Gemini FlatPanel's CH341, extremely common on cheap astro gear) the
  **first async read succeeds, then every subsequent read aborts** with `IOException: "The I/O
  operation has been aborted because of either a thread exit or an application request."`
  (`ERROR_OPERATION_ABORTED`). The reply still arrives — the read just gets torn down — so responses
  land one frame late and every query desyncs. Confirmed on real hardware with a verbose wire trace.
- Per **dotnet/runtime#28968** (and the Sparx Engineering "if you *must* use SerialPort" writeup), the
  BCL `SerialStream` "async" is itself just a **blocking read on a background thread** — there is no
  real overlapped async win to lose. Its `BaseStream.ReadAsync` also **ignores `ReadTimeout`**, so a
  `Task.WhenAny(readTask, Task.Delay)` timeout leaves the read hanging forever.
- Mixing a synchronous `DiscardInBuffer` (which does `BaseStream.Read`) with an async read corrupts the
  overlapped state and makes the next async read abort — another dotnet/runtime-reported footgun.

Sources: [dotnet/runtime#28968](https://github.com/dotnet/runtime/issues/28968),
[dotnet/runtime#35545](https://github.com/dotnet/runtime/issues/35545),
[Sparx: "If you must use .NET System.IO.Ports.SerialPort"](https://sparxeng.com/blog/software/must-use-net-system-io-ports-serialport).

### The interim fix already in TianWen (the stopgap this repo would supersede)

`fix/gemini-flat-panel` added `ISerialConnection.SynchronousReads` (opt-in) + a cancellable synchronous
read path in `TianWen.Lib/Connections/SerialConnection.cs`: `Task.Run` over a blocking `ReadByte` with
a short `ReadTimeout` slice, checking the cancellation token between slices (so it is cancellable
without abandoning a blocked thread — the exact pattern the runtime maintainers recommend). The Gemini
driver and the discovery probe service opt in. This is a **wrapper over `SerialPort`**, so it inherits
the rest of `SerialPort`'s baggage (exclusive-open semantics, control-line quirks, no true async). The
sibling repo is the chance to own the layer end to end.

## Goal / conventions (proposed — mirror the lzip repo)

- **Repo/package:** `Serial.Lib`, **`RootNamespace = SharpAstro.Serial`** (same split as `SER.Lib` ->
  `SharpAstro.Ser`, `Lzip.Lib` -> `SharpAstro.Lzip`). Sibling at `../Serial.Lib`. `net10.0`.
- Standard SharpAstro CI (`dotnet.yml`, `VERSION_PREFIX: 1.0.${{ github.run_number }}`, publish to
  NuGet, centralized `Directory.Packages.props`). Model on `SER.Lib`.
- **Scope discipline (the "does one job well" contract):** cross-platform, **cancellable**, timeout-honouring
  serial byte I/O + control lines + port enumeration. NOT a device-protocol library (framing/probes stay
  in TianWen). The API is the seam TianWen's `ISerialConnection` already defines.

## Public API (shape it on TianWen's existing `ISerialConnection`)

```
public interface ISerialPort : IAsyncDisposable
{
    bool IsOpen { get; }
    // cancellable, honour a real read timeout, never spuriously abort:
    ValueTask<int>  ReadTerminatedAsync(Memory<byte> buffer, ReadOnlyMemory<byte> terminators, CancellationToken ct);
    ValueTask<bool> ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct);
    ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    void DiscardInBuffer();
    // control lines (needed by e.g. the Gemini CH341, which holds the MCU in reset until DTR asserted):
    bool Dtr { get; set; }
    bool Rts { get; set; }
}
public static class SerialPortFactory
{
    static IReadOnlyList<string> Enumerate();                       // "serial:COM3", "/dev/ttyUSB0"
    static ValueTask<ISerialPort> OpenAsync(string port, SerialSettings settings, CancellationToken ct);
}
public sealed record SerialSettings(int Baud, int DataBits = 8, Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One, bool AssertControlLinesOnOpen = false, TimeSpan? ReadTimeout = null);
```

Behaviour contract (the whole point):
- Reads are cancellable and observe `ReadTimeout` — cancelling a read never leaves a hung task and
  never corrupts the next read.
- No spurious `ERROR_OPERATION_ABORTED`; reads stay frame-aligned across many exchanges.
- `AssertControlLinesOnOpen` sets DTR+RTS before open (CH34x reset release).

## Implementation phasing

| Phase | Scope | Ships |
|---|---|---|
| **P1 — managed wrapper** | Lift TianWen's cancellable `SynchronousReads` path into `Serial.Lib` over `SerialPort` (blocking `Read` on `Task.Run` + `ReadTimeout` slices + token). Immediately reliable, low risk, cross-platform (works wherever `SerialPort` opens). Port enumeration + control lines. | `Serial.Lib` 1.0 |
| **P2 — native backend (the real "do it well")** | Bypass `SerialStream` entirely. Windows: P/Invoke `CreateFile`/`ReadFile`/`WriteFile` with **correctly-driven overlapped I/O** (an IOCP-bound handle, not thread-affine) + `SetCommTimeouts`/`SetCommState`/`EscapeCommFunction` (DTR/RTS). Linux/macOS: `open`/`read`/`write` + `termios` + `poll` for cancellable timed reads. Pick the backend at open. This is where genuine non-blocking async + rock-solid cancellation come from. | `Serial.Lib` 2.0 |
| **P3 — re-point TianWen** | Add `Serial.Lib` to `Directory.Packages.props` + the `UseLocalSiblings` set; reimplement `TianWen.Lib/Connections/SerialConnection(.Base)` on top of `SharpAstro.Serial` (or delete them and adapt `ISerialConnection` callers). Delete the interim `SynchronousReads` wrapper. | TianWen consuming it |

Respect the release dance (per CLAUDE.md): publish `Serial.Lib` to NuGet first, then bump the tianwen
pin — never push TianWen referencing an unpublished version, never use local nupkg feeds.

## Why a separate repo (not just harden it in TianWen)

Same rationale as `Lzip.Lib`: a focused, testable, reusable unit; the reliability tests (long
read/write soak, cancellation-under-load, timeout honouring, CH34x abort regression) live with the
code; other SharpAstro consumers can use it; and it forces the clean API boundary. TianWen's interim
`SynchronousReads` fix keeps hardware working *today*; this repo is the "properly, once" follow-through.

## Open questions / decisions to make

1. **P1-only vs go straight to native (P2).** P1 (managed wrapper) is what we already have working; it
   fixes the abort but keeps `SerialPort`'s exclusive-open + no-true-async. P2 (native P/Invoke) is the
   real prize but real work per-OS. Recommend ship P1 to get the codec out of TianWen, then P2.
2. **Keep `ISerialConnection` in TianWen or move it to the lib.** Moving it makes the lib the single
   source of the serial abstraction; keeping it lets TianWen adapt. Lean: define `ISerialPort` in the
   lib, adapt TianWen's `ISerialConnection` to it (thin).
3. **Enumeration + hot-plug.** `SerialPort.GetPortNames` is fine for P1; a native P2 could add
   arrival/removal notifications (WM_DEVICECHANGE / udev) so discovery reacts to plug events.
