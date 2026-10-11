using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Sessions left out of the dataset altogether through <c>dataset-parameters.json</c>'s <c>excludedSessions</c>:
/// the file reads them with their reasons, and a resumed bake drops their rows from the tile manifest (which is
/// what training reads) while leaving their tiles on disk. The grouping half is in
/// <see cref="DatasetSessionDiscoveryTests"/>.
/// </summary>
public sealed class DatasetExcludedSessionsTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static DatasetTileExporter.TileManifestRow Row(string sessionId, string tile) => new(
        Tile: tile, SessionId: sessionId, Camera: "Cam", Frame: "master",
        SourceFile: "", CellX: 0, CellY: 0, TileSize: 64, Channels: 3, Gain: 100,
        ExposureSeconds: 1, NoiseMad: 0.1);

    [Fact]
    public async Task TheParametersFileReadsExcludedSessionsWithTheirReasons()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("excluded-params").FullName, DatasetParameters.FileName);
        await File.WriteAllTextAsync(path, """
            {
              "heldOutSessions": [ { "session": "a|Cam|A", "reason": "scored, never trained" } ],
              "excludedSessions": [ { "session": "b|Cam|B|Luminance", "reason": "no dark or flat anywhere" } ]
            }
            """, ct);

        var parameters = await DatasetParameters.ReadAsync(path, ct);

        parameters.HeldOutSessionIds.ShouldBe(["a|Cam|A"]);
        parameters.ExcludedSessionIds.ShouldBe(["b|Cam|B|Luminance"]);
        parameters.ExcludedSessions.ShouldHaveSingleItem().Reason.ShouldBe("no dark or flat anywhere");
    }

    [Fact]
    public async Task AFileWithoutExcludedSessionsExcludesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_folders.Create("excluded-none").FullName, DatasetParameters.FileName);
        await File.WriteAllTextAsync(path, """{ "heldOutSessions": [] }""", ct);

        (await DatasetParameters.ReadAsync(path, ct)).ExcludedSessionIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task AResumeDropsAnExcludedNightsRowsWithItsFlipSidesAndLeavesItsTiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = _folders.Create("excluded-manifest").FullName;
        var manifest = Path.Combine(dir, DatasetTileExporter.ManifestFileName);
        var tile = Path.Combine(dir, "tiles", "night", "x0_y0_master.f16");
        Directory.CreateDirectory(Path.GetDirectoryName(tile) ?? dir);
        await File.WriteAllTextAsync(tile, "tile", ct);
        await DatasetTileExporter.AppendManifestAsync(manifest,
        [
            Row("night|Cam|Vela", "tiles/night/x0_y0_master.f16"),
            Row("night|Cam|Vela|flip=a", "tiles/night-a/x0_y0_master.f16"),
            Row("other|Cam|Rim", "tiles/other/x0_y0_master.f16"),
        ], ct);
        var checkpoints = await DatasetTileExporter.ReadManifestCheckpointsAsync(manifest, ct);

        var dropped = await DatasetBuildRunner.RemoveExcludedRowsAsync(
            manifest, checkpoints, ["night|Cam|Vela"], progress: null, ct);

        dropped.ShouldBe(["night|Cam|Vela", "night|Cam|Vela|flip=a"]);
        checkpoints.Keys.ShouldBe(["other|Cam|Rim"]);
        (await DatasetTileExporter.ReadManifestCheckpointsAsync(manifest, ct)).Keys.ShouldBe(["other|Cam|Rim"]);
        File.Exists(tile).ShouldBeTrue();
    }

    [Fact]
    public async Task NoExcludedSessionsLeavesTheManifestAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = _folders.Create("excluded-untouched").FullName;
        var manifest = Path.Combine(dir, DatasetTileExporter.ManifestFileName);
        await DatasetTileExporter.AppendManifestAsync(manifest, [Row("night|Cam|Vela", "tiles/night/x0_y0_master.f16")], ct);
        var before = await File.ReadAllTextAsync(manifest, ct);
        var checkpoints = await DatasetTileExporter.ReadManifestCheckpointsAsync(manifest, ct);

        (await DatasetBuildRunner.RemoveExcludedRowsAsync(manifest, checkpoints, ImmutableArray<string>.Empty, progress: null, ct))
            .ShouldBeEmpty();

        (await File.ReadAllTextAsync(manifest, ct)).ShouldBe(before);
        checkpoints.Count.ShouldBe(1);
    }
}
