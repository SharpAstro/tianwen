using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Validates <see cref="CatalogStarCounter"/> against the real (Tycho-2-loaded) celestial object DB:
/// the cumulative magnitude histogram is monotone and agrees with the single-limit count, dense
/// galactic fields out-star sparse ones, and the box test matches an independent re-implementation.
/// These counts are the catalog floor / zenith-gauge prediction the first-scout obstruction oracle
/// is built on.
/// </summary>
[Collection("Astrometry")]
public class CatalogStarCounterTests(ITestOutputHelper output)
{
    // A dense galactic-plane field (Cygnus) and a comparatively sparse off-plane field (Coma).
    private const double DenseRaHours = 20.5, DenseDecDeg = 40.0;
    private const double SparseRaHours = 12.83, SparseDecDeg = 27.13;
    private const double FovWdeg = 3.0, FovHdeg = 2.0;

    [Fact]
    public async Task CountStarsByMagnitude_IsCumulativeAndMonotone()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);

        var bins = CatalogStarCounter.CountStarsByMagnitude(db, DenseRaHours, DenseDecDeg, FovWdeg, FovHdeg);

        bins.Length.ShouldBe(CatalogStarCounter.MagBinCount);
        for (var b = 1; b < bins.Length; b++)
        {
            bins[b].ShouldBeGreaterThanOrEqualTo(bins[b - 1], $"bin {b} ({bins[b]}) must be >= bin {b - 1} ({bins[b - 1]})");
        }
        bins[^1].ShouldBeGreaterThan(0);
        output.WriteLine($"Cygnus cumulative bins (V<=0.5..15): [{string.Join(", ", bins)}]");
    }

    [Theory]
    // Exact 0.5-mag-boundary limits, where CountStarsInField (<= limit) and the binned histogram agree exactly.
    [InlineData(6.0)]
    [InlineData(9.0)]
    [InlineData(11.0)]
    public async Task CountStarsInField_MatchesCumulativeHistogramAtBinBoundary(double magLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);

        var direct = CatalogStarCounter.CountStarsInField(db, DenseRaHours, DenseDecDeg, FovWdeg, FovHdeg, magLimit);
        var bins = CatalogStarCounter.CountStarsByMagnitude(db, DenseRaHours, DenseDecDeg, FovWdeg, FovHdeg);
        var bin = Math.Clamp((int)Math.Round(magLimit / 0.5) - 1, 0, bins.Length - 1);

        direct.ShouldBe(bins[bin]);
        output.WriteLine($"V<={magLimit}: direct={direct} histogram[bin {bin}]={bins[bin]}");
    }

    [Fact]
    public async Task DenseGalacticField_HasMoreStarsThanSparseField()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        const double mag = 11.0;

        var dense = CatalogStarCounter.CountStarsInField(db, DenseRaHours, DenseDecDeg, FovWdeg, FovHdeg, mag);
        var sparse = CatalogStarCounter.CountStarsInField(db, SparseRaHours, SparseDecDeg, FovWdeg, FovHdeg, mag);

        output.WriteLine($"dense(Cygnus)={dense}  sparse(Coma)={sparse}");
        dense.ShouldBeGreaterThan(0);
        dense.ShouldBeGreaterThan(sparse);
    }

    [Fact]
    public async Task BrighterLimit_CountsNoMoreThanFainterLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);

        var toMag8 = CatalogStarCounter.CountStarsInField(db, DenseRaHours, DenseDecDeg, FovWdeg, FovHdeg, 8.0);
        var toMag11 = CatalogStarCounter.CountStarsInField(db, DenseRaHours, DenseDecDeg, FovWdeg, FovHdeg, 11.0);

        toMag8.ShouldBeLessThanOrEqualTo(toMag11);
        toMag11.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CountStarsInField_MatchesIndependentBoxScanOverEnumerateFieldStars()
    {
        // Re-implements the box test independently of CatalogStarCounter's private InBox, sharing only
        // the public EnumerateFieldStars cell-walk + the documented search radius, so a regression in
        // the box geometry shows up as a mismatch.
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        const double mag = 10.0;
        const double Deg2Rad = Math.PI / 180.0;
        var cosDec = Math.Cos(DenseDecDeg * Deg2Rad);
        var searchRadius = Math.Sqrt(FovWdeg * FovWdeg + FovHdeg * FovHdeg) * 0.5 + 0.1;

        var independent = 0;
        foreach (var obj in CatalogStarCounter.EnumerateFieldStars(db, DenseRaHours, DenseDecDeg, searchRadius))
        {
            if ((double)obj.V_Mag > mag) continue;
            if (Math.Abs((double)obj.Dec - DenseDecDeg) > FovHdeg * 0.5) continue;
            var dRa = obj.RA - DenseRaHours;
            dRa -= 24.0 * Math.Round(dRa / 24.0);
            if (Math.Abs(dRa * 15.0 * cosDec) > FovWdeg * 0.5) continue;
            independent++;
        }

        var counted = CatalogStarCounter.CountStarsInField(db, DenseRaHours, DenseDecDeg, FovWdeg, FovHdeg, mag);

        counted.ShouldBe(independent);
        output.WriteLine($"counted={counted} independent={independent}");
    }
}
