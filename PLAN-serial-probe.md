# Plan: Centralized Serial-Port Discovery

Centralize all serial-port probing during `DeviceDiscovery.DiscoverAsync` so that
each port is opened **once per baud rate**, all interested probes run against
that one open handle, and every log line is scoped with `port` + `baud` +
`probe` so timeouts can be attributed to a specific probe instead of "some
serial source somewhere."

## Why now

| Symptom | Cause |
|---|---|
| Discovery on a box with one COM port and ASCOM-less profile takes ~10–15s | Skywatcher (300ms × 2 bauds) + OnStep (250ms multi-cmd) + Meade (250ms) + iOptron (500ms) + QHYCFW3 (500ms) + QFOC (1500ms) all open/probe/close the *same* port serially. |
| OnStep cold-start (Teesek mount) sometimes shows up, sometimes doesn't | OnStep controller wake-up can exceed the 250ms probe budget; one retry per source isn't worth the 5×-multiplier cost. Need per-port wake-up amortized once. |
| Logs say "timeout on COM5" but not who timed out | All sources log via `ILogger<TSourceType>` without per-port/per-probe scope (`SerialConnectionBase` logs at trace level only in DEBUG; production logs are anonymous). |
| Just bumping timeouts globally would make discovery 5× slower | Yes — and OnStep already takes the lion's share. Need to share the wait, not multiply it. |

## Current state (inventory in survey notes — keep for reference)

7 distinct probes across 5 sources, all hitting `IExternal.OpenSerialDeviceAsync`
on their own:

| Source | Bauds | Timeout | Protocol family | Notes |
|---|---|---|---|---|
| `SkywatcherDeviceSource` | 115200, 9600 | 300ms | Skywatcher binary `:e1\r` | Tries both bauds per port |
| `MeadeDeviceSource` | 9600 | 250ms | LX200 `:GVP#…` | Multi-command sequence |
| `OnStepDeviceSource` | 9600 | 250ms | LX200 `:GVP#` + `^On[-]?Step` regex | First-connect can be slow |
| `IOptronDeviceSource` | 28800 | 500ms | LX200-ish `:MRSVE#` | Different baud isolates it |
| `QHYDeviceSource` (CFW3 phase) | 9600 | 500ms | `VRS` (3 ASCII bytes) | Probe in `QHYSerialControlledFilterWheelDriver.ProbeAsync` |
| `QHYDeviceSource` (QFOC phase) | 9600 | 1500ms | JSON `{"cmd_id":1}` | Probe in `QHYFocuserDriver.ProbeAsync` |

Existing partial centralization: `IExternal.WaitForSerialPortEnumerationAsync()`
serializes *enumeration* (single semaphore) and `External._serialConnections`
caches open `ISerialConnection` by address — but probing remains per-source.

`DeviceDiscovery.DiscoverAsync` runs sources sequentially with no per-port
coordination.

## Design

### New: `ISerialProbeService`

```
src/TianWen.Lib/Devices/Discovery/ISerialProbeService.cs
src/TianWen.Lib/Devices/Discovery/SerialProbeService.cs
src/TianWen.Lib/Devices/Discovery/ISerialProbe.cs
src/TianWen.Lib/Devices/Discovery/SerialProbeResult.cs
```

```csharp
public interface ISerialProbe
{
    string Name { get; }                       // "Skywatcher", "OnStep", "QFOC", ...
    int BaudRate { get; }                      // 9600, 115200, 28800, ...
    Encoding Encoding { get; }                 // ASCII / Latin1 / UTF8
    ProbeExclusivity Exclusivity { get; }      // Shared (default) | ExclusiveBaud
    TimeSpan Budget { get; }                   // per-attempt deadline
    int MaxAttempts { get; }                   // 1 default; OnStep = 2 (cold-start retry)

    // Run on an already-open connection. Return a SerialProbeMatch on success
    // (carries any device URIs to publish), null on no-match. Exceptions are
    // caught, logged with full scope, and treated as no-match.
    ValueTask<SerialProbeMatch?> ProbeAsync(ISerialConnection conn, CancellationToken ct);
}

public interface ISerialProbeService
{
    void Register(ISerialProbe probe);                  // called once per probe at DI build time
    ValueTask ProbeAllAsync(CancellationToken ct);      // called by DeviceDiscovery before per-source DiscoverAsync
    IEnumerable<SerialProbeMatch> ResultsFor(string probeName);  // sources read from this
}
```

### Per-port scheduling algorithm

