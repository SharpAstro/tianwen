# Soft discovery + discovery performance (plan)

**Status: PARTIAL.** Parallelism + non-serial overlap + boot-aware serial probing **DONE** on
`fix/gemini-flat-panel`. The "soft discovery" (GC-style verify-known-skip-the-rest) model and the
remaining budget tuning are **NOT STARTED** (design captured). Motivated by measuring
`tianwen device discover` at **~30 s** with one device (a Gemini FlatPanel on COM3) attached.

## Where the ~30 s went (measured, device attached)

| Phase | ~time | notes |
|---|---|---|
| process start + JIT/DI | ~2–3 s | |
| weather HTTP reachability (OWM, Open-Meteo) | ~1 s | in the support-check phase |
| **serial probing (COM3)** | **~17 s** | 8 probes × up to 3 baud groups × 2 passes; the dominant cost |
| Canon (WPD/USB/mDNS) | ~2 s | mDNS scan |
| PHD2 (not reachable) | ~2 s | connect timeout |
| OnStep mDNS + Alpaca UDP | ~2 s | Alpaca `DiscoverServersAsync` also errored |

The old (pre-branch) serial phase looked ~1–2 s but was **fast-because-broken**: async reads aborted
instantly (`ERROR_OPERATION_ABORTED`) so probes never actually waited for a reply — replies on COM3
were silently dropped. Making probe reads *correct* (sync) means empty-port probes now wait their full
budget, so it's "~17 s and correct" vs "~1 s and finds nothing on that port."

## DONE on `fix/gemini-flat-panel`

1. **Per-source discovery runs in parallel** (`DeviceDiscovery.DiscoverAsync`/`DiscoverOnlyDeviceType`
   use `Parallel.ForEachAsync` + per-source try/catch), instead of `foreach { await … }` serialising
   each absent source's timeout.
2. **Non-serial sources overlap the serial probe pass.** `IDeviceSource.ConsumesSerialProbe` (default
   false; true on the 6 serial-consuming sources: OnStep/Meade/iOptron/Skywatcher/Gemini/QHYCCD) —
   only those await the serial-probe task; independent sources (Alpaca/Canon/PHD2/weather/ZWO) run
   concurrently with it. `device discover` ~30 s -> ~25 s.
3. **Probe reads are cancellable-synchronous** (`SerialProbeService.TryOpenAsync` sets
   `SynchronousReads`) — fixes the CH34x async-abort that was sinking replies for *all* `#`-terminated
   serial discovery, not just Gemini.
4. **Boot-aware serial probing** for slow controllers: `ISerialProbe.Warmup` (post-open boot delay) +
   `AssertControlLines` (DTR/RTS on the isolated per-probe pass only). Gemini opts into both -> its
   panel is now auto-discovered. DTR-requiring probes are skipped on the shared pass 1 (can't match
   without DTR), so the 2.2 s warmup is paid **once** (pass 2).

## The soft-discovery / GC model (NOT STARTED — the big win)

Treat the profile's known devices like live objects in a GC: **verify they survive cheaply, only do
the expensive full scan for what's new/changed.**

- **Mark (soft pass):** for each device already assigned in the active profile / last result, confirm
  it is still present with the *same identity* — a targeted single-port verify (the existing pinned
  tier) or a lightweight connect-ping. One probe per known device; no full fan-out.
- **Sweep (full pass):** run the expensive general fan-out **only** for what's unaccounted — a new
  serial port that appeared, or a known device that failed verification (identity changed / unplugged).

Make it a **discovery mode**:
- **Soft** (default for periodic refresh / reconnect / session start): verify-known + scan-only-new →
  near-instant when nothing changed (the "if it's there and same id, keep it" the user asked for).
- **Force** (user clicks "Discover"): full fan-out — what `device discover` / Shift+Discover does today.

The seed already exists: `IPinnedSerialPortsProvider` +
`SerialProbeService.VerifyPinnedPortsAsync` verify pinned ports (family-scoped probe) before the
general pass. What's missing is (a) letting a fully-verified profile **skip** the general fan-out
entirely, and (b) extending the same idea to non-serial sources (Alpaca/ASCOM by device id).

## Open work / gaps

1. **`device discover` (CLI) registers no pinned provider** (`0 pinned`) and forces a full scan — it's
   a "find everything new" command. The GUI wires `ActiveProfilePinnedSerialPortsProvider`. Soft mode
   would give the CLI/GUI a fast "refresh what I have" path.
2. **Pinned-verify doesn't work for a DTR-only device (Gemini).** `VerifyPinnedPortsAsync` calls
   `ProbePortAsync` with `isolatePerProbe: false` (shared handle, no DTR), so the new DTR-skip means
   the Gemini probe is skipped in verification and falls through to Stage 2. Fix: verification should
   isolate (assert DTR + warmup) for probes that need control lines — pass `isolatePerProbe: true` (or
   gate on `AssertControlLines`) in `VerifyPinnedPortsAsync`. (Note: **direct URI connect** of a
   pinned Gemini already works — it goes URI -> driver, no discovery.)
3. **OnStep mDNS serialises after the serial pass.** `OnStepDeviceSource` both consumes serial matches
   *and* does a WiFi/mDNS scan in one `DiscoverAsync`, so marking it `ConsumesSerialProbe` makes its
   ~2 s mDNS wait for the serial pass too. Splitting OnStep's serial-vs-network discovery would recover
   that overlap.
4. **Budget tuning (#3, deferred).** Probe `Budget`s were sized assuming instant-abort-on-empty; now
   that reads wait properly, empty-port probes cost their full budget × baud groups × 2 passes. Options:
   shorter default budgets, short-circuit a port once matched, skip baud groups a pinned device doesn't
   use, or only run pass 2 for ports that a pinned device expects. Biggest lever on the ~17 s.
5. **Alpaca UDP discovery errored** (`AlpacaDeviceSource.DiscoverServersAsync`) — separate; worth a
   look (may be broadcast/firewall on this box) but not blocking.
