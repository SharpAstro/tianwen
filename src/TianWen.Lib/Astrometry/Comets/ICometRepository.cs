using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// The locally-cached set of comets from JPL SBDB, and the single entry point for resolving a comet's
/// live position and magnitude. Mirrors how <see cref="VSOP87a"/> serves the planets, but backed by
/// fetched-and-cached osculating elements rather than a closed-form series.
/// </summary>
public interface ICometRepository
{
    /// <summary>All loaded comets. Empty until <see cref="EnsureLoadedAsync"/> has completed at least once.</summary>
    ImmutableArray<CometElements> All { get; }

    /// <summary>Looks up a comet by its <see cref="Catalog.Comet"/> <see cref="CatalogIndex"/>.</summary>
    bool TryGet(CatalogIndex index, out CometElements elements);

    /// <summary>
    /// Resolves the comet's geocentric astrometric J2000 position (and predicted total magnitude) at
    /// <paramref name="time"/>. Returns false if the index is not a loaded comet or the solve fails.
    /// </summary>
    bool TryGetPosition(CatalogIndex index, DateTimeOffset time, out double raJ2000Hours, out double decJ2000Deg, out double magnitude);

    /// <summary>Loads the comet set once (from fresh cache, else a network fetch). Idempotent + concurrency-safe.</summary>
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the comet set. When the cache is within its TTL and <paramref name="forceRefetch"/> is
    /// false this is a no-op; otherwise it fetches from SBDB and rewrites the cache, falling back to the
    /// (stale) cache if the network is unavailable.
    /// </summary>
    Task RefreshAsync(bool forceRefetch = false, CancellationToken cancellationToken = default);
}