```
ProbeAllAsync:
  ports = await IExternal.WaitForSerialPortEnumerationAsync + EnumerateAvailableSerialPorts
  ports = ports - profile-pinned ports (see "Profile-pinned skip" below)

  Parallel.ForEachAsync(ports, MaxDegreeOfParallelism = min(4, ports.Count), async port =>
      using scope = logger.BeginScope({ port })
      groupedByBaud = probes.GroupBy(p => p.BaudRate).OrderBy(g => commonBauds.IndexOf(g.Key))
      for each baudGroup:
          conn = await OpenSerialDeviceAsync(port, baud, encoding=ascii)  // closed at end of group
          using scope2 = logger.BeginScope({ baud })

          // Exclusive probes run alone within a baud group
          if any probe in group is ExclusiveBaud:
              run that one only, skip the rest
          else:
              for each probe in group (insertion order):
                  using scope3 = logger.BeginScope({ probe = probe.Name })
                  for attempt 1..MaxAttempts:
                      using cts = CancellationTokenSource.CreateLinked(ct, after Budget)
                      lock = await conn.WaitAsync(cts)
                      try:
                          // optional: per-probe pre-flight (drain stale data)
                          // probe.PreFlightAsync(conn, cts)
                          match = await probe.ProbeAsync(conn, cts)
                          if match != null: stash & break
                      catch OperationCanceledException when cts.IsCancellationRequested:
                          log "timeout after {Budget} (attempt {n}/{MaxAttempts})"
                          continue
                      catch ex:
                          log warn ex
                          break  // one fault = no retry on the same probe
          await conn.DisposeAsync()  // explicit close before reopening at next baud

  populate _resultsByProbeName
```

Key properties:
- **Each port opened at most once per baud rate** (was: once per source per baud)
- **Within a baud group, probes share the open handle** (was: each source open/close)
- **Different ports run in parallel** (was: fully sequential)
- **Logger scopes nest** so a single line reads
  `[port=COM5 baud=9600 probe=OnStep] timeout after 1500ms (attempt 2/2)`

### Probe registration

Each device source moves its probe lambda into a tiny class:

```csharp
internal sealed class SkywatcherSerialProbe(ILogger logger) : ISerialProbe
{
    public string Name => "Skywatcher";
    public int BaudRate => 115200;            // also register one for 9600
    public Encoding Encoding => Encoding.ASCII;
    public ProbeExclusivity Exclusivity => ProbeExclusivity.Shared;
    public TimeSpan Budget => TimeSpan.FromMilliseconds(300);
    public int MaxAttempts => 1;

    public async ValueTask<SerialProbeMatch?> ProbeAsync(ISerialConnection conn, CancellationToken ct) {
        // existing logic from SkywatcherDeviceSource probe loop, minus open/close
    }
}
```

Sources register probes via DI extension:
```csharp
services.AddSerialProbe<SkywatcherSerialProbe>();
services.AddSerialProbe<SkywatcherSerialProbe115200>();
services.AddSerialProbe<OnStepSerialProbe>();
// ...
```

`SerialProbeService` collects all `ISerialProbe` from DI in its constructor.

### Source rewrites

Each source's `DiscoverAsync` becomes:
```csharp
public async ValueTask DiscoverAsync(CancellationToken ct)
{
    foreach (var match in _probeService.ResultsFor("Skywatcher"))
        // build SkywatcherDevice from match.DeviceUri / match.Metadata
}
```

No port enumeration, no `OpenSerialDeviceAsync`, no per-port loop. The
WiFi/UDP/mDNS paths in `SkywatcherDeviceSource` and `OnStepDeviceSource` are
**unchanged** — those are not the bottleneck and use different transports.

### Profile-pinned skip

Profiles already store device URIs with `?port=COM5` query params. Before
probing, walk the active profile's URIs:
```csharp
var pinnedPorts = profile.AllDeviceUris()
    .Select(u => DeviceUri.TryGetSerialPort(u))
    .OfType<string>()
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
ports = ports.Where(p => !pinnedPorts.Contains(p)).ToList();
```

Skip with reason: `[port=COM3] skipped: pinned to active profile (Mount://OnStepDevice/...)`.
This avoids fighting an already-open driver and shaves the most expensive
probes off the common case (re-discovery during a session).

Add a small helper to `DeviceUri` (or extension) that reads `DeviceQueryKey.Port`
once instead of inlining in every device class.

### OnStep cold-start

