using Shouldly;
using System;
using System.IO;
using System.Linq;
using TianWen.AI.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// E16b's bright-cell rule (<see cref="DatasetBrightCells"/>) and the cell list the export and the trainer share
/// (<see cref="DatasetCellList"/>). A cell is bright when a tenth of it lies at a level of 0.45 or more and below
/// 0.95, read as the scorer reads a level: the channels' mean, low-passed, inside the rim.
/// </summary>
[Collection("Imaging")]
public class DatasetBrightCellsTests
{
    private const int Size = 256;

    /// <summary>A CHW tile at a sky level, with a square of <paramref name="side"/> px at <paramref name="level"/>.</summary>
    private static float[] Tile(double sky, double level, int side)
    {
        var n = Size * Size;
        var chw = new float[3 * n];
        var lo = (Size - side) / 2;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var inside = x >= lo && x < lo + side && y >= lo && y < lo + side;
                    chw[(c * n) + (y * Size) + x] = (float)(inside ? level : sky);
                }
            }
        }
        return chw;
    }

    [Fact]
    public void ANebulaFillingAFifthOfTheCellIsBright()
    {
        // 100 px square of 224 px interior: a fifth.
        var tile = Tile(0.25, 0.6, 100);
        DatasetBrightCells.BrightFraction(tile, 3, Size).ShouldBe(0.2, 0.02);
        DatasetBrightCells.IsBright(tile, 3, Size).ShouldBeTrue();
    }

    [Fact]
    public void ASaturatedCoreQualifiesNothing()
    {
        DatasetBrightCells.IsBright(Tile(0.25, 0.99, 100), 3, Size).ShouldBeFalse("its noise was clipped away; a model cannot learn to clean it");
    }

    [Fact]
    public void AStarsWorthOfBrightIsNotABrightCell()
    {
        DatasetBrightCells.IsBright(Tile(0.25, 0.8, 12), 3, Size).ShouldBeFalse();
        DatasetBrightCells.IsBright(Tile(0.25, 0.25, 0), 3, Size).ShouldBeFalse();
    }

    [Fact]
    public void ACellListRoundTripsSessionIdsWithBarsAndSpaces()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cells-{Guid.NewGuid():N}.txt");
        try
        {
            const string id = "ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm/HIP-85088/2025-05-20|ZWO ASI533MC Pro|HIP 85088|Optolong L-Ultimate 3nm|flip=a";
            DatasetCellList.Write(path, ["a header"], [(id, 1001, 101), (id, 5, 7), ("other|x", 0, 0)]);
            var read = DatasetCellList.Read(path);
            read.Keys.Order(StringComparer.Ordinal).ShouldBe(["ZWO-ASI533MC-Pro/Optolong-L-Ultimate-3nm/HIP-85088/2025-05-20|ZWO ASI533MC Pro|HIP 85088|Optolong L-Ultimate 3nm|flip=a", "other|x"], ignoreOrder: true);
            read[id].ShouldBe([(1001, 101), (5, 7)], ignoreOrder: true);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
