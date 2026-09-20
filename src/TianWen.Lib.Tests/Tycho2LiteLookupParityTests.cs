using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The sky map's star pass reads a Tycho-2 candidate through <see cref="ICelestialObjectDB.TryGetTycho2Star"/>
/// (the 17-byte entry as a struct) and looks up only the winner through
/// <see cref="ICelestialObjectDB.TryLookupByIndex"/> (a <see cref="CelestialObject"/>, whose constellation
/// costs a precession per call). That is only correct if the two read the SAME star: same position, a star's
/// object type, and a magnitude that differs by no more than the <see cref="Half"/> the object stores it in.
/// A Tycho-2 index that is also a merged main entry with another type or another magnitude would break the
/// premise silently, so this walks every cell of the composite grid and asks, for the whole catalogue.
/// </summary>
[Collection("Astrometry")]
public class Tycho2LiteLookupParityTests
{
    [Fact]
    public async Task EveryTycho2CandidateReadsTheSameEitherWay()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var grid = db.CoordinateGrid;

        var seen = 0;
        var mismatches = new List<string>();
        for (var raIdx = 0; raIdx < 360; raIdx++)
        {
            var ra = (raIdx + 0.5) / 15.0;
            for (var decIdx = 0; decIdx <= 180; decIdx++)
            {
                var dec = Math.Min(-90.0 + decIdx + 0.5, 90.0);
                foreach (var idx in grid[ra, dec])
                {
                    if (!db.TryGetTycho2Star(idx, out var lite))
                    {
                        continue;
                    }

                    seen++;
                    if (!db.TryLookupByIndex(idx, out var o))
                    {
                        Record(mismatches, idx, "the lightweight lookup answers, the full one does not");
                        continue;
                    }

                    // The full lookup stores the entry's float magnitude as a Half; compare at that width.
                    var liteMagAsHalf = float.IsNaN(lite.VMag) ? float.NaN : (float)(Half)lite.VMag;
                    var fullMag = (float)o.V_Mag;
                    var magsAgree = (float.IsNaN(liteMagAsHalf) && float.IsNaN(fullMag)) || liteMagAsHalf == fullMag;
                    if (!o.ObjectType.IsStar || o.RA != lite.RaHours || o.Dec != lite.DecDeg || !magsAgree)
                    {
                        Record(mismatches, idx,
                            $"full {o.ObjectType} RA {o.RA} Dec {o.Dec} V {fullMag}; lite RA {lite.RaHours} Dec {lite.DecDeg} V {lite.VMag}");
                    }
                }
            }
        }

        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches));
        // The premise of the premise: the walk covered the catalogue, not a corner of it. A star sits in
        // exactly one cell box, so this is the star count less the handful on the RA seam and the pole.
        seen.ShouldBeGreaterThan((int)(db.Tycho2StarCount * 0.99));
    }

    private static void Record(List<string> mismatches, CatalogIndex idx, string what)
    {
        if (mismatches.Count < 20)
        {
            mismatches.Add($"{idx.ToCanonical()}: {what}");
        }
    }
}
