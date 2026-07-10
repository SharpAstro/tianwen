using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>The local comet cache envelope: the fetch timestamp gates the TTL, the payload is the mapped set.</summary>
internal sealed record CometCacheFile(DateTimeOffset FetchedUtc, CometElements[] Comets);

/// <summary>
/// Default <see cref="ICometRepository"/>: SBDB elements cached to <c>AppData/SmallBodies/comets.json</c>
/// with the weather-driver freshness idiom (poll-on-read, TTL-gated, stale-offline fallback, atomic
/// write-back) -- but keyed on a stored <c>FetchedUtc</c> in the cache envelope rather than the file
/// mtime, so the TTL is driven by the injected <see cref="ITimeProvider"/> (fake-clock testable, and
/// robust against file-copy/sync tools that reset mtimes). The in-memory map is swapped atomically so
/// the render/planner threads always read a torn-free snapshot.
/// </summary>
internal sealed class CometRepository : ICometRepository
{
    // Comet orbit solutions change on the timescale of new observation arcs; a weekly refresh is ample
    // and keeps the keyless bulk fetch rare.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private readonly ISbdbCometSource _source;
    private readonly IExternal _external;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger<CometRepository> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private volatile ImmutableDictionary<CatalogIndex, CometElements> _byIndex = ImmutableDictionary<CatalogIndex, CometElements>.Empty;
    private ImmutableArray<CometElements> _all = [];
    private bool _loadedOnce;

    public CometRepository(ISbdbCometSource source, IExternal external, ITimeProvider timeProvider, ILogger<CometRepository> logger)
    {
        _source = source;
        _external = external;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public ImmutableArray<CometElements> All => _all;

    public bool TryGet(CatalogIndex index, out CometElements elements) => _byIndex.TryGetValue(index, out elements);

    public bool TryGetPosition(CatalogIndex index, DateTimeOffset time, out double raJ2000Hours, out double decJ2000Deg, out double magnitude)
    {
        if (_byIndex.TryGetValue(index, out var elements))
        {
            return CometEphemeris.TryGetEquatorialJ2000WithMagnitude(elements, time, out raJ2000Hours, out decJ2000Deg, out magnitude);
        }

        raJ2000Hours = decJ2000Deg = magnitude = double.NaN;
        return false;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _loadedOnce))
        {
            return;
        }

        await RefreshAsync(forceRefetch: false, cancellationToken);
    }

    public async Task RefreshAsync(bool forceRefetch = false, CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var cacheFile = Path.Combine(_external.CreateSubDirectoryInAppDataFolder("SmallBodies").FullName, "comets.json");
            var cached = await _external.TryReadJsonAsync(cacheFile, SbdbJsonContext.Default.CometCacheFile, _logger, cancellationToken);

            var fresh = cached is not null && _timeProvider.GetUtcNow() - cached.FetchedUtc <= CacheTtl;
            if (cached is not null && fresh && !forceRefetch)
            {
                Publish(cached.Comets);
                _logger.LogDebug("Loaded {Count} comets from fresh cache", cached.Comets.Length);
                return;
            }

            IReadOnlyList<CometElements> fetched;
            try
            {
                fetched = await _source.FetchAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "SBDB comet fetch failed");
                if (cached is not null)
                {
                    _logger.LogInformation("Using stale comet cache (offline fallback): {Count} comets", cached.Comets.Length);
                    Publish(cached.Comets);
                }
                return;
            }

            var comets = new CometElements[fetched.Count];
            for (var i = 0; i < comets.Length; i++)
            {
                comets[i] = fetched[i];
            }

            Publish(comets);

            await _logger.CatchAsync(
                ct => _external.AtomicWriteJsonAsync(cacheFile, new CometCacheFile(_timeProvider.GetUtcNow(), comets), SbdbJsonContext.Default.CometCacheFile, ct),
                cancellationToken);

            _logger.LogInformation("Refreshed comet cache: {Count} comets from SBDB", comets.Length);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void Publish(IReadOnlyList<CometElements> comets)
    {
        var builder = ImmutableDictionary.CreateBuilder<CatalogIndex, CometElements>();
        foreach (var comet in comets)
        {
            // A designation can appear more than once across apparitions in SBDB; the last wins.
            if (comet.CatalogIndex is { } index)
            {
                builder[index] = comet;
            }
        }

        _byIndex = builder.ToImmutable();
        _all = [.. comets];
        Volatile.Write(ref _loadedOnce, true);
    }
}