OnStep ESP32 controllers can take ~1–2s to respond on first connect after a
cold boot. Currently each source budgets 250ms and gives up. With centralization:

- `OnStepSerialProbe.Budget = TimeSpan.FromMilliseconds(1500)`
- `OnStepSerialProbe.MaxAttempts = 2`
- The 1.5s wait is paid **once per port**, not once per source per port
- Net cost on a no-OnStep box: 1.5s × ports-with-9600-but-no-OnStep (was: 250ms × 5 sources × ports = roughly the same, but now it actually *finds* OnStep when present)

Optional follow-up (not in scope here): add a 1-byte wake-up nudge with a
50ms settle before the real probe.

### Exclusivity

`Skywatcher` binary protocol is the only one that's nominally a hazard if
interleaved with text probes — but since it sends `:e1\r` and reads back its
own `=…\r` reply, and the LX200 probes start with `:G…#` and read until `#`,
they don't collide on the wire as long as we run them sequentially within a
baud group (which we do). Default `Shared` is fine for everyone today.

`ExclusiveBaud` is reserved for future binary protocols that send unsolicited
chatter or have stateful handshakes.

### Cancellation & error handling

- Per-probe `CancellationTokenSource.CreateLinkedTokenSource(outerCt, Budget)`
- `OperationCanceledException` checked against the inner `cts.IsCancellationRequested`
  to distinguish probe-timeout (log + continue) from outer cancel (rethrow)
- `ResourceLock` from `ISerialConnection.WaitAsync` is acquired per-probe
  (not per-baud-group) so a hung probe can't lock the port forever — the
  budget cancel releases the lock

### Testing

New file: `src/TianWen.Lib.Tests/SerialProbeServiceTests.cs`

- Fake `IExternal` that returns a configurable list of ports and an in-memory
  `ISerialConnection` per (port, baud) pair
- Each `ISerialConnection` fake is scripted: "if write contains `:GVP#` reply
  with `OnStep V5#`"; "if baud is 115200 reply nothing within 300ms"
- Tests:
  - **One port, two probes, one match** — port opened once per baud, both
    probes run, only matching one returns a result
  - **Two ports parallel** — `MaxDegreeOfParallelism` honored, both probed
  - **Profile-pinned port skipped** — port not opened at all
  - **Probe timeout doesn't poison the next probe** — `:GVP#` times out, next
    probe still runs against the same port
  - **Exclusive probe skips siblings in its baud group**
  - **OnStep retry budget** — first attempt times out, second succeeds
  - **Logger scopes present** — capture log lines and assert each carries
    `port`, `baud`, `probe` keys (use `Meziantou.Extensions.Logging.Xunit.v3`
    capture)

For the live probe classes, keep the existing source-specific tests (e.g.
`SkywatcherProtocolTests`) — they test the wire-format helpers, not the
discovery loop.

## Phases

### Phase 1 — Plumbing, no behavior change

- [ ] Add `ISerialProbe`, `SerialProbeMatch`, `ProbeExclusivity` types
- [ ] Add `ISerialProbeService` + `SerialProbeService` impl with the per-port
      algorithm above
- [ ] DI extension `services.AddSerialProbeService()` and
      `services.AddSerialProbe<T>()`
- [ ] Wire `SerialProbeService.ProbeAllAsync(ct)` into
      `DeviceDiscovery.DiscoverAsync` *before* the per-source loop
- [ ] Logger scopes (`port`, `baud`, `probe`)
- [ ] **No source migrated yet** — service has zero registered probes, runs
      ProbeAllAsync as a no-op. This phase ships and is observable in logs as
      `[SerialProbeService] enumerated 1 ports, 0 probes registered`.

### Phase 2 — Logger scopes everywhere (independent quick win)

- [ ] In each existing source's per-port probe loop, add
      `using var scope = logger.BeginScope(new { port, baud, source = "OnStep" })`
      around the open/probe/close block. This *immediately* fixes the "logs
      don't say who" complaint without waiting for the full migration.
- [ ] Ship as a separate commit so the logging fix is independently
      reviewable.

### Phase 3 — Two-tier pinned-port discovery (verify-then-fall-through)

Naively filtering pinned ports is unsafe: if two pinned devices swap cables
(e.g., OnStep@COM5 ↔ EAF@COM6), a blanket filter would leave *both*
undiscoverable. The correct design runs two stages:

