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
/// <para>Env-gated (<c>TIANWEN_HOVER_PROBE=1</c>) because it bulk-loads Tycho-2 and then times
/// thousands of resolves. It prints rather than asserts: it is an attribution tool, and the
/// invariants it established are pinned by the benchmark and by <c>SkyMapHoverAndPictureTests</c>
/// instead.</para>
///
/// <para><b>It RANKS terms; it does not price them.</b> Its absolute microseconds run about 3x
/// <c>SkyMapHoverResolveBenchmarks</c> and vary run to run -- a Stopwatch loop inside a test host is
/// not BenchmarkDotNet steady state. Never subtract one of its figures from a benchmark figure, and
/// never quote one as the cost of the resolve. That mistake was made with the first version of this
/// probe and shipped into four documents.</para>
///
/// <para>What it establishes, at the Aquila pointing the benchmark uses (Release, win-arm64):</para>
/// <list type="bullet">
/// <item>The nine cells are the SAME at every zoom -- they derive from the unprojected pointer
/// position, not the field of view -- and hold 22 deep-sky entries against 1094 composite ones.</item>
/// <item>So the cliff is not a per-star cost that varies with zoom. It is whether the star pass RUNS
/// at all: the deep-sky pass short-circuits it, and that pass floors its hit test at a fixed 20
/// SCREEN pixels, which is 0.020 deg of sky at 1 degree FOV and 4.200 deg at 170. The nearest
/// deep-sky-grid entry here is HD 183919 at 0.409 deg, so the pass misses at 1 and 10 and matches at
/// 60 and 170. The printed RAN/SKIPPED column is predicted from that geometry alone and agrees with
/// the measured times at all four.</item>
/// <item>The magnitude limit is the minor term and runs the OTHER way: 1 degree is dearer than 10
/// over identical cells and identical lookups, because zoomed IN the limit admits MORE stars to the
/// projection, not fewer.</item>
/// <item><b>Inside the pass, ranked:</b> <c>TryLookupByIndex</c> at ~905 ns for each of the 1094
/// candidates is the dominant term, three to four times the nine cell lookups (~320 us) that fed it.
/// Within those, <c>Tycho2RaDecIndex.GetStarsInCell</c> reads only 6x what it keeps (6572 entries
/// over 16 GSC regions for 1072 stars), and <c>Tyc2CatalogIndex</c> is ~14% of the scan. The
/// over-scan and the index packing were both guessed at as the cause before this was measured, and
/// both guesses were wrong -- which is the whole reason the splits below are printed separately.</item>
/// <item>At ~205 B per candidate, the benchmark's 225 KB points at the same term the timing ranks
/// first. <b>The fix both halves share:</b> the 17-byte Tycho-2 entry already carries RA, Dec and
/// magnitude, so the filter, the projection and the hit test could all run before any lookup,
/// leaving ONE <c>TryLookupByIndex</c> for the winner rather than 1094.</item>
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

        // INSIDE that lookup: the cell box keeps a fraction of what the region scan reads, and the
        // ratio is what decides whether this wants a per-cell index or only a cheaper buffer.
        if (db is CelestialObjectDB { Tycho2RaDecIndexForDiagnostics: { } tycho2 })
        {
            var regions = 0;
            var scanned = 0;
            var yielded = 0;
            foreach (var (probeRa, probeDec) in Probes())
            {
                var (r, s, y) = tycho2.DescribeCellScan(probeRa, probeDec);
                regions += r;
                scanned += s;
                yielded += y;
            }

            output.WriteLine(
                $"inside the 9 composite lookups: {regions} GSC regions, {scanned} entries READ, " +
                $"{yielded} kept ({(scanned > 0 ? (double)scanned / Math.Max(yielded, 1) : 0):F0}x over-scan)");

            // And how much of the composite's cost IS that scan: call the Tycho-2 cell lookup
            // directly, with no CompositeRaDecIndex, no deep-sky half and no boxed enumerator.
            for (var warm = 0; warm < 20; warm++)
            {
                foreach (var (probeRa, probeDec) in Probes())
                {
                    tycho2.GetStarsInCell(probeRa, probeDec);
                }
            }

            const int M = 200;
            var swRaw = Stopwatch.StartNew();
            for (var i = 0; i < M; i++)
            {
                foreach (var (probeRa, probeDec) in Probes())
                {
                    tycho2.GetStarsInCell(probeRa, probeDec);
                }
            }

            swRaw.Stop();
            var rawUs = swRaw.Elapsed.TotalMicroseconds / M;
            output.WriteLine(
                $"  of which GetStarsInCell x9 direct: {rawUs,8:F1} us " +
                $"({rawUs * 1000.0 / Math.Max(scanned, 1),5:F1} ns per entry read)");

            // And of THAT, how much is building the CatalogIndex for each KEPT star. Tyc2CatalogIndex
            // base91-encodes a packed ulong and re-packs the digits 7 bits each: no allocation since
            // the string round-trip went, but the arithmetic is still per kept star.
            for (var warm = 0; warm < 1000; warm++)
            {
                CatalogUtils.Tyc2CatalogIndex(Catalog.Tycho2, 486, 1093, 1);
            }

            var swEnc = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++)
            {
                for (var s = 0; s < yielded; s++)
                {
                    CatalogUtils.Tyc2CatalogIndex(Catalog.Tycho2, (ushort)(486 + (s & 0xFF)), (ushort)(1093 + s), 1);
                }
            }

            swEnc.Stop();
            var encUs = swEnc.Elapsed.TotalMicroseconds / 200;
            output.WriteLine(
                $"  of which Tyc2CatalogIndex x{yielded}:      {encUs,8:F1} us " +
                $"({encUs * 1000.0 / Math.Max(yielded, 1),5:F0} ns each, {encUs / Math.Max(rawUs, 0.001) * 100,4:F0}% of the scan)");
            output.WriteLine("");
        }

        // The OTHER half of the star pass: what the loop BODY costs per candidate the lookup handed
        // it. TryLookupByIndex runs for every one of them, before the magnitude filter can reject it.
        var all = new System.Collections.Generic.List<CatalogIndex>();
        foreach (var (probeRa, probeDec) in Probes())
        {
            foreach (var idx in starGrid[probeRa, probeDec])
            {
                all.Add(idx);
            }
        }

        for (var warm = 0; warm < 20; warm++)
        {
            foreach (var idx in all)
            {
                db.TryLookupByIndex(idx, out _);
            }
        }

        var swLookupByIndex = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            foreach (var idx in all)
            {
                db.TryLookupByIndex(idx, out _);
            }
        }

        swLookupByIndex.Stop();
        var byIdxUs = swLookupByIndex.Elapsed.TotalMicroseconds / 200;
        output.WriteLine(
            $"TryLookupByIndex x{all.Count} (the loop BODY): {byIdxUs,8:F1} us " +
            $"({byIdxUs * 1000.0 / Math.Max(all.Count, 1),5:F0} ns each)");
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

    /// <summary>
    /// The benchmark's fixed instant, NOT <c>UtcNow</c>. The planet pass caches on the viewing time,
    /// so a fresh instant per call misses that cache and measures an ephemeris recompute on top of
    /// the search -- which is how this probe first read 3x the benchmark it exists to explain.
    /// </summary>
    private static readonly DateTimeOffset ViewingUtc = new(2026, 6, 21, 22, 0, 0, TimeSpan.Zero);

    private static SkyMapHoverTarget? Resolve(SkyMapState state, ICelestialObjectDB db)
        => SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, db, ViewingUtc,
            ViewportW * 0.5f, ViewportH * 0.5f, pinnedCatalogIndices: null);
}
