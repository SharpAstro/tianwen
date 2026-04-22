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
internal sealed class SerialProbeService(
    IExternal external,
    ILogger<SerialProbeService> logger,
    IEnumerable<ISerialProbe> probes,
    IPinnedSerialPortsProvider? pinnedPortsProvider = null) : ISerialProbeService
{
    private readonly ISerialProbe[] _probes = [.. probes];
    private readonly ConcurrentDictionary<string, List<SerialProbeMatch>> _results = new(StringComparer.Ordinal);

    // Bound by number of physical USB-serial bridges a hobbyist typically has; higher
    // parallelism has diminishing returns and risks thread-pool starvation when
    // probes hold the lock for up to Budget each.
    private const int MaxPortParallelism = 4;

    public IReadOnlyList<SerialProbeMatch> ResultsFor(string probeName)
        => _results.TryGetValue(probeName, out var list) ? list.ToArray() : [];

    public async ValueTask ProbeAllAsync(CancellationToken cancellationToken)
    {
        _results.Clear();

        if (_probes.Length == 0)
        {
            logger.LogDebug("No serial probes registered — skipping.");
            return;
        }

        IReadOnlyList<string> ports;
        using (var portLock = await external.WaitForSerialPortEnumerationAsync(cancellationToken))
        {
            ports = external.EnumerateAvailableSerialPorts(portLock);
        }

        var pinned = pinnedPortsProvider?.GetPinnedPorts() ?? [];

        logger.LogDebug("Enumerated {PortCount} serial port(s); {ProbeCount} probe(s) registered; {PinnedCount} pinned.",
            ports.Count, _probes.Length, pinned.Count);

        if (ports.Count == 0)
        {
            return;
        }

        // Stage 1: verify pinned ports still hold their expected device.
        var verifiedPorts = await VerifyPinnedPortsAsync(ports, pinned, cancellationToken);

        // Stage 2: general probing on every port not verified in Stage 1.
        var portsToProbe = verifiedPorts.Count == 0
            ? ports
            : ports.Where(p => !verifiedPorts.Contains(p)).ToArray();

        if (portsToProbe.Count == 0)
        {
            return;
        }

        var parallelism = Math.Min(MaxPortParallelism, portsToProbe.Count);
        await Parallel.ForEachAsync(
            portsToProbe,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = parallelism },
            async (port, ct) => await ProbePortAsync(port, probesToRun: _probes, ct));
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
                using var portScope = logger.BeginScope(new Dictionary<string, object>
                {
                    ["Port"] = entry.Port,
                    ["Stage"] = "Verify",
                });

                var matchingProbes = _probes
                    .Where(p => p.MatchesDeviceHosts.Contains(entry.ExpectedUri.Host, StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                if (matchingProbes.Length == 0)
                {
                    logger.LogDebug("No registered probe can verify {ExpectedUri} — port falls through to general probing.",
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
                            logger.LogInformation("Port {Port} responded but identity changed: expected {Expected}, got {Actual} — will rediscover in Stage 2.",
                                entry.Port, entry.ExpectedUri, match.DeviceUri);
                            return false;
                        }

                        lock (verifyLock) verified.Add(entry.Port);
                        logger.LogInformation("Verified pinned device at {Port}: {Uri}", entry.Port, match.DeviceUri);
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
        Func<SerialProbeMatch, bool>? onMatch = null)
    {
        using var portScope = logger.BeginScope(new Dictionary<string, object> { ["Port"] = port });

        // One user-visible line per port so the operator can tell what discovery is
        // actually doing — the Try* serial reads underneath are noisy on normal
        // probe timeouts (port close aborts the pending read) and have been moved
        // to Debug, so the Info-level story needs to live here instead.
        logger.LogInformation("Probing {Port}: {Probes}", port,
            string.Join(", ", probesToRun.Select(p => $"{p.Name}@{p.BaudRate}")));

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
            await ProbeBaudGroupAsync(port, baudGroup, cancellationToken, onMatch);
        }
    }

    private async ValueTask ProbeBaudGroupAsync(
        string port,
        IGrouping<int, ISerialProbe> baudGroup,
        CancellationToken cancellationToken,
        Func<SerialProbeMatch, bool>? onMatch)
    {
        var baud = baudGroup.Key;
        using var baudScope = logger.BeginScope(new Dictionary<string, object> { ["Baud"] = baud });

        var probesInGroup = baudGroup.ToArray();

        // If any probe in this baud group is ExclusiveBaud, it runs alone. First-registration wins.
        var exclusive = Array.Find(probesInGroup, p => p.Exclusivity == ProbeExclusivity.ExclusiveBaud);
        var probesToRun = exclusive is not null ? [exclusive] : probesInGroup;

        // All probes in one group share the baud; we pick the first probe's encoding.
        // Mixing encodings within a baud group is unusual — if it comes up, split probes.
        var encoding = probesToRun[0].Encoding;

        // Probe isolation: when multiple probes share a baud group we close+reopen
        // between each one, so a rejected command on probe N cannot leave bytes in
        // the device-side buffer that concatenate into probe N+1's exchange. Cost
        // is roughly one extra serial open (~50-200ms on USB bridges) per shared
        // probe slot — worth it for correctness. Single-probe groups skip this and
        // keep the original open-once semantics.
        var isolateEachProbe = probesToRun.Length > 1;

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
                    if (conn is null) continue;
                }

                try
                {
                    await RunSingleProbeAsync(port, probe, conn!, cancellationToken, onMatch);
                }
                finally
                {
                    if (isolateEachProbe)
                    {
                        conn!.TryClose();
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
            conn?.TryClose();
        }

        async ValueTask<ISerialConnection?> TryOpenAsync()
        {
            try
            {
                var c = await external.OpenSerialDeviceAsync(port, baud, encoding, cancellationToken);
                // Log the exact handshake at Info during probes; drivers opening the
                // same port for session use do not touch this flag.
                c.LogVerbose = true;
                return c;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to open port at {Baud} baud — skipping.", baud);
                return null;
            }
        }
    }

    private async ValueTask RunSingleProbeAsync(
        string port,
        ISerialProbe probe,
        ISerialConnection conn,
        CancellationToken cancellationToken,
        Func<SerialProbeMatch, bool>? onMatch)
    {
        using var probeScope = logger.BeginScope(new Dictionary<string, object> { ["Probe"] = probe.Name });

        for (var attempt = 1; attempt <= probe.MaxAttempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(probe.Budget);

            try
            {
                var match = await probe.ProbeAsync(port, conn, cts.Token);
                if (match is not null)
                {
                    // onMatch is the verification gate: returns true to publish, false to
                    // discard (identity mismatch). In the general pass, onMatch is null,
                    // so all matches are published unconditionally.
                    var publish = onMatch?.Invoke(match) ?? true;
                    if (publish)
                    {
                        PublishMatch(probe.Name, match);
                        logger.LogInformation("Match → {DeviceUri}", match.DeviceUri);
                    }
                    return;
                }

                // Null = no match at the protocol level; no point retrying.
                return;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Timeout after {Budget}ms (attempt {Attempt}/{Max}).",
                    probe.Budget.TotalMilliseconds, attempt, probe.MaxAttempts);
                // Fall through to next attempt, if any.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Probe threw — treating as no-match.");
                return;
            }
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