- **Stage 1 (verify)**: for each pinned `(port, expected URI)` pair, open
  only that port and run only the probes whose `MatchesDeviceHosts` claims
  the expected URI's host. If the match's identity (scheme+host+path) lines
  up with the pinned URI, the port is verified — match is published and the
  port is excluded from Stage 2. If verification fails (no matching probe,
  timeout, or identity mismatch), the port stays in the Stage 2 pool.
- **Stage 2 (general)**: every registered probe runs against every
  non-verified port, grouped by baud and parallelised across ports (the
  original algorithm).

Done:
- [x] `PinnedSerialPort` record carrying `(Port, ExpectedUri)`
- [x] `IPinnedSerialPortsProvider.GetPinnedPorts()` returns the pairs
- [x] `ISerialProbe.MatchesDeviceHosts` opt-in for verification
- [x] Two-stage algorithm in `SerialProbeService.ProbeAllAsync`
- [x] `ActiveProfilePinnedSerialPortsProvider` walks every URI slot in
      `ProfileData` and produces pairs; sentinel ports (wifi/wpd/fake) are
      filtered at normalisation time
- [x] GUI composition root replaces the null default with the active-profile
      provider
- [x] Cable-swap test: COM5↔COM6 swap both auto-recovered via Stage 2

### Phase 4 — Migrate one source at a time

Order by impact (highest pain → lowest):

- [ ] Skywatcher (two bauds × every port = biggest single saving)
- [ ] OnStep (slow first-connect benefits most from per-port amortized wait)
- [ ] Meade (LX200 shares a baud with OnStep — runs in same baud group, free win)
- [ ] iOptron (different baud, isolated)
- [ ] QHY CFW3 + QFOC (move both probes from `QHYDeviceSource` Phase 2 into
      separate `ISerialProbe` classes; the SDK-camera Phase 1 + camera-cable
      CFW Phase 3 remain on `QHYDeviceSource` because they're not serial)

Each migration is one commit:
1. Add new probe class
2. Register in DI
3. Strip per-port loop from `*DeviceSource.DiscoverAsync`
4. Source's tests still pass (probe behavior unchanged from caller's POV)

### Phase 5 — Cleanup

- [ ] Delete `IExternal.WaitForSerialPortEnumerationAsync` callers from
      individual sources (only `SerialProbeService` needs it now)
- [ ] Remove the per-source baud constants once probes own them
- [ ] Audit `External._serialConnections` cache — does it still need to live
      across discovery cycles? Probably not, since probes always close their
      handle.

## Risks & open questions

- **`SerialPort` baud-rate change while open is unreliable** on some Windows
  drivers (FTDI vs CH340 vs CDC-ACM). Plan assumes close-then-reopen between
  baud groups. Worth a one-shot test on the user's two boxes (FTDI + CH340)
  before committing to the per-baud reopen pattern.
- **Some USB-serial drivers fail enumeration if probed too fast after
  Windows mounts them.** The existing `WaitForSerialPortEnumerationAsync`
  semaphore handles this only for *enumeration*, not *open*. If we see
  flakes, add a 100ms settle after open() before the first write.
- **Parallelism across ports may starve `Parallel.ForEachAsync` thread pool
  if the user has many ports** (>8). Cap at `Min(4, ports.Count)` —
  configurable via `IConfiguration` if needed.
- **Profile may have stale port pins** (user moved cable, COM5 → COM6).
  Phase 3's skip would then mis-skip the new port. Acceptable: user runs
  discovery again after the obvious move; the existing
  `DeviceDiscoveryExtensions.ReconcileUri` handles the recovery once any
  source re-finds the device. We could add "if pinned port doesn't exist in
  enumeration, fall through and probe everything" as a safety net — cheap.
- **OnStep ESP32 cold-start retry budget (2 × 1.5s)**: still possible to
  miss it on a port that has *another* slow device that wins the lock first.
  Reasonable to accept until evidence says otherwise.

## Out of scope

- TCP / mDNS / WiFi discovery (already different transports, not the bottleneck)
- Skywatcher UDP broadcast (network discovery, separate path)
- ASCOM / Alpaca (no serial probing)
- Per-source CheckSupportAsync gating (already runs in parallel via
  `DeviceDiscovery.CheckSupportAsync`)

## Done when

- A box with one COM port and no profile-pin runs full discovery in under 3s
  (down from ~10s)
- Every probe-related log line in production carries `port`, `baud`, `probe`
- A connected session can re-run discovery without touching its in-use port
- OnStep cold-start is detected on second probe attempt without inflating
  no-OnStep discovery cost
