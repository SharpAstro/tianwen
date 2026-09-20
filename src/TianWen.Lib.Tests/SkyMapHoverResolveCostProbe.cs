using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>SkyMapHoverResolveBenchmarks</c> measures that the hover resolve costs ~10 us over a catalogued
/// object and ~166 us / 27.6 KB over bare star field at a deep zoom (389 us / 225 KB before
/// 2026-09-20), collapsing to ~1.8 us past 60 degrees. It cannot say WHY, and the first written
/// explanation was wrong: the cliff was attributed to <c>EffectiveMagnitudeLimit</c> tightening as
/// you zoom out. This probe attributes it instead, which is the only reason the correction is a fact
/// rather than a second guess -- and it is what found the fix.
/// </summary>
/// <remarks>
/// <para>Env-gated (<c>TIANWEN_HOVER_PROBE=1</c>) because it bulk-loads Tycho-2 and then times
/// thousands of resolves. It prints rather than asserts: it is an attribution tool, and the
/// invariants it established are pinned by the benchmark and by <c>SkyMapHoverAndPictureTests</c>
/// instead.</para>
///
/// <para><b>Run it with <c>DOTNET_TieredCompilation=0</c>, or it ranks terms by the order they were
/// timed in.</b> The test host tiers a method up only after a quiet period with no new JIT activity,
/// which a test that keeps calling new code never grants, so a loop timed early runs Tier-0 code and
/// the same loop timed at the end runs optimised code: the lookup loop below measured 992 us first and
/// 290 us when re-timed last in one run. That is what the "905 ns per lookup" and the "probe runs 3x
/// the benchmark" once written here and in four documents were -- JIT order, not a Stopwatch in a
/// test host. With tiering off, every term re-times within 5 percent of its first figure, the parts
/// sum to the whole they are parts of, and the whole lands within 7 percent of the benchmark
/// (417 us against 389 at 1 degree; the difference is the dynamic PGO the benchmark has and a
/// tiering-off run cannot). The allocation figures come from the runtime's own counter and are exact
/// at either setting: the per-resolve bytes reproduce the benchmark's to the byte (224,880 and
/// 225,064). The two "re-timed last" lines at the end exist to show whether order is still showing
/// through. Quote the benchmark for what a resolve costs; quote this for how the cost splits.</para>
///
/// <para>What it establishes, at the Aquila pointing the benchmark uses (Release, win-arm64,
/// tiering off):</para>
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
/// <item><b>Inside the pass BEFORE the fix, per resolve on bare sky at 10 degrees (389 us / 225 KB):</b>
/// <c>TryLookupByIndex</c> for each of the 1094 candidates is 320 us and 197 KB (293 ns, 180 B
/// each), five sixths of the time and seven eighths of the bytes; the nine composite cell lookups
/// that fed it are 53 us and ~28 KB; the magnitude gate, projection and hit test are the ~16 us left
/// (~45 at 1 degree, which is the whole of the magnitude term). Within the cell lookups,
/// <c>Tycho2RaDecIndex.GetStarsInCell</c> reads only 6x what it keeps (6572 entries over 16 GSC
/// regions for 1072 stars) and <c>Tyc2CatalogIndex</c> is 20 us of it. The over-scan and the index
/// packing were both guessed at as the cause before this was measured, and both guesses were
/// wrong -- which is the whole reason the splits below are printed separately.</item>
/// <item><b>Inside the lookup, two terms, both allocating, neither needed by a hit test.</b>
/// <c>ConstellationBoundary.TryFindConstellation</c> is 174 us and 120 KB (162 ns, 112 B each): it
/// precesses the star from J2000 to B1875 through <c>CoordinateUtils.PrecessRadians</c>, which builds
/// its two vectors and its 3x3 rotation matrix as heap arrays, to fill
/// <c>CelestialObject.Constellation</c> -- a field the resolver never reads. And the binary search
/// itself, <c>TryGetTycho2Star</c>, is 111 us and 78 KB (102 ns, 71 B each), where the bytes are
/// <c>CatalogIndex.ToCatalogAndValue</c> building a string and a byte array to decode the base91
/// index: the mirror of the string round trip <c>Tyc2CatalogIndex</c> took out of the ENCODE side.</item>
/// <item><b>So the fixes were three, and two of them not in the resolver at all</b> (all taken
/// 2026-09-20): an allocation-free <c>PrecessRadians</c> and an allocation-free
/// <c>ToCatalogAndValue</c>, one function each in <c>TianWen.Lib</c>, paying off on every catalogue
/// lookup in the program; and the star pass calling <c>TryGetTycho2Star</c> (which already existed,
/// and is the 17-byte entry as a struct) for its candidates and <c>TryLookupByIndex</c> only for the
/// winner, which removed the constellation term from the resolve outright.
/// <c>Tycho2LiteLookupParityTests</c> pins that the two lookups read the same star.</item>
/// <item><b>After, same pointing, same setting:</b> the resolve is ~150-175 us and 27,632 B on bare
/// sky at 1 and 10 degrees (the benchmark says 166 and 139 us); <c>TryLookupByIndex</c> is 230 ns
/// and 0 B, <c>TryGetTycho2Star</c> 76 ns and 0 B, <c>TryFindConstellation</c> 130 ns and 0 B. What
/// is left is the nine cell lookups (~50 us and the whole 27.6 KB: a <c>List</c> per cell plus the
/// composite's iterator) and about 1,100 binary searches.</item>
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

        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") is not "0")
        {
            output.WriteLine("WARNING: DOTNET_TieredCompilation is not 0. The terms below rank by the ORDER they were");
            output.WriteLine("         timed in, not by cost: a loop timed early runs Tier-0 code. See the class doc.");
            output.WriteLine("");
        }

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

        // Every term this probe times is exercised here BEFORE any of them is timed. Without this,
        // the terms rank by the order they were measured in: the test host tiers a method up only
        // after a quiet period, so a loop timed early ran Tier-0 code and a loop timed late ran
        // optimised code, and the first version of this probe printed parts that summed to more
        // than the whole they were parts of. Each block below keeps its own short warm-up as well.
        foreach (var fov in Fovs)
        {
            var warmState = MakeState(fov);
            for (var i = 0; i < 30; i++)
            {
                Resolve(warmState, db);
            }
        }

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
        var (_, byIdxBytes) = Measure(() =>
        {
            foreach (var idx in all)
            {
                db.TryLookupByIndex(idx, out _);
            }
        });
        output.WriteLine(
            $"TryLookupByIndex x{all.Count} (the loop BODY): {byIdxUs,8:F1} us " +
            $"({byIdxUs * 1000.0 / Math.Max(all.Count, 1),5:F0} ns each, {byIdxBytes / Math.Max(all.Count, 1),4:F0} B each)");

        // INSIDE that lookup, for a Tycho-2 index: a binary search for the 17-byte entry, and then
        // ConstellationBoundary.TryFindConstellation to fill CelestialObject.Constellation -- a field
        // the hit test never reads. That call precesses J2000 to B1875 through
        // CoordinateUtils.Precess, which builds its two vectors and its rotation matrix as heap
        // arrays. TryGetTycho2Star is the SAME binary search returning the 17-byte entry as a struct,
        // with no constellation: it is what a filter-before-lookup star pass would call.
        var positions = new List<(double Ra, double Dec)>(all.Count);
        foreach (var idx in all)
        {
            if (db.TryGetTycho2Star(idx, out var lite))
            {
                positions.Add((lite.RaHours, lite.DecDeg));
            }
        }

        var (liteUs, liteBytes) = Measure(() =>
        {
            foreach (var idx in all)
            {
                db.TryGetTycho2Star(idx, out _);
            }
        });
        var (constUs, constBytes) = Measure(() =>
        {
            foreach (var (ra, dec) in positions)
            {
                ConstellationBoundary.TryFindConstellation(ra, dec, out _);
            }
        });
        output.WriteLine(
            $"  of which TryGetTycho2Star x{all.Count} (binary search only): {liteUs,8:F1} us " +
            $"({liteUs * 1000.0 / Math.Max(all.Count, 1),5:F0} ns each, {liteBytes / Math.Max(all.Count, 1),4:F0} B each)");
        output.WriteLine(
            $"  of which TryFindConstellation x{positions.Count}:            {constUs,8:F1} us " +
            $"({constUs * 1000.0 / Math.Max(positions.Count, 1),5:F0} ns each, {constBytes / Math.Max(positions.Count, 1),4:F0} B each)");
        output.WriteLine("");

        foreach (var fov in Fovs)
        {
            var state = MakeState(fov);

            var hit = Resolve(state, db);
            for (var i = 0; i < 20; i++)
            {
                Resolve(state, db);
            }

            var (resolveUs, resolveBytes) = Measure(() => Resolve(state, db));

            // What the 20 px floor is worth in SKY here, which is the deep-sky pass's whole reach.
            var toleranceDeg = ClickToleranceScreenPx / SkyMapProjection.PixelsPerRadian(ViewportH, fov) * 180.0 / Math.PI;
            var what = hit is { } t && db.TryLookupByIndex(t.Index, out var ho)
                ? $"{t.Index.ToCanonical()} [{ho.ObjectType}]"
                : "nothing";
            var starPass = toleranceDeg < nearestDsoSepDeg ? "star pass RAN" : "star pass SKIPPED";

            output.WriteLine(
                $"FOV {fov,6}: 20px = {toleranceDeg,6:F3} deg -> {starPass,-17} " +
                $"resolved {what,-28} {resolveUs,8:F1} us {resolveBytes,9:F0} B");
        }

        // The two headline terms again, now that everything has run: if these differ from the
        // figures above by more than noise, the order of measurement was still showing through.
        output.WriteLine("");
        output.WriteLine($"re-timed last: 9 composite lookups alone: {TimeLookups(db, deepSky: false),8:F1} us");
        var (againUs, _) = Measure(() =>
        {
            foreach (var idx in all)
            {
                db.TryLookupByIndex(idx, out _);
            }
        });
        output.WriteLine($"re-timed last: TryLookupByIndex x{all.Count}:        {againUs,8:F1} us");
    }

    private static readonly double[] Fovs = [1.0, 10.0, 60.0, 170.0];

    private static SkyMapState MakeState(double fov)
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
        return state;
    }

    /// <summary>
    /// Mean wall time and mean bytes allocated on this thread per call of <paramref name="body"/>,
    /// over <paramref name="n"/> calls. The allocation figure is exact (the runtime's own counter);
    /// the time is a Stopwatch mean in a test host, so read it against the caveat in the class doc.
    /// </summary>
    private static (double Us, double Bytes) Measure(Action body, int n = 200)
    {
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
        {
            body();
        }

        sw.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        return (sw.Elapsed.TotalMicroseconds / n, (double)bytes / n);
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
    /// the search. (It was checked as the cause of the probe once reading 3x the benchmark, and was
    /// not -- that was JIT tiering, see the class doc -- but the pin is right regardless.)
    /// </summary>
    private static readonly DateTimeOffset ViewingUtc = new(2026, 6, 21, 22, 0, 0, TimeSpan.Zero);

    private static SkyMapHoverTarget? Resolve(SkyMapState state, ICelestialObjectDB db)
        => SkyMapSearchActions.ResolveHoverAtScreenPoint(
            state, db, ViewingUtc,
            ViewportW * 0.5f, ViewportH * 0.5f, pinnedCatalogIndices: null);
}
