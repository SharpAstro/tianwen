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
    /// Asks, without blocking, for this comet's elements to be upgraded to the apparition IN PROGRESS.
    /// Call it for a comet the user is actually looking at (pinned, or bright enough to draw).
    ///
    /// <para><b>Why a comet needs this and a planet does not.</b> The bulk SBDB record is stated at
    /// whatever osculating epoch its solution used, often an earlier apparition, and two-body
    /// propagation from there carries a fixed period while the real comet's period is being changed by
    /// outgassing. The error is in PHASE and it compounds: 10P's 2016 record puts perihelion 3.76 days
    /// late by 2026, which is 9.3 degrees of sky. Fetching the osculating set for today removes it
    /// without modelling non-gravitational forces, because osculating elements at time T already carry
    /// the perturbation state at T.</para>
    ///
    /// <para>Fire-and-forget by design: it is called from render and poll paths that cannot await, it
    /// is single-flight per comet, and it publishes by swapping an immutable map, so the next
    /// <see cref="TryGetPosition"/> simply gets the better answer. Failure is silent and harmless, and
    /// leaves <see cref="CometElements.IsElementSetStale"/> reporting true so the UI keeps saying the
    /// position is approximate.</para>
    /// </summary>
    void RequestCurrentApparition(CatalogIndex index);

    /// <summary>
    /// Refreshes the comet set. When the cache is within its TTL and <paramref name="forceRefetch"/> is
    /// false this is a no-op; otherwise it fetches from SBDB and rewrites the cache, falling back to the
    /// (stale) cache if the network is unavailable.
    /// </summary>
    Task RefreshAsync(bool forceRefetch = false, CancellationToken cancellationToken = default);
}
