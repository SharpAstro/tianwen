using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Discovery;

/// <summary>
/// Two-stage serial-port discovery:
/// <list type="number">
///   <item><b>Verify</b> — for each <see cref="PinnedSerialPort"/> pair from
///         <see cref="IPinnedSerialPortsProvider"/>, open just that port and run only the
///         probes whose <see cref="ISerialProbe.MatchesDeviceHosts"/> contain the pinned
///         URI's host. If a match's identity (scheme + host + path) lines up with the
///         pinned URI, the port is confirmed still-in-place — published and excluded
///         from Stage 2. If verification fails (no matching probe, timeout, or identity
///         mismatch), the port stays in the Stage 2 pool so a cable-swap can be
///         auto-recovered by the general probe pass.</item>
///   <item><b>General probe</b> — every registered probe runs against every
///         non-verified port, grouped by baud (port opened once per baud) and
///         parallelised across ports.</item>
/// </list>
/// Implementation notes:
/// <list type="bullet">
///   <item>Within a baud group, probes run sequentially on a shared open handle — the
///         key cost saving vs the legacy "each source opens its own port" model.</item>
///   <item>Per-attempt timeout is implemented via a linked <see cref="CancellationTokenSource"/>;
///         <see cref="OperationCanceledException"/> is caught only when the *inner* token
///         fired (probe timed out) — outer cancellation always rethrows.</item>
///   <item>Results are published atomically per probe name; <see cref="ResultsFor(string)"/>
///         returns a snapshot.</item>
/// </list>
/// </summary>
internal sealed class SerialProbeService : ISerialProbeService
{
    // Default ladder of per-pass budget multipliers applied to each ISerialProbe.Budget.
    // Rationale: "everyone gets a chance first" — pass 1 runs every probe on every
    // port at the declared budget, then only the ports that produced no match get a
    // pass 2 at the extended budget. Cold ESP32 devices (OnStep WiFi controllers,
    // ~1-2s boot) are caught without making every warm probe wait the long timeout.
    //
    // Dead ports pay the extra pass cost, but dead serial ports are cheap — a port
    // that never produced a match in pass 1 is very likely still empty in pass 2,
    // and we bail as fast as each probe's timeout lets us.
    internal static readonly double[] DefaultPassBudgetMultipliers = [1.0, 2.0];

    private readonly ITimeProvider _timeProvider;
    private readonly IExternal _external;
    private readonly ILogger<SerialProbeService> _logger;
    private readonly IPinnedSerialPortsProvider? _pinnedPortsProvider;
    private readonly ISerialProbe[] _probes;
    private readonly double[] _probePassMultipliers;
    private readonly ConcurrentDictionary<string, List<SerialProbeMatch>> _results = new(StringComparer.Ordinal);

    // Bound by number of physical USB-serial bridges a hobbyist typically has; higher
    // parallelism has diminishing returns and risks thread-pool starvation when
    // probes hold the lock for up to Budget each.
    private const int MaxPortParallelism = 4;

    public SerialProbeService(
        ITimeProvider timeProvider,
        IExternal external,
        ILogger<SerialProbeService> logger,
        IEnumerable<ISerialProbe> probes,
        IPinnedSerialPortsProvider? pinnedPortsProvider = null,
        IReadOnlyList<double>? passBudgetMultipliers = null)
    {
        _timeProvider = timeProvider;
        _external = external;
        _logger = logger;
        _pinnedPortsProvider = pinnedPortsProvider;
        _probes = [.. probes];
        _probePassMultipliers = passBudgetMultipliers is { Count: > 0 }
            ? [.. passBudgetMultipliers]
            : DefaultPassBudgetMultipliers;
    }

    public IReadOnlyList<SerialProbeMatch> ResultsFor(string probeName)
        => _results.TryGetValue(probeName, out var list) ? list.ToArray() : [];

