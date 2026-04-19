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
/// Implementation notes:
/// <list type="bullet">
///   <item>Each port is processed in its own <see cref="Parallel.ForEachAsync"/> iteration.
///         Within a port, baud groups are handled sequentially (close between bauds — the
///         BCL <c>SerialPort</c> baud change while-open is flaky on some USB-serial drivers).</item>
///   <item>Within a baud group, probes run sequentially on a shared open handle. This is
///         the key cost saving vs the legacy "each source opens its own port" model.</item>
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
    IPinnedSerialPortsProvider pinnedPortsProvider) : ISerialProbeService
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

        var enumeratedCount = ports.Count;
        ports = pinnedPortsProvider.FilterUnpinned(ports, logger);

        logger.LogDebug("Enumerated {EnumeratedCount} serial port(s) ({UnpinnedCount} after pinned filter); {ProbeCount} probe(s) registered.",
            enumeratedCount, ports.Count, _probes.Length);

        if (ports.Count == 0)
        {
            return;
        }

        var parallelism = Math.Min(MaxPortParallelism, ports.Count);
        await Parallel.ForEachAsync(
            ports,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = parallelism },
            async (port, ct) => await ProbePortAsync(port, ct));
    }

    private async ValueTask ProbePortAsync(string port, CancellationToken cancellationToken)
    {
        using var portScope = logger.BeginScope(new Dictionary<string, object> { ["Port"] = port });

        // Group by baud rate so each distinct baud opens the port exactly once.
        // Order: most common bauds first (9600) so LX200-style protocols dominate the
        // critical path; rare bauds (28800 iOptron) run after.
        var baudGroups = _probes
            .GroupBy(p => p.BaudRate)
            .OrderBy(g => BaudSortOrder(g.Key))
            .ThenBy(g => g.Key);

        foreach (var baudGroup in baudGroups)
        {
            if (cancellationToken.IsCancellationRequested) return;
            await ProbeBaudGroupAsync(port, baudGroup, cancellationToken);
        }
    }

    private async ValueTask ProbeBaudGroupAsync(string port, IGrouping<int, ISerialProbe> baudGroup, CancellationToken cancellationToken)
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

        ISerialConnection? conn;
        try
        {
            conn = await external.OpenSerialDeviceAsync(port, baud, encoding, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to open port at {Baud} baud — skipping baud group.", baud);
            return;
        }

        try
        {
            foreach (var probe in probesToRun)
            {
                if (cancellationToken.IsCancellationRequested) return;
                await RunSingleProbeAsync(port, probe, conn, cancellationToken);
            }
        }
        finally
        {
            // Close before reopening at the next baud. Reliability on USB-serial bridges
            // is better with close+reopen than with mid-stream BaudRate mutation.
            conn.TryClose();
        }
    }

    private async ValueTask RunSingleProbeAsync(string port, ISerialProbe probe, ISerialConnection conn, CancellationToken cancellationToken)
    {
        using var probeScope = logger.BeginScope(new Dictionary<string, object> { ["Probe"] = probe.Name });

        for (var attempt = 1; attempt <= probe.MaxAttempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(probe.Budget);

            try
            {
                var match = await probe.ProbeAsync(conn, cts.Token);
                if (match is not null)
                {
                    PublishMatch(probe.Name, match);
                    logger.LogInformation("Match → {DeviceUri}", match.DeviceUri);
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
