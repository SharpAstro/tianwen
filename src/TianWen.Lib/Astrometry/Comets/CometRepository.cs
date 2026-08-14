using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>The local comet cache envelope: the fetch timestamp gates the TTL, the payload is the mapped set.</summary>
internal sealed record CometCacheFile(DateTimeOffset FetchedUtc, CometElements[] Comets);

/// <summary>One comet's current-apparition element set, with its own fetch stamp so entries expire
/// individually (they are fetched individually, on demand, and a shared stamp would make one fetch
/// look like it refreshed the rest).</summary>
internal sealed record ApparitionEntry(DateTimeOffset FetchedUtc, CometElements Elements);

/// <summary>The Horizons per-object overlay cache: current-apparition elements for the comets someone
/// has actually looked at.</summary>
internal sealed record ApparitionCacheFile(ApparitionEntry[] Entries)
{
    /// <summary>
    /// When true, the repository must not attempt a per-object Horizons fetch: whatever
    /// <see cref="Entries"/> carries is the whole overlay this host will ever have. Set only by the
    /// publish-time bake, for a host that cannot reach JPL at all (a browser -- Horizons sends no
    /// CORS headers), never by the local write-back.
    ///
    /// <para><b>It is a policy, NOT a completeness claim, and the difference is load-bearing.</b> If it
    /// meant "every comet that needed upgrading is in here" it would have to be tied to the bulk
    /// snapshot it was baked against (a hash guard, as <c>SimbadMergeSnapshot</c> carries), and a comet
    /// missing from it would be a correctness bug. Meaning only "do not ask", a comet that was never
    /// baked simply keeps its bulk record and stays flagged approximate -- which is what the host did
    /// anyway, minus a request that could only ever fail. That is what lets the bake cover the comets
    /// worth covering instead of having to be exhaustive.</para>
    /// </summary>
    public bool NoRemoteRefresh { get; init; }
}

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

    // A current-apparition set is osculating AT the instant it was requested, so it decays with time
    // rather than with new observations. A week keeps a comet sub-arcminute over any realistic session
    // while matching the bulk cadence, so the two caches expire on the same rhythm.
    private static readonly TimeSpan ApparitionTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a comet whose Horizons fetch FAILED is left alone before it is tried again.
    ///
    /// <para>Without this, a permanently-unreachable endpoint is retried at whatever rate the request
    /// settles: the single-flight key clears in the <c>finally</c> and only SUCCESS writes an entry, so
    /// nothing remembers that the last attempt failed. Measured on the browser build, where Horizons
    /// can never answer (no CORS headers): <b>45 attempts and 50 s of cumulative request time in one
    /// four-minute session, all for the same comet</b>. An offline desktop and a JPL outage take the
    /// identical path.</para>
    ///
    /// <para>An hour is far below the <see cref="ApparitionTtl"/> this feeds and the upgrade is a
    /// background nicety nobody is waiting on -- the marker is already drawn from the bulk record and
    /// labelled approximate -- so delaying a recovery from a genuinely transient failure by up to an
    /// hour costs nothing observable.</para>
    /// </summary>
    private static readonly TimeSpan ApparitionRetryCooldown = TimeSpan.FromHours(1);

    private readonly ISbdbCometSource _source;
    private readonly IHorizonsCometSource _horizons;
    private readonly IApparitionSeedSource _seed;
    private readonly IExternal _external;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger<CometRepository> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private volatile ImmutableDictionary<CatalogIndex, CometElements> _byIndex = ImmutableDictionary<CatalogIndex, CometElements>.Empty;
    private ImmutableArray<CometElements> _all = [];
    private bool _loadedOnce;

    // The Horizons overlay: current-apparition elements that take precedence over the bulk record.
    // Swapped by reference, so a reader on the render thread always sees a torn-free map.
    private volatile ImmutableDictionary<CatalogIndex, ApparitionEntry> _apparitions = ImmutableDictionary<CatalogIndex, ApparitionEntry>.Empty;

    // Single-flight per comet. A key present means a fetch is in the air; it is removed when that fetch
    // settles, so a failure is retried on a later request rather than being latched forever -- bounded
    // by ApparitionRetryCooldown below, which is what stops "later" meaning "immediately, forever".
    private readonly ConcurrentDictionary<CatalogIndex, byte> _apparitionInFlight = new();

    // When each comet's last Horizons attempt failed, so the retry can be spaced out. Written from the
    // fetch continuation (a thread-pool thread) and read from the render/poll path, hence concurrent.
    private readonly ConcurrentDictionary<CatalogIndex, DateTimeOffset> _apparitionFailedAtUtc = new();

    // Set from a seed that declares itself sealed; latched for the process, never cleared.
    private volatile bool _noRemoteRefresh;
    private bool _apparitionsLoaded;
    private readonly SemaphoreSlim _apparitionLoadGate = new(1, 1);

    public CometRepository(ISbdbCometSource source, IHorizonsCometSource horizons, IApparitionSeedSource seed, IExternal external, ITimeProvider timeProvider, ILogger<CometRepository> logger)
    {
        _source = source;
        _horizons = horizons;
        _seed = seed;
        _external = external;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public ImmutableArray<CometElements> All => _all;

    /// <summary>
    /// The current-apparition set when one has been fetched, else the bulk SBDB record. Every consumer
    /// goes through here (including <see cref="TryGetPosition"/>), so upgrading a comet's elements
    /// improves the sky map, the planner, the search and MCP at once with no per-caller wiring.
    /// </summary>
    public bool TryGet(CatalogIndex index, out CometElements elements)
    {
        if (_apparitions.TryGetValue(index, out var apparition))
        {
            elements = apparition.Elements;
            return true;
        }
        return _byIndex.TryGetValue(index, out elements);
    }

    public bool TryGetPosition(CatalogIndex index, DateTimeOffset time, out double raJ2000Hours, out double decJ2000Deg, out double magnitude)
    {
        // Resolving Earth first and then delegating keeps ONE reduction here: the two overloads differ
        // only in who pays for the per-instant state.
        if (CometEphemeris.TryGetEarthState(time, out var earth))
        {
            return TryGetPosition(index, earth, out raJ2000Hours, out decJ2000Deg, out magnitude);
        }

        raJ2000Hours = decJ2000Deg = magnitude = double.NaN;
        return false;
    }

    public bool TryGetPosition(CatalogIndex index, in CometEphemeris.EarthState earth, out double raJ2000Hours, out double decJ2000Deg, out double magnitude)
    {
        // TryGet, not _byIndex: that is where the current-apparition upgrade is applied, and it is the
        // whole reason this overload lives on the repository instead of at the sweeping call site.
        if (TryGet(index, out var elements))
        {
            return CometEphemeris.TryGetEquatorialJ2000WithMagnitude(elements, earth, out raJ2000Hours, out decJ2000Deg, out magnitude);
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
        // Before the bulk set, so the overlay (and the seal it may carry) is in place by the time any
        // consumer resolves a position. Doing it lazily inside the fetch task -- where it used to be its
        // only caller -- meant the first drawn marker fired a request BEFORE learning it must not.
        // Its own gate, so this is safe to await while holding _loadGate below.
        await LoadApparitionCacheAsync(cancellationToken);

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

    /// <inheritdoc />
    public void RequestCurrentApparition(CatalogIndex index)
    {
        if (!_byIndex.TryGetValue(index, out var baseElements))
        {
            return; // not a loaded comet
        }

        var now = _timeProvider.GetUtcNow();

        // Already upgraded and still inside its TTL.
        if (_apparitions.TryGetValue(index, out var existing) && now - existing.FetchedUtc <= ApparitionTtl)
        {
            return;
        }

        // A sealed seed says this host cannot reach Horizons at all, so there is nothing to ask.
        if (_noRemoteRefresh)
        {
            return;
        }

        // Nothing to gain: the bulk record is already stated within this apparition, which is the
        // common case (a freshly discovered comet, or one whose solution epoch is recent). Only a set
        // that is a revolution or more old produces the phase error worth a network round-trip for.
        now.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        if (!baseElements.IsElementSetStale(tt1 + tt2))
        {
            return;
        }

        // The last attempt failed and is still cooling off (see ApparitionRetryCooldown). This is what
        // an UNSEALED host that cannot reach Horizons falls back on -- a dev server serving the app
        // without the CI-baked seed, an offline desktop, a JPL outage.
        if (_apparitionFailedAtUtc.TryGetValue(index, out var failedAt) && now - failedAt < ApparitionRetryCooldown)
        {
            return;
        }

        if (!_apparitionInFlight.TryAdd(index, 0))
        {
            return; // already in the air
        }

        _ = Task.Run(async () =>
        {
            // Anything that did NOT settle the question counts as a failure for backoff purposes,
            // including the null return (Horizons answered with no usable element record, which repeats
            // deterministically for that comet). Defaulting to false and setting it only on the paths
            // that genuinely resolved means a new early return backs off rather than silently spinning.
            var resolved = false;
            try
            {
                await LoadApparitionCacheAsync(CancellationToken.None);

                // The seed may have arrived with the load above and sealed this host.
                if (_noRemoteRefresh)
                {
                    resolved = true; // not a failure: there is nothing to back off from
                    return;
                }

                if (_apparitions.TryGetValue(index, out var fromDisk) && _timeProvider.GetUtcNow() - fromDisk.FetchedUtc <= ApparitionTtl)
                {
                    resolved = true; // the disk cache already had it
                    return;
                }

                var refined = await _horizons.TryFetchCurrentApparitionAsync(baseElements, _timeProvider.GetUtcNow(), CancellationToken.None);
                if (refined is not { } elements)
                {
                    return;
                }

                var entry = new ApparitionEntry(_timeProvider.GetUtcNow(), elements);
                _apparitions = _apparitions.SetItem(index, entry);
                _apparitionFailedAtUtc.TryRemove(index, out _);
                resolved = true;
                _logger.LogInformation(
                    "Upgraded {Comet} to current-apparition elements (epoch {Epoch:F1}); its bulk record was {Revolutions:F1} revolutions old",
                    elements.DisplayName, elements.EpochJdTt, baseElements.RevolutionsSinceEpoch(tt1 + tt2));

                await PersistApparitionsAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Silent and harmless by design: the bulk elements stay in use and IsElementSetStale
                // keeps reporting true, so the UI keeps flagging the position as approximate.
                _logger.LogDebug(ex, "Horizons current-apparition fetch failed for {Index}", index);
            }
            finally
            {
                if (!resolved)
                {
                    _apparitionFailedAtUtc[index] = _timeProvider.GetUtcNow();
                }

                _apparitionInFlight.TryRemove(index, out _);
            }
        });
    }

    private string ApparitionCachePath
        => Path.Combine(_external.CreateSubDirectoryInAppDataFolder("SmallBodies").FullName, "apparitions.json");

    /// <summary>
    /// Populates the apparition overlay once, from the publish-time seed and then the local cache.
    ///
    /// <para>Order matters: the seed is the baked snapshot, the local cache is only ever written by a
    /// fetch that succeeded on THIS machine, so where both carry a comet the local entry is the fresher
    /// one and layers on top.</para>
    /// </summary>
    private async Task LoadApparitionCacheAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _apparitionsLoaded))
        {
            return;
        }

        // Gated because the fire-and-forget fetch path can reach this concurrently with the load, and
        // two builders taken from the same base would drop whichever upgrade committed in between.
        await _apparitionLoadGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _apparitionsLoaded))
            {
                return;
            }

            var builder = _apparitions.ToBuilder();

            if (await _seed.TryFetchAsync(cancellationToken) is { } seed)
            {
                Merge(builder, seed.Entries);
                if (seed.NoRemoteRefresh)
                {
                    _noRemoteRefresh = true;
                    _logger.LogInformation(
                        "Comet apparition overlay is a sealed publish-time snapshot ({Count} entries); per-object Horizons refresh is disabled on this host",
                        seed.Entries.Length);
                }
            }

            var cached = await _external.TryReadJsonAsync(ApparitionCachePath, SbdbJsonContext.Default.ApparitionCacheFile, _logger, cancellationToken);
            if (cached?.Entries is { } entries)
            {
                Merge(builder, entries);
            }

            _apparitions = builder.ToImmutable();
            Volatile.Write(ref _apparitionsLoaded, true);
        }
        finally
        {
            _apparitionLoadGate.Release();
        }

        static void Merge(ImmutableDictionary<CatalogIndex, ApparitionEntry>.Builder builder, ApparitionEntry[] entries)
        {
            foreach (var entry in entries)
            {
                if (entry.Elements.CatalogIndex is { } index)
                {
                    builder[index] = entry;
                }
            }
        }
    }

    private Task PersistApparitionsAsync(CancellationToken cancellationToken)
    {
        var snapshot = _apparitions;
        var entries = new ApparitionEntry[snapshot.Count];
        var i = 0;
        foreach (var (_, entry) in snapshot)
        {
            entries[i++] = entry;
        }

        return _logger.CatchAsync(
            ct => _external.AtomicWriteJsonAsync(ApparitionCachePath, new ApparitionCacheFile(entries), SbdbJsonContext.Default.ApparitionCacheFile, ct),
            cancellationToken);
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
