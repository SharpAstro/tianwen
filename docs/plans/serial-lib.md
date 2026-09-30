# Serial.Lib: a serial-I/O sibling repo that does one job well (plan)

**Status: P1 and P3 DONE (2026-09-27); P2 open as #1012.** `SharpAstro/Serial.Lib` exists (1.0.11 on nuget.org;
1.1 adds reads with no deadline and a loopback pair, 1.2 the Bluetooth device behind a port), and TianWen's `SerialConnection` is an adapter over it
("What shipped", below). #407 closes with P3. The 2026-09 bench findings it must cover are in
"What the 2026-09 bench sessions added" (#780, #781, #783, #784, #809, #810). Motivated by the Gemini FlatPanel Lite hardware bring-up
(branch `fix/gemini-flat-panel`), which proved that .NET's `System.IO.Ports.SerialPort` is not
trustworthy for our use. Serial is load-bearing for an astro app (mounts, focusers, filter wheels,
cover/calibrators, flat panels; OnStep, LX200/Meade, Skywatcher, iOptron, QHYCFW/QFOC, Gemini), so
"it mostly works" is not good enough. Extract serial I/O into its own SharpAstro sibling repo (like
`Lzip.Lib` / `SER.Lib` / `DIR.Lib`), do it properly once, and have TianWen depend on it.

## Motivation: what the hardware bring-up exposed

`SerialPort.BaseStream` **async** reads are unreliable:

- On a CH34x USB bridge (the Gemini FlatPanel's CH341, extremely common on cheap astro gear) the
  **first async read succeeds, then every subsequent read aborts** with `IOException: "The I/O
  operation has been aborted because of either a thread exit or an application request."`
  (`ERROR_OPERATION_ABORTED`). The reply still arrives, the read just gets torn down, so responses
  land one frame late and every query desyncs. Confirmed on real hardware with a verbose wire trace.
- Per **dotnet/runtime#28968** (and the Sparx Engineering "if you *must* use SerialPort" writeup), the
  BCL `SerialStream` "async" is itself just a **blocking read on a background thread**; there is no
  real overlapped async win to lose. Its `BaseStream.ReadAsync` also **ignores `ReadTimeout`**, so a
  `Task.WhenAny(readTask, Task.Delay)` timeout leaves the read hanging forever.
- Mixing a synchronous `DiscardInBuffer` (which does `BaseStream.Read`) with an async read corrupts the
  overlapped state and makes the next async read abort; another dotnet/runtime-reported footgun.

Sources: [dotnet/runtime#28968](https://github.com/dotnet/runtime/issues/28968),
[dotnet/runtime#35545](https://github.com/dotnet/runtime/issues/35545),
[Sparx: "If you must use .NET System.IO.Ports.SerialPort"](https://sparxeng.com/blog/software/must-use-net-system-io-ports-serialport).

### The interim fix already in TianWen (the stopgap this repo would supersede)

`fix/gemini-flat-panel` added `ISerialConnection.SynchronousReads` (opt-in) + a cancellable synchronous
read path in `TianWen.Lib/Connections/SerialConnection.cs`: `Task.Run` over a blocking `ReadByte` with
a short `ReadTimeout` slice, checking the cancellation token between slices (so it is cancellable
without abandoning a blocked thread; the exact pattern the runtime maintainers recommend). The Gemini
driver and the discovery probe service opt in. This is a **wrapper over `SerialPort`**, so it inherits
the rest of `SerialPort`'s baggage (exclusive-open semantics, control-line quirks, no true async). The
sibling repo is the chance to own the layer end to end.

## Goal / conventions (proposed, mirror the lzip repo)

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
- Reads are cancellable and observe `ReadTimeout`: cancelling a read never leaves a hung task and
  never corrupts the next read.
- No spurious `ERROR_OPERATION_ABORTED`; reads stay frame-aligned across many exchanges.
- `AssertControlLinesOnOpen` sets DTR+RTS before open (CH34x reset release).

## What the 2026-09 bench sessions added (the lib's contract must cover these)

The Gemini Focuser Pro bench sessions of 2026-09-25 (#654 and its follow-ups) found five more serial facts,
beyond the async-read unreliability that started this plan. Each is either a **transport** fact, which
belongs in `Serial.Lib`, or a **device** fact, which stays in the driver and needs the lib to expose
something:

| Finding | Issue | Transport or device | What `Serial.Lib` owes |
|---|---|---|---|
| A connected read that gets no reply, times out, or gets an unparseable reply returned a neutral value ("not moving", `int.MinValue`) instead of failing. The Gemini focuser's reads are fixed in PR #811 ("a Gemini focuser read with no reply throws instead of reading \"not moving\""); the rule for every native serial driver is #810 | #810, #781 (fixed) | Both | A timed-out read is an `IOException` (a transient one, for the resilience layer), never a default or an empty string. Drivers then only choose how to PARSE. |
| Every port open resets a CH340 board, with or without DTR, and the myFocuserPro2 firmware saves a changed position to EEPROM 30 s late. A reopen within 30 s of a move reports the OLD position, and nothing errors. | #780 | Device (the reset is the board's), with transport consequences | Expose whether an open toggled the control lines, and make the open itself observable (a timestamp), so the driver can apply its save window. Never reopen silently inside the lib (no hidden retry-by-reopen). |
| A USB serial device's port name follows the USB **socket** on Windows (COM3 stays COM3 for the socket), so swapping two units swaps their identities. | #783 | Transport | Enumerate ports WITH their stable hardware identity: USB instance path, VID/PID and iSerial where the chip has one (a CH340 has none). Then identity can key on the device where possible, and on the socket knowingly where not. |
| On Linux the names are enumeration order (`/dev/ttyUSBn`), which is not even stable per socket. | #784 | Transport | Enumerate `/dev/serial/by-path` and `/dev/serial/by-id` alongside the `tty` names, and prefer them as identity. |
| What reads do while the cable is out mid-move, and whether the driver comes back, is still a bench question. | #809 (bench) | Transport | Surface device REMOVAL as its own exception type, distinct from a timeout, so a driver can mark the position uncertain rather than retry into a vanished port. |

Also owed by the lib, and already known before the bench sessions:
- the abandoned-write guard: a Bluetooth SPP port accepts an open and never completes a write
  (`ISerialConnection.HasAbandonedIo`; see CLAUDE.md's device-management notes);
- per-command round-trip timing (#409).

## Implementation phasing

| Phase | Scope | Ships |
|---|---|---|
| **P1: managed wrapper** (DONE) | Lift TianWen's cancellable `SynchronousReads` path into `Serial.Lib` over `SerialPort` (blocking `Read` on `Task.Run` + `ReadTimeout` slices + token). Immediately reliable, low risk, cross-platform (works wherever `SerialPort` opens). Port enumeration + control lines. | `Serial.Lib` 1.0 |
| **P2: native backend (the real "do it well"), Windows-first** (#1012) | Bypass `SerialStream` entirely on **Windows**: P/Invoke `CreateFile`/`ReadFile`/`WriteFile` with **correctly-driven overlapped I/O** (an IOCP-bound handle, not thread-affine) + `SetCommTimeouts`/`SetCommState`/`EscapeCommFunction` (DTR/RTS). **Linux/macOS may not need a native backend at all**; the `ERROR_OPERATION_ABORTED` abort is Windows-specific (it *is* a Win32 overlapped-I/O error code, and .NET's `SerialStream` is a wholly separate termios/`poll` implementation on Unix; a dotnet/runtime ticket reports the Linux impl behaves acceptably). So the plan is: keep the P1 managed wrapper over stock `SerialPort` on Unix and only write the native Win32 backend, `#if`-selected at open. A native `termios`/`poll` Unix backend stays a *possible* later refinement (true non-blocking async, hot-plug), not a correctness requirement. **macOS unverified**; treat like Linux until a real macOS serial device is tested. | `Serial.Lib` 2.0 |
| **P3: re-point TianWen** (DONE) | Add `Serial.Lib` to `Directory.Packages.props` + the `UseLocalSiblings` set; reimplement `TianWen.Lib/Connections/SerialConnection(.Base)` on top of `SharpAstro.Serial` (or delete them and adapt `ISerialConnection` callers). Delete the interim `SynchronousReads` wrapper. | TianWen consuming it |

Respect the release dance (per CLAUDE.md): publish `Serial.Lib` to NuGet first, then bump the tianwen
pin, never push TianWen referencing an unpublished version, never use local nupkg feeds.

## What shipped (2026-09-27)

- **The library** (`../Serial.Lib`, `SharpAstro.Serial`): `SerialPorts.OpenAsync` / `Enumerate`, `ISerialPort`,
  `SerialSettings`, `SerialPortInfo`, and one `SerialException : IOException` family (timeout carrying the bytes
  that did arrive, framing, removed, busy, not found, abandoned I/O). Every guarantee lives once in
  `SerialPortCore`, over an ASYNCHRONOUS backend seam: the core awaits the backend, deadlines are a timer-driven
  token linked with the caller's, and only the `System.IO.Ports` backend blocks a thread, because its only
  reliable read and write are the blocking ones (it answers buffered bytes at once and blocks only to wait, in
  token-checked slices). The seam started as blocking calls the core wrapped in `Task.Run`, which spread
  blocking to the loopback for no reason; the user's rule is not to block where it can be helped, and P2's
  overlapped I/O plugs into the same seam. Bench-tested on a Gemini Focuser Pro (CH340) on COM3: 400 exchanges
  frame-aligned, a cancelled and a timed-out read each followed by a clean exchange, the port described as
  `1a86:7523` on its USB socket.
- **1.1:** `Timeout.InfiniteTimeSpan` as a read deadline (only the token ends the read), because
  `ISerialConnection`'s reads have always been bounded by the caller's token alone (a QHY wheel's reply blocks
  until the wheel arrives); and `SerialLoopback.CreatePair`, two real ports wired to each other in memory, which
  TianWen's adapter tests run over.
- **1.2:** `SerialPortInfo.Bluetooth`, what a Windows Bluetooth serial port leads to: the paired device's address, name
  and Class of Device as Windows recorded them, or Windows' own incoming port. TianWen's discovery skips a port whose far
  end can never be an instrument (`SerialProbeExclusion`): a pair of headphones ("S42", Audio/Video) took every write,
  answered none, and cost 31 s of each discovery; with 1.2 that discovery took 8 s, node start included (2026-09-30).
- **TianWen** (P3): `Serial.Lib` joins `UseLocalSiblings`; `SerialConnection` adapts `ISerialPort` to
  `ISerialConnection` (typed failures to the `Try*` null / -1 / false, the verbose probe log unchanged); the old
  `SerialConnectionBase` and the interim `SynchronousReads` opt-in are gone; bytes past a terminator are now kept
  for the next read (the old `ReadAtLeastAsync` dropped them). `ISerialConnection` closes asynchronously
  (`TryCloseAsync`, `IAsyncDisposable`), every driver awaits it, and a synchronous `Dispose` starts the close
  with `CloseInBackground` rather than blocking on it.
- **Answered:** open question 1 (P1 first, then the Windows-only P2) and 2 (`ISerialPort` lives in the library,
  TianWen adapts). Still open: 3 (hot-plug, with #1012), and the bench table's #780, #783, #784, #809 and #810,
  which the library now makes possible (identity keys, `OpenedAt`, a removal exception, typed failures) but
  TianWen does not use yet.

## Why a separate repo (not just harden it in TianWen)

Same rationale as `Lzip.Lib`: a focused, testable, reusable unit; the reliability tests (long
read/write soak, cancellation-under-load, timeout honouring, CH34x abort regression) live with the
code; other SharpAstro consumers can use it; and it forces the clean API boundary. TianWen's interim
`SynchronousReads` fix keeps hardware working *today*; this repo is the "properly, once" follow-through.

## Open questions / decisions to make

1. **P1-only vs go straight to native (P2).** P1 (managed wrapper) is what we already have working; it
   fixes the abort but keeps `SerialPort`'s exclusive-open + no-true-async. P2 (native P/Invoke) is the
   real prize but real work, though, per the P2 note, **only on Windows**: the abort is Windows-specific,
   so Unix can stay on the P1 wrapper and P2 is a one-platform job, not three. Recommend ship P1 to get
   the codec out of TianWen, then the Windows-only P2.
2. **Keep `ISerialConnection` in TianWen or move it to the lib.** Moving it makes the lib the single
   source of the serial abstraction; keeping it lets TianWen adapt. Lean: define `ISerialPort` in the
   lib, adapt TianWen's `ISerialConnection` to it (thin).
3. **Enumeration + hot-plug.** `SerialPort.GetPortNames` is fine for P1; a native P2 could add
   arrival/removal notifications (WM_DEVICECHANGE / udev) so discovery reacts to plug events.
