using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>SkyMapHoverResolveBenchmarks</c> measures that the hover resolve costs ~9 us over a catalogued
/// object and ~360 us over bare star field at a deep zoom, collapsing to ~1.7 us past 60 degrees. It
/// cannot say WHY, and the first written explanation was wrong: the cliff was attributed to
/// <c>EffectiveMagnitudeLimit</c> tightening as you zoom out. This probe attributes it instead, which
/// is the only reason the correction is a fact rather than a second guess.
/// </summary>
/// <remarks>
/// <para>Env-gated (<c>TIANWEN_HOVER_PROBE=1</c>) because it bulk-loads Tycho-2 and then times four
/// thousand resolves. It prints rather than asserts: it is an attribution tool, and the invariants it
/// established are pinned by the benchmark and by <c>SkyMapHoverAndPictureTests</c> instead.</para>
///
/// <para>What it establishes, at the Aquila pointing the benchmark uses (Release, win-arm64):</para>
/// <list type="bullet">
/// <item>The nine cells are the SAME at every zoom -- they derive from the unprojected pointer
/// position, not the field of view -- and hold 22 deep-sky entries against 1094 composite ones.</item>
/// <item>Nine deep-sky lookups cost 0.5 us; nine composite lookups cost ~351 us, before a single star
/// has been tested. That is <c>Tycho2RaDecIndex.GetStarsInCell</c>, and it IS the number.</item>
/// <item>So the cliff is not a per-star cost that varies with zoom. It is whether the star pass RUNS
/// at all: the deep-sky pass short-circuits it, and that pass floors its hit test at a fixed 20
/// SCREEN pixels, which is 0.020 deg of sky at 1 degree FOV and 4.200 deg at 170. The nearest
/// deep-sky-grid entry here is HD 183919 at 0.409 deg, so the pass misses at 1 and 10 and matches at
/// 60 and 170.</item>
/// <item>The magnitude limit is the minor term and runs the OTHER way: 1 degree is dearer than 10
/// over identical cells and identical lookups, because zoomed IN the limit admits MORE stars to the
/// projection, not fewer.</item>
/// </list>
/// </remarks>
[Collection("Astrometry")]
public class SkyMapHoverResolveCostProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_HOVER_PROBE";

    /// <summary>The benchmark's dense-Milky-Way pointing in Aquila, with no large nebula on it.</summary>
    private const double PointingRaHours = 19.5;
    private const double PointingDecDeg = 5.0;

    private const float ViewportW = 1600f;
    private const float ViewportH = 1000f;

    /// <summary>Mirrors <c>SkyMapSearchActions.ClickToleranceScreenPx</c>, which is private there.</summary>
    private const double ClickToleranceScreenPx = 20.0;

    [Fact]
    public async Task WhereTheHoverResolveTimeGoes()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 }, $"{EnvVar} not set");

        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        var dsoGrid = db.DeepSkyCoordinateGrid;
        var starGrid = db.CoordinateGrid;

        var dsoEntries = 0;
        var starEntries = 0;
        var nearestDsoSepDeg = double.MaxValue;
        var nearestDso = "";
        foreach (var (probeRa, probeDec) in Probes())
        {
            foreach (var idx in dsoGrid[probeRa, probeDec])
            {
                dsoEntries++;
                if (!db.TryLookupByIndex(idx, out var o) || double.IsNaN(o.RA))
                {
                    continue;
                }

                var sep = SeparationDeg(o.RA, o.Dec);
                if (sep < nearestDsoSepDeg)
                {
                    nearestDsoSepDeg = sep;
                    nearestDso = idx.ToCanonical();
                }
            }

            foreach (var _ in starGrid[probeRa, probeDec])
            {
                starEntries++;
            }
        }

        output.WriteLine($"9-cell window at RA {PointingRaHours}h Dec {PointingDecDeg}, IDENTICAL at every zoom:");
        output.WriteLine($"  {dsoEntries} deep-sky entries, {starEntries} composite entries");
        output.WriteLine($"  nearest deep-sky entry: {nearestDso} at {nearestDsoSepDeg:F3} deg");
        output.WriteLine("");

        // The lookups alone: no projection, no hit test, no planets. Whatever this costs, the star
        // pass has paid it before it has looked at one star.
        for (var warm = 0; warm < 20; warm++)
        {
            foreach (var (probeRa, probeDec) in Probes())
            {
                foreach (var _ in starGrid[probeRa, probeDec])
                {
                }
            }
        }

        output.WriteLine($"9 deep-sky lookups alone:  {TimeLookups(db, deepSky: true),8:F1} us");
        output.WriteLine($"9 composite lookups alone: {TimeLookups(db, deepSky: false),8:F1} us   <- Tycho2RaDecIndex.GetStarsInCell");
        output.WriteLine("");

        foreach (var fov in new[] { 1.0, 10.0, 60.0, 170.0 })
        {
            var state = new SkyMapState
            {
                Mode = SkyMapMode.Equatorial,
                CenterRA = PointingRaHours,
                CenterDec = PointingDecDeg,
                FieldOfViewDeg = fov,
                ShowObjectOverlay = true,
                ShowDarkNebulae = true,
                LastContentRect = new RectF32(0f, 0f, ViewportW, ViewportH),
            };
            state.CurrentViewMatrix = state.ComputeViewMatrix();

            var hit = Resolve(state, db);
            for (var i = 0; i < 20; i++)
            {
                Resolve(state, db);
            }

            const int N = 200;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < N; i++)
            {
                Resolve(state, db);
            }

            sw.Stop();

            // What the 20 px floor is worth in SKY here, which is the deep-sky pass's whole reach.
            var toleranceDeg = ClickToleranceScreenPx / SkyMapProjection.PixelsPerRadian(ViewportH, fov) * 180.0 / Math.PI;
            var what = hit is { } t && db.TryLookupByIndex(t.Index, out var ho)
                ? $"{t.Index.ToCanonical()} [{ho.ObjectType}]"
                : "nothing";
            var starPass = toleranceDeg < nearestDsoSepDeg ? "star pass RAN" : "star pass SKIPPED";

            output.WriteLine(
                $"FOV {fov,6}: 20px = {toleranceDeg,6:F3} deg -> {starPass,-17} " +
                $"resolved {what,-28} {sw.Elapsed.TotalMicroseconds / N,8:F1} us");
        }
    }

    /// <summary>
    /// The nine probes <c>TryResolveHit</c> builds: one index cell step in each direction from the
    /// unprojected pointer. Note what is NOT an input here -- the field of view.
    /// </summary>
    private static (double Ra, double Dec)[] Probes()
    {
        const double CellRaHours = 1.0 / 15.0;
        var probes = new (double, double)[9];
        var k = 0;
        for (var di = -1; di <= 1; di++)
        {
            for (var dj = -1; dj <= 1; dj++)
            {
                probes[k++] = ((PointingRaHours + di * CellRaHours + 24.0) % 24.0,
                    Math.Clamp(PointingDecDeg + dj, -90.0, 90.0));
            }
        }

        return probes;
    }

    private static double SeparationDeg(double ra, double dec)
    {
        var dRa = (ra - PointingRaHours) * 15.0 * Math.Cos(PointingDecDeg * Math.PI / 180.0);
        var dDec = dec - PointingDecDeg;
        return Math.Sqrt(dRa * dRa + dDec * dDec);
    }

    private static double TimeLookups(ICelestialObjectDB db, bool deepSky)
    {
        const int N = 200;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < N; i++)
        {
            // Read the property inside the loop, as a caller does: both grids are PROPERTIES that
            // return a freshly built CompositeRaDecIndex on every read.
            var grid = deepSky ? db.DeepSkyCoordinateGrid : db.CoordinateGrid;
            foreach (var (probeRa, probeDec) in Probes())
            {
                foreach (var _ in grid[probeRa, probeDec])
                {
                }
            }
        }

        sw.Stop();
        return sw.Elapsed.TotalMicroseconds / N;
    }

    private static SkyMapHoverTarget? Resolve(SkyMapState state, ICelestialObjectDB db)
        => SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, db, DateTimeOffset.UtcNow,
            ViewportW * 0.5f, ViewportH * 0.5f, pinnedCatalogIndices: null);
}