    public async ValueTask ProbeAllAsync(CancellationToken cancellationToken)
    {
        _results.Clear();

        if (_probes.Length == 0)
        {
            _logger.LogDebug("No serial probes registered — skipping.");
            return;
        }

        IReadOnlyList<string> ports;
        using (var portLock = await _external.WaitForSerialPortEnumerationAsync(cancellationToken))
        {
            ports = _external.EnumerateAvailableSerialPorts(portLock);
        }

        var pinned = _pinnedPortsProvider?.GetPinnedPorts() ?? [];

        _logger.LogDebug("Enumerated {PortCount} serial port(s); {ProbeCount} probe(s) registered; {PinnedCount} pinned.",
            ports.Count, _probes.Length, pinned.Count);

        if (ports.Count == 0)
        {
            return;
        }

        // Stage 1: verify pinned ports still hold their expected device.
        var verifiedPorts = await VerifyPinnedPortsAsync(ports, pinned, cancellationToken);

        // Stage 2: general probing on every port not verified in Stage 1, with a
        // ladder pass — each pass runs all probes on all still-unmatched ports at
        // a fraction (or multiple) of each probe's declared Budget. Cold devices
        // that miss the first pass get a retry at a longer budget without any
        // warm probe paying the cost.
        var initialPorts = verifiedPorts.Count == 0
            ? ports
            : ports.Where(p => !verifiedPorts.Contains(p)).ToArray();

        if (initialPorts.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> remainingPorts = [.. initialPorts];
        for (var passIdx = 0; passIdx < _probePassMultipliers.Length; passIdx++)
        {
            if (remainingPorts.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var mult = _probePassMultipliers[passIdx];
            var passNum = passIdx + 1;
            if (passNum == 1)
            {
                _logger.LogInformation("Probe pass 1: {Count} port(s), {Probes} probe(s) at declared budget.",
                    remainingPorts.Count, _probes.Length);
            }
            else
            {
                _logger.LogInformation("Probe pass {Pass}: retrying {Count} unmatched port(s) at {Mult}x budget.",
                    passNum, remainingPorts.Count, mult);
            }

            var parallelism = Math.Min(MaxPortParallelism, remainingPorts.Count);
            var passMult = mult;
            // Pass 1 uses the fast shared-connection path (one open per baud
            // group, probes run in sequence on the same handle) — optimal when
            // the device responds cleanly. Pass 2 escalates to close+reopen per
            // probe so stale bytes left by a rejected pass-1 command on probe N
            // can't concatenate into probe N+1's exchange on pass 2.
            var isolatePerProbe = passIdx > 0;
            await Parallel.ForEachAsync(
                remainingPorts,
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = parallelism },
                async (port, ct) => await ProbePortAsync(port, _probes, ct,
                    budgetMultiplier: passMult, isolatePerProbe: isolatePerProbe));

            // Drop ports that produced any match from subsequent passes.
            var matched = GetPortsWithAnyMatch();
            remainingPorts = [.. remainingPorts.Where(p => !matched.Contains(p))];
        }
    }

    /// <summary>
    /// Snapshot of ports that have at least one match published across all probe
    /// names. Used to decide which ports still need another pass.
    /// </summary>
    private HashSet<string> GetPortsWithAnyMatch()
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in _results.Values)
        {
            lock (list)
            {
                foreach (var m in list)
                {
                    matched.Add(m.Port);
                }
            }
        }
        return matched;
    }

    /// <summary>
    /// Stage 1 of the algorithm: for every pinned <c>(port, expected URI)</c> pair, run
    /// the subset of registered probes that can verify that URI's device family. Ports
    /// whose handshake comes back with the expected identity are excluded from the
    /// general probe pass (and their match is published). Everything else — including
    /// ports whose family has no registered probe yet — stays in the general pool.
    /// </summary>
    private async ValueTask<HashSet<string>> VerifyPinnedPortsAsync(
        IReadOnlyList<string> enumeratedPorts,
        IReadOnlyList<PinnedSerialPort> pinned,
        CancellationToken cancellationToken)
    {
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pinned.Count == 0 || _probes.Length == 0) return verified;

        // Only verify pinned ports that are actually present in this enumeration —
        // a stale pin (cable unplugged, port number shifted) is nothing to verify.
        var enumeratedSet = new HashSet<string>(enumeratedPorts, StringComparer.OrdinalIgnoreCase);
        var candidates = pinned.Where(p => enumeratedSet.Contains(p.Port)).ToArray();
        if (candidates.Length == 0) return verified;

        var verifyLock = new object();
        var parallelism = Math.Min(MaxPortParallelism, candidates.Length);

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = parallelism },
            async (entry, ct) =>
            {
                using var portScope = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["Port"] = entry.Port,
                    ["Stage"] = "Verify",
                });

                var matchingProbes = _probes
                    .Where(p => p.MatchesDeviceHosts.Contains(entry.ExpectedUri.Host, StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                if (matchingProbes.Length == 0)
                {
                    _logger.LogDebug("No registered probe can verify {ExpectedUri} — port falls through to general probing.",
                        entry.ExpectedUri);
                    return;
                }

                // Run family-scoped probes on the pinned port, grouped by baud.
                // If any publishes a match whose identity lines up with the pinned URI,
                // the port is verified and skipped from Stage 2.
                await ProbePortAsync(
                    entry.Port,
                    probesToRun: matchingProbes,
                    ct,
                    onMatch: match =>
                    {
                        if (!IdentityMatches(match.DeviceUri, entry.ExpectedUri))
                        {
                            _logger.LogInformation("Port {Port} responded but identity changed: expected {Expected}, got {Actual} — will rediscover in Stage 2.",
                                entry.Port, entry.ExpectedUri, match.DeviceUri);
                            return false;
                        }

                        lock (verifyLock) verified.Add(entry.Port);
                        _logger.LogInformation("Verified pinned device at {Port}: {Uri}", entry.Port, match.DeviceUri);
                        return true;
                    });
            });

        return verified;
    }

    /// <summary>
    /// Two device URIs represent the same device when their scheme (device type), host
    /// (device source) and path (stable device id) align. Query (port, filter offsets,
    /// user-edited site) is deliberately ignored — cable movement and user edits drift
    /// the query, but the identity stays put.
    /// </summary>
    private static bool IdentityMatches(Uri a, Uri b)
        => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.AbsolutePath, b.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    private async ValueTask ProbePortAsync(
        string port,
        IReadOnlyList<ISerialProbe> probesToRun,
        CancellationToken cancellationToken,
        Func<SerialProbeMatch, bool>? onMatch = null,
        double budgetMultiplier = 1.0,
        bool isolatePerProbe = false)
    {
        using var portScope = _logger.BeginScope(new Dictionary<string, object> { ["Port"] = port });

        // One user-visible line per port so the operator can tell what discovery is
        // actually doing — the Try* serial reads underneath are noisy on normal
        // probe timeouts (port close aborts the pending read) and have been moved
        // to Debug, so the Info-level story needs to live here instead.
        // Tag each probe in the log with its framing so the ordering is self-explanatory.
        _logger.LogInformation("Probing {Port}: {Probes}", port,
            string.Join(", ", probesToRun.Select(p => $"{p.Name}@{p.BaudRate}({p.Framing})")));

        // Group by baud rate so each distinct baud opens the port exactly once.
        // Order: most common bauds first (9600) so LX200-style protocols dominate the
        // critical path; rare bauds (28800 iOptron) run after.
        var baudGroups = probesToRun
            .GroupBy(p => p.BaudRate)
            .OrderBy(g => BaudSortOrder(g.Key))
            .ThenBy(g => g.Key);

        foreach (var baudGroup in baudGroups)
        {
            if (cancellationToken.IsCancellationRequested) return;
            await ProbeBaudGroupAsync(port, baudGroup, onMatch, budgetMultiplier, isolatePerProbe, cancellationToken);
        }
    }

    private async ValueTask ProbeBaudGroupAsync(
        string port,
        IGrouping<int, ISerialProbe> baudGroup,
        Func<SerialProbeMatch, bool>? onMatch,
        double budgetMultiplier = 1.0,
        bool isolatePerProbe = false,
        CancellationToken cancellationToken = default)
    {
        var baud = baudGroup.Key;
        using var baudScope = _logger.BeginScope(new Dictionary<string, object> { ["Baud"] = baud });

        // Secondary sort within a baud group: framed protocols first (exit cleanly on
        // their terminator), then brace-framed (QFOC JSON), then unframed last (QHYCFW3
        // "VRS" uses fixed-length reads). Rationale: a fixed-length read that over-reads
        // or times out can leave stale bytes in the device-side buffer that contaminate
        // the next probe's response on the shared handle. Keeping unframed probes at
        // the tail means only the last probe in the group has to tolerate stale bytes,
        // and every probe before it can trust its terminator. Stable sort so insertion
        // order is preserved between probes with equal framing (OrderBy is stable).
        var probesInGroup = baudGroup.OrderBy(p => p.Framing).ToArray();

        // If any probe in this baud group is ExclusiveBaud, it runs alone. First-registration wins.
        var exclusive = Array.Find(probesInGroup, p => p.Exclusivity == ProbeExclusivity.ExclusiveBaud);
        var probesToRun = exclusive is not null ? [exclusive] : probesInGroup;

        // All probes in one group share the baud; we pick the first probe's encoding.
        // Mixing encodings within a baud group is unusual — if it comes up, split probes.
        var encoding = probesToRun[0].Encoding;

        // Probe isolation: close+reopen between probes so a rejected command on
        // probe N cannot leave bytes in the device-side buffer that concatenate
        // into probe N+1's exchange. Only engaged on pass 2 (isolatePerProbe) and
        // only when >1 probe shares the baud group — pass 1 keeps the fast
        // shared-handle path for clean responders, and pass 2 escalates to
        // isolation for the ports that didn't match pass 1 anyway.
        var isolateEachProbe = isolatePerProbe && probesToRun.Length > 1;

        ISerialConnection? conn = null;
        if (!isolateEachProbe)
        {
            conn = await TryOpenAsync();
            if (conn is null) return;
        }

        try
        {
            foreach (var probe in probesToRun)
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (isolateEachProbe)
                {
                    conn = await TryOpenAsync();
                }

                if (conn is null) continue;

                try
                {
                    await RunSingleProbeAsync(port, probe, conn, onMatch, budgetMultiplier, cancellationToken);
                }
                finally
                {
                    if (isolateEachProbe)
                    {
                        TryCloseWithLog(conn);
                        conn = null;
                    }
                }
            }
        }
        finally
        {
            // Single-probe groups only: close the shared connection on exit. Reopen
            // at the next baud because USB-serial bridges react badly to mid-stream
            // BaudRate mutation.
            if (conn is not null)
            {
                TryCloseWithLog(conn);
            }
        }

        async ValueTask<ISerialConnection?> TryOpenAsync()
        {
            try
            {
                var c = await _external.OpenSerialDeviceAsync(port, baud, encoding, cancellationToken);
                // Log the exact handshake at Info during probes; drivers opening the
                // same port for session use do not touch this flag.
                c.LogVerbose = true;
                // Open/close framing at Info so every exchange in the log is visibly
                // bracketed by the baud rate it ran at — otherwise the baud only
                // shows up inside a _logger scope, which most formatters drop.
                _logger.LogInformation("{Port} opened @ {Baud} baud", port, baud);
                return c;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to open port at {Baud} baud — skipping.", baud);
                return null;
            }
        }

        void TryCloseWithLog(ISerialConnection c)
        {
            c.TryClose();
            _logger.LogInformation("{Port} closed (was @ {Baud} baud)", port, baud);
        }
    }

    private async ValueTask RunSingleProbeAsync(
        string port,
        ISerialProbe probe,
        ISerialConnection conn,
        Func<SerialProbeMatch, bool>? onMatch,
        double budgetMultiplier = 1.0,
        CancellationToken cancellationToken = default)
    {

        var budget = TimeSpan.FromMilliseconds(probe.Budget.TotalMilliseconds * budgetMultiplier);

        using var probeScope = _logger.BeginScope(new Dictionary<string, object> { ["Probe"] = probe.Name, ["Budget"] = budget });

        // Tag the shared connection so the External _logger's "COM5 --> ..." / "<-- ..."
        // lines are attributed to this probe. Connection.VerboseTag is read on each
        // write/read; clearing it on exit leaves the handle in an untagged state for
        // the next probe (which sets its own tag on entry).
        var previousTag = conn.VerboseTag;
        conn.VerboseTag = probe.Name;

        // Drop stale bytes the previous probe may have left in the receive buffer.
        // Without this, e.g. leftover bytes from an unframed QHYCFW3 read can end
        // with a '#' that the next LX200 probe's TryReadTerminated matches instantly,
        // parsing garbage as a response and exiting in <1 ms instead of waiting for
        // the real reply. Matches the DiscardInBuffer() pattern in a from-scratch
        // PowerShell handshake: every exchange starts clean.
        conn.DiscardInBuffer();

        try
        {
            for (var attempt = 1; attempt <= probe.MaxAttempts; attempt++)
            {
                using var cts = new CancellationTokenSource(budget, _timeProvider.System);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
 
                try
                {
                    var match = await probe.ProbeAsync(port, conn, linkedCts.Token);
                    if (match is not null)
                    {
                        // onMatch is the verification gate: returns true to publish, false to
                        // discard (identity mismatch). In the general pass, onMatch is null,
                        // so all matches are published unconditionally.
                        var publish = onMatch?.Invoke(match) ?? true;
                        if (publish)
                        {
                            PublishMatch(probe.Name, match);
                            _logger.LogInformation("Match -> {DeviceUri}", match.DeviceUri);
                        }
                        return;
                    }

                    // Null = no match at the protocol level; no point retrying.
                    return;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Timeout after {Budget}ms (attempt {Attempt}/{Max}).",
                        budget.TotalMilliseconds, attempt, probe.MaxAttempts);
                    // Fall through to next attempt, if any.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Probe threw - treating as no-match.");
                    return;
                }
            }
        }
        finally
        {
            conn.VerboseTag = previousTag;
        }
    }

    private void PublishMatch(string probeName, SerialProbeMatch match)
    {
        _results.AddOrUpdate(
            probeName,
            _ => [match],
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(match);
                }
                return existing;
            });
    }

    /// <summary>
    /// Lower values probe first. 9600 is the LX200 workhorse and hosts the most probes
    /// (OnStep / Meade / QHYCFW3 / QFOC), so amortising the open cost across that
    /// group is the biggest win. Higher bauds run after.
    /// </summary>
    private static int BaudSortOrder(int baud) => baud switch
    {
        9600 => 0,
        115200 => 1,
        28800 => 2,
        _ => 3,
    };
}
