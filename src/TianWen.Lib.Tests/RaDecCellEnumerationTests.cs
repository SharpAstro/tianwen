using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="IRaDecIndex.EnumerateCell"/> is the indexer with the per-cell <c>List</c> taken out:
/// a struct that scans the Tycho-2 regions as the caller advances. Two things have to hold for
/// that to be a refactor and not a new answer. It must yield exactly what the indexer yields, in
/// the indexer's order, for every cell of the sky, against an oracle that is NOT the same scan:
/// every Tycho-2 star bucketed into its cell box from <see cref="ICelestialObjectDB.CopyTycho2Stars"/>.
/// And a nine-cell walk, the sky map's hover resolve, must allocate nothing, which the indexer's walk
/// is first shown NOT to do so the measurement is known to see the bytes it is asserting on.
/// <para>
/// The whole-sky walk also pins two facts about the baked blob that nothing else did, because
/// <c>Tycho2LiteLookupParityTests</c> skipped a candidate its lightweight lookup could not read.
/// 254 identifiers are in the blob TWICE: Supplement 1 lists a star the main catalogue already
/// has (TYC 2271-1073-1 at V 7.555 and again at V 10.6), and the baker appends the supplement
/// without asking. And ONE entry is unaddressable: TYC 1327-606-4, whose component number the
/// packed index truncates to 0 in its two-bit field (<c>CatalogUtils.TYC3_MASK</c>), so the index
/// the scan yields for it decodes to a star that does not exist. Both are pre-existing, both are
/// in <c>docs/known-limitations.md</c>, and a re-bake that fixes either fails the exact counts
/// below on purpose: they are the fixture's premise, and the numbers should move WITH the blob.
/// </para>
/// </summary>
[Collection("Astrometry")]
public class RaDecCellEnumerationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EveryCellReadsTheSameThroughTheStructAsThroughTheIndexerAndTheCatalogue()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var grid = db.CoordinateGrid;
        var deepSky = db.DeepSkyCoordinateGrid;

        // The independent oracle: every Tycho-2 star, placed in the one cell box whose float bounds
        // hold it (the same bounds the scan tests, so a star on a boundary lands the same way).
        var stars = new Tycho2StarLite[db.Tycho2StarCount];
        db.CopyTycho2Stars(stars).ShouldBe(stars.Length);
        var expectedPerCell = new int[360, 181];
        var unplaced = 0;
        foreach (var star in stars)
        {
            if (TryBucket(star, out var raIdx, out var decIdx))
            {
                expectedPerCell[raIdx, decIdx]++;
            }
            else
            {
                unplaced++;
            }
        }

        var indexerBuffer = new List<CatalogIndex>();
        var structBuffer = new List<CatalogIndex>();
        var seenInCell = new HashSet<CatalogIndex>();
        var mismatches = new List<string>();
        var duplicates = new List<CatalogIndex>();
        var unreadable = new List<CatalogIndex>();
        var tychoTotal = 0;
        var directTotal = 0;
        for (var raIdx = 0; raIdx < 360; raIdx++)
        {
            var ra = (raIdx + 0.5) / 15.0;
            for (var decIdx = 0; decIdx <= 180; decIdx++)
            {
                var dec = Math.Min(-90.0 + decIdx + 0.5, 90.0);

                indexerBuffer.Clear();
                foreach (var idx in grid[ra, dec])
                {
                    indexerBuffer.Add(idx);
                }

                structBuffer.Clear();
                var cell = grid.EnumerateCell(ra, dec);
                foreach (var idx in cell)
                {
                    structBuffer.Add(idx);
                }

                if (!structBuffer.SequenceEqual(indexerBuffer))
                {
                    Record(mismatches, raIdx, decIdx,
                        $"struct yields {structBuffer.Count}, indexer {indexerBuffer.Count}, or in another order");
                    continue;
                }

                // The deep-sky half is the primary grid's cell, in front, verbatim.
                var direct = cell.DirectEntries;
                if (!direct.SequenceEqual(deepSky[ra, dec].ToArray()))
                {
                    Record(mismatches, raIdx, decIdx, "the deep-sky half is not the deep-sky grid's cell");
                    continue;
                }

                directTotal += direct.Length;

                // The Tycho-2 half: as many stars as the catalogue puts in this box, each one of them
                // inside the box, none twice.
                seenInCell.Clear();
                var tychoInCell = 0;
                for (var k = direct.Length; k < structBuffer.Count; k++)
                {
                    var idx = structBuffer[k];
                    tychoInCell++;
                    if (!seenInCell.Add(idx))
                    {
                        duplicates.Add(idx);
                    }
                    else if (!db.TryGetTycho2Star(idx, out var lite))
                    {
                        unreadable.Add(idx);
                    }
                    else if (!TryBucket(lite, out var starRaIdx, out var starDecIdx) || starRaIdx != raIdx || starDecIdx != decIdx)
                    {
                        Record(mismatches, raIdx, decIdx,
                            $"{idx.ToCanonical()} at RA {lite.RaHours} Dec {lite.DecDeg} is outside the cell box");
                    }
                }

                tychoTotal += tychoInCell;
                if (tychoInCell != expectedPerCell[raIdx, decIdx])
                {
                    Record(mismatches, raIdx, decIdx,
                        $"the scan yields {tychoInCell} Tycho-2 stars, the catalogue puts {expectedPerCell[raIdx, decIdx]} in the box");
                }
            }
        }

        output.WriteLine($"{tychoTotal} Tycho-2 stars over the sky through the struct, {directTotal} deep-sky entries, " +
            $"{unplaced} catalogue stars in no box (RA 24 h or Dec past the last row)");

        output.WriteLine($"{duplicates.Count} identifiers yielded twice in a cell, {unreadable.Count} yielded that the lightweight lookup cannot read");

        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches));
        (tychoTotal + unplaced).ShouldBe(stars.Length, "every entry of the blob is in exactly one cell");

        // The two blob facts, pinned exactly (see the class doc). A re-bake that removes either moves
        // these numbers, and should.
        duplicates.Count.ShouldBe(254, "Supplement 1 identifiers the main catalogue already has, appended by the baker without a check");
        unreadable.Select(i => i.ToCanonical()).ShouldBe(["TYC 1327-606-0"],
            "the one supplement star with component 4, truncated to 0 by the packed index's two-bit field");
    }

    [Fact]
    public async Task ANineCellWalkAllocatesNothing()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        // The Aquila pointing SkyMapHoverResolveBenchmarks uses: about 1,100 Tycho-2 candidates in
        // the nine cells, the dense case the resolve pays for.
        var probes = Probes(19.5, 5.0);
        var starGrid = db.CoordinateGrid;
        var dsoGrid = db.DeepSkyCoordinateGrid;

        // Warm both shapes so neither measurement below includes a first-call cost.
        for (var i = 0; i < 3; i++)
        {
            WalkIndexer(starGrid, probes);
            WalkStruct(starGrid, probes);
            WalkStruct(dsoGrid, probes);
        }

        // The premise: the indexer's walk is what this replaces, so it must be seen to allocate,
        // or a zero below would be a measurement that sees nothing.
        var indexerBytes = MeasureBytes(() => WalkIndexer(starGrid, probes), out var indexerCount);
        output.WriteLine($"indexer walk: {indexerCount} candidates, {indexerBytes} B");
        indexerBytes.ShouldBeGreaterThan(1024, "the indexer builds a list per cell; the premise of this test is that it allocates");

        var structBytes = MeasureBytes(() => WalkStruct(starGrid, probes), out var structCount);
        output.WriteLine($"struct walk:  {structCount} candidates, {structBytes} B");
        structCount.ShouldBe(indexerCount);
        structBytes.ShouldBe(0L);

        var dsoBytes = MeasureBytes(() => WalkStruct(dsoGrid, probes), out var dsoCount);
        output.WriteLine($"deep-sky struct walk: {dsoCount} candidates, {dsoBytes} B");
        dsoBytes.ShouldBe(0L);
    }

    private static long MeasureBytes(Func<int> body, out int count)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        count = body();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static int WalkIndexer(IRaDecIndex grid, (double Ra, double Dec)[] probes)
    {
        var n = 0;
        foreach (var (ra, dec) in probes)
        {
            foreach (var _ in grid[ra, dec])
            {
                n++;
            }
        }

        return n;
    }

    private static int WalkStruct(IRaDecIndex grid, (double Ra, double Dec)[] probes)
    {
        var n = 0;
        foreach (var (ra, dec) in probes)
        {
            foreach (var _ in grid.EnumerateCell(ra, dec))
            {
                n++;
            }
        }

        return n;
    }

    private static (double Ra, double Dec)[] Probes(double raHours, double decDeg)
    {
        const double CellRaHours = 1.0 / 15.0;
        var probes = new (double, double)[9];
        var k = 0;
        for (var di = -1; di <= 1; di++)
        {
            for (var dj = -1; dj <= 1; dj++)
            {
                probes[k++] = ((raHours + di * CellRaHours + 24.0) % 24.0, Math.Clamp(decDeg + dj, -90.0, 90.0));
            }
        }

        return probes;
    }

    /// <summary>
    /// The cell box holding a star, by the scan's own test: RA in <c>[raIdx / 15f, (raIdx + 1) / 15f)</c>,
    /// Dec in <c>[decIdx - 90, decIdx - 89)</c>, both in float. The RA candidate from integer
    /// division is checked against its neighbours because <c>raIdx / 15f</c> is not exact.
    /// </summary>
    private static bool TryBucket(in Tycho2StarLite star, out int raIdx, out int decIdx)
    {
        decIdx = (int)Math.Floor(star.DecDeg + 90.0);
        var cellMinDec = (float)(decIdx - 90);
        if (decIdx < 0 || decIdx > 180 || star.DecDeg < cellMinDec || star.DecDeg >= cellMinDec + 1f)
        {
            raIdx = -1;
            return false;
        }

        var guess = (int)(star.RaHours * 15.0);
        for (var candidate = guess - 1; candidate <= guess + 1; candidate++)
        {
            if (candidate >= 0 && candidate < 360 && star.RaHours >= candidate / 15f && star.RaHours < (candidate + 1) / 15f)
            {
                raIdx = candidate;
                return true;
            }
        }

        raIdx = -1;
        return false;
    }

    private static void Record(List<string> mismatches, int raIdx, int decIdx, string what)
    {
        if (mismatches.Count < 20)
        {
            mismatches.Add($"cell ra {raIdx} dec {decIdx - 90}: {what}");
        }
    }
}
