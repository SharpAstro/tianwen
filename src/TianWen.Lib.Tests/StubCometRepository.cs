using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;

namespace TianWen.Lib.Tests;

/// <summary>
/// An <see cref="ICometRepository"/> over a fixed element set, for the surfaces that augment
/// themselves from the repository (search keys, the sky-map index, the planner). Shared rather than
/// re-declared per test class: several suites need the same "two comets that share a common name"
/// shape, which is the case most of the naming behaviour turns on.
/// </summary>
internal sealed class StubCometRepository(params CometElements[] comets) : ICometRepository
{
    public ImmutableArray<CometElements> All => [.. comets];

    /// <summary>
    /// A comet with the given designation and SBDB common name. Orbital elements are placeholders:
    /// every caller here is testing identity and naming, not the ephemeris.
    /// </summary>
    public static CometElements Comet(string designation, string? commonName)
    {
        if (!CometDesignation.TryParse(designation, out var parsed))
        {
            throw new ArgumentException($"not a comet designation: {designation}", nameof(designation));
        }
        return new CometElements(parsed, commonName, 0.9, 0.9, 60.0, 100.0, 100.0, 2460000.0, 2460000.0, 8.0, 10.0);
    }

    public bool TryGet(CatalogIndex index, out CometElements elements)
    {
        foreach (var c in comets)
        {
            if (c.CatalogIndex is { } idx && idx == index)
            {
                elements = c;
                return true;
            }
        }
        elements = default;
        return false;
    }

    /// <summary>
    /// Fixed positions returned by <see cref="TryGetPosition"/>, for the callers that only care that a
    /// comet HAS a live position and moves. Empty means "no ephemeris", the default, which is what the
    /// naming tests want (they must not depend on the propagator).
    /// </summary>
    public Dictionary<CatalogIndex, (double RaHours, double DecDeg, double VMag)> Positions { get; } = [];

    public bool TryGetPosition(CatalogIndex index, DateTimeOffset time, out double raJ2000Hours, out double decJ2000Deg, out double magnitude)
    {
        if (Positions.TryGetValue(index, out var p))
        {
            (raJ2000Hours, decJ2000Deg, magnitude) = p;
            return true;
        }
        raJ2000Hours = decJ2000Deg = magnitude = double.NaN;
        return false;
    }

    /// <summary>Indices a caller asked to upgrade to current-apparition elements. Recorded rather than
    /// ignored, so a test can assert that the surfaces which SHOULD ask actually do.</summary>
    public List<CatalogIndex> ApparitionRequests { get; } = [];

    public void RequestCurrentApparition(CatalogIndex index) => ApparitionRequests.Add(index);

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RefreshAsync(bool forceRefetch = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
