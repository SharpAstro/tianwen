using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The synthetic clusters as training tiles (<see cref="SyntheticClusterExport"/>): what each cell's target and input are, the
/// draw's loss mask, and the split of whole skies by seed.
/// </summary>
public sealed class SyntheticClusterExportTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private const int Size = 512;

    // A two-cell-wide scene: a sloping sky as the noise-free truth, its noisy truth, one star's light, and the composite.
    private static SyntheticClusterRender Scene()
    {
        var rng = new Random(5);
        var (starless, noisy, stars, composite) = (new float[3][], new float[3][], new float[3][], new float[3][]);
        for (var c = 0; c < 3; c++)
        {
            (starless[c], noisy[c], stars[c], composite[c]) = (new float[Size * Size], new float[Size * Size], new float[Size * Size], new float[Size * Size]);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var i = (y * Size) + x;
                    starless[c][i] = 0.1f + (0.0002f * x);
                    noisy[c][i] = starless[c][i] + (float)(0.004 * (rng.NextDouble() - 0.5));
                    stars[c][i] = (float)(0.5 * Math.Exp(-0.5 * (((x - 300.0) * (x - 300.0)) + ((y - 100.0) * (y - 100.0))) / 2.0));
                    composite[c][i] = noisy[c][i] + stars[c][i];
                }
            }
        }
        return new SyntheticClusterRender(stars, starless, composite, new float[3][], [], 0, new double[3], new double[3], new double[3], (0, 0), [],
            noisy, []);
    }

    private static float[] ReadHalves(string path) => [.. File.ReadAllBytes(path).Chunk(2).Select(static b => (float)BitConverter.ToHalf(b))];

    [Fact]
    public void ACellsTargetIsTheNoisyTruthAndItsInputTheComposite()
    {
        var dir = _folders.Create("cluster-export").FullName;
        var scene = Scene();
        // A unit divisor, no minimum and a midtones balance of one half: the stretch is the identity, so a tile holds the scene.
        var calibrations = Enumerable.Repeat(new LinearDegradation.NoiseCalibration(0.0, 0.1, 0.002, 1), 3).ToArray();
        var rows = SyntheticClusterExport.WriteScene(dir, "synthetic-cluster|m|s0001|measured", "cam", scene, Size, 1f, [0f, 0f, 0f], [0.5, 0.5, 0.5], calibrations);

        rows.Length.ShouldBe(2 * 4, "a target and a draw for each of the four cells");
        var target = rows.Single(static r => r is { CellX: 256, CellY: 0, Frame: "master" });
        var input = rows.Single(static r => r is { CellX: 256, CellY: 0, Frame: "deg000" });
        var t = ReadHalves(Path.Combine(dir, target.Tile));
        var x = ReadHalves(Path.Combine(dir, input.Tile));
        const int n = SyntheticClusterExport.TileSize * SyntheticClusterExport.TileSize;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < SyntheticClusterExport.TileSize; y += 37)
            {
                for (var col = 0; col < SyntheticClusterExport.TileSize; col += 41)
                {
                    var i = (y * Size) + 256 + col;
                    t[(c * n) + (y * SyntheticClusterExport.TileSize) + col].ShouldBe((float)(Half)scene.StarlessNoisy[c][i], "the target is the NOISY truth");
                    x[(c * n) + (y * SyntheticClusterExport.TileSize) + col].ShouldBe((float)(Half)scene.Composite[c][i], "the input is the composite");
                }
            }
        }
        File.Exists(Path.Combine(dir, target.SigmaTile ?? "")).ShouldBeTrue("the target has its noise plane");
        File.Exists(Path.Combine(dir, input.SigmaTile ?? "")).ShouldBeTrue("and so has the draw");
    }

    [Fact]
    public void ADrawsLossMaskIsAllOnesAndTheTargetHasNone()
    {
        var dir = _folders.Create("cluster-export").FullName;
        var calibrations = Enumerable.Repeat(new LinearDegradation.NoiseCalibration(0.0, 0.1, 0.002, 1), 3).ToArray();
        var rows = SyntheticClusterExport.WriteScene(dir, "synthetic-cluster|m|s0001|measured", "cam", Scene(), Size, 1f, [0f, 0f, 0f], [0.5, 0.5, 0.5], calibrations);

        foreach (var row in rows)
        {
            var keep = Path.Combine(dir, StarRemovalMasks.KeepPathFor(row.Tile));
            if (row.Frame == "master")
            {
                File.Exists(keep).ShouldBeFalse("a mask is a draw's");
                continue;
            }
            var mask = ReadHalves(keep);
            mask.Length.ShouldBe(SyntheticClusterExport.TileSize * SyntheticClusterExport.TileSize);
            mask.ShouldAllBe(static v => v == 1f, "every pixel counts, the knots and the sky near a star alike");
        }
    }

    [Fact]
    public async Task ASkyIsHeldOutWholeBySeed()
    {
        var dir = _folders.Create("cluster-export").FullName;
        Directory.CreateDirectory(Path.Combine(dir, SyntheticClusterExport.FragmentDir));
        var calibrations = Enumerable.Repeat(new LinearDegradation.NoiseCalibration(0.0, 0.1, 0.002, 1), 3).ToArray();
        foreach (var seed in new[] { 9, 10, 11 })
        {
            var lines = new System.Text.StringBuilder();
            foreach (var optics in new[] { ClusterOptics.Measured, ClusterOptics.Lateral })
            {
                var session = SyntheticClusterExport.SessionFor("m", seed, optics);
                foreach (var row in SyntheticClusterExport.WriteScene(dir, session, "cam", Scene(), Size, 1f, [0f, 0f, 0f], [0.5, 0.5, 0.5], calibrations))
                {
                    lines.Append(JsonSerializer.Serialize(row, DatasetManifestJsonContext.Default.TileManifestRow)).Append('\n');
                }
            }
            var fragment = Path.Combine(dir, SyntheticClusterExport.FragmentDir, $"s{seed:D4}.jsonl");
            await File.WriteAllTextAsync(fragment, lines.ToString(), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.ChangeExtension(fragment, ".tsv"), $"{seed}\n", TestContext.Current.CancellationToken);
        }

        var (train, val) = await SyntheticClusterExport.RebuildAsync(dir, TestContext.Current.CancellationToken);

        SyntheticClusterExport.HeldOut(10).ShouldBeTrue();
        SyntheticClusterExport.HeldOut(9).ShouldBeFalse();
        var valList = await File.ReadAllLinesAsync(Path.Combine(dir, SyntheticClusterExport.ValListFileName), TestContext.Current.CancellationToken);
        var trainList = await File.ReadAllLinesAsync(Path.Combine(dir, SyntheticClusterExport.TrainListFileName), TestContext.Current.CancellationToken);
        valList.ShouldBe([SyntheticClusterExport.SessionFor("m", 10, ClusterOptics.Lateral), SyntheticClusterExport.SessionFor("m", 10, ClusterOptics.Measured)],
            "the held-out sky, in every optics");
        trainList.Length.ShouldBe(4);
        trainList.ShouldNotContain(static s => s.Contains("|s0010|"), "no tile of a held-out sky trains");
        (train, val).ShouldBe((2 * 2 * 4, 2 * 4), "input tiles: two training skies and one held out, two optics, four cells");
        var manifest = await File.ReadAllLinesAsync(Path.Combine(dir, DatasetTileExporter.ManifestFileName), TestContext.Current.CancellationToken);
        manifest.Length.ShouldBe(3 * 2 * 8);
    }

    [Fact]
    public void ASkysDrawIsItsSeedsAlone()
    {
        var a = SyntheticClusterExport.Sky.Draw(42, 2.0 / 3.0);
        a.ShouldBe(SyntheticClusterExport.Sky.Draw(42, 2.0 / 3.0));
        a.TidalRadiusPx.ShouldBeGreaterThanOrEqualTo(3 * a.CoreRadiusPx);
        var nebulae = Enumerable.Range(1, 3000).Count(static s => SyntheticClusterExport.Sky.Draw(s, 2.0 / 3.0).Nebula);
        (nebulae / 3000.0).ShouldBe(2.0 / 3.0, 0.03, "about two skies in three carry a nebula");
    }
}
