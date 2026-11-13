using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Astrometry.MeteorShowers;

/// <summary>
/// Where a shower's radiant is on one night, from the table alone (no site, no dark window).
/// </summary>
/// <param name="RaDeg">Right ascension, degrees.</param>
/// <param name="DecDeg">Declination, degrees.</param>
/// <param name="Active">Whether the night is inside the shower's activity window. Outside it the radiant is the one the drift
/// gives at the window's nearer end, so a surface that only LOOKS at a shower still has a place to centre.</param>
public readonly record struct MeteorShowerRadiant(double RaDeg, double DecDeg, bool Active)
{
    /// <summary>Right ascension in hours, the unit <c>Target.RA</c> and the sky map use.</summary>
    public double RaHours => RaDeg / 15.0;
}

/// <summary>
/// A shower's identity: a <see cref="CatalogIndex"/> in <see cref="Catalog.MeteorShower"/> per shower (its IAU number), so a
/// shower is matched by index wherever a comet or planet is, and the object DB holds none.
/// </summary>
public static class MeteorShowerIdentity
{
    private static readonly FrozenDictionary<CatalogIndex, MeteorShower> ByIndex = BuildByIndex();

    private static readonly FrozenDictionary<string, MeteorShower> ByCodeOrName = BuildByCodeOrName();

    private static FrozenDictionary<CatalogIndex, MeteorShower> BuildByIndex()
    {
        var map = new Dictionary<CatalogIndex, MeteorShower>();
        foreach (var shower in MeteorShowerTable.All)
        {
            map[Compute(shower)] = shower;
        }

        return map.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, MeteorShower> BuildByCodeOrName()
    {
        var map = new Dictionary<string, MeteorShower>(StringComparer.OrdinalIgnoreCase);
        foreach (var shower in MeteorShowerTable.All)
        {
            map[shower.Name] = shower;
            map.TryAdd(shower.Code, shower);
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static CatalogIndex Compute(MeteorShower shower)
        => CatalogUtils.TryGetCleanedUpCatalogName($"MSh{shower.IauNumber:000}", out var index)
            ? index
            : throw new InvalidOperationException($"No catalogue index for shower {shower.Code}");

    /// <summary>The <see cref="CatalogIndex"/> of <paramref name="shower"/>.</summary>
    public static CatalogIndex IndexOf(MeteorShower shower) => Compute(shower);

    /// <summary>The shower a <see cref="CatalogIndex"/> names, or false when it is not a shower's.</summary>
    public static bool TryGet(CatalogIndex index, [NotNullWhen(true)] out MeteorShower? shower)
        => ByIndex.TryGetValue(index, out shower);

    /// <summary>A shower by its exact name or IAU code, ignoring case and surrounding spaces.</summary>
    public static bool TryResolve(string? query, [NotNullWhen(true)] out MeteorShower? shower)
    {
        shower = null;
        return !string.IsNullOrWhiteSpace(query) && ByCodeOrName.TryGetValue(query.Trim(), out shower);
    }
}
