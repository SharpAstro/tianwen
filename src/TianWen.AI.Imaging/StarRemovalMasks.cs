using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;

namespace TianWen.AI.Imaging;

/// <summary>
/// R2d's D4 (docs/plans/star-remover-training.md): the loss mask a star remover's trainer leaves the plate's own sources out
/// of. A plate keeps the faint stars its builder could not find (about 29 a 256 px tile on R2's plates), and the target then
/// asks the net to remove an injected faint star and keep one exactly like it. Beside every draw of a Stars-mode export this
/// writes <c>&lt;tile&gt;.keep.f16</c> (<see cref="KeepPathFor"/>): 0 on the sky within two PSF widths of a source the plate
/// kept, 1 everywhere else, by the eval's own rule (<see cref="StarRemovalEval.PlateSourceZone"/>), so the loss leaves out
/// exactly the sky the eval reads apart.
/// </summary>
public static class StarRemovalMasks
{
    /// <summary>The mask's sidecar extension, beside the draw's tile.</summary>
    public const string KeepTileExtension = ".keep.f16";

    /// <summary>A draw's mask path: its tile's <c>.f16</c> replaced by <see cref="KeepTileExtension"/>.</summary>
    public static string KeepPathFor(string tilePath)
        => tilePath.EndsWith(DatasetTileExporter.TileExtension, StringComparison.Ordinal)
            ? tilePath[..^DatasetTileExporter.TileExtension.Length] + KeepTileExtension
            : throw new ArgumentException($"{tilePath} is not a {DatasetTileExporter.TileExtension} tile", nameof(tilePath));

    /// <summary>What a run wrote.</summary>
    /// <param name="Draws">Masks written.</param>
    /// <param name="Skipped">Draws whose mask was already there.</param>
    /// <param name="MaskedFraction">The share of the draws' pixels inside the rim the masks leave out.</param>
    /// <param name="Sources">The plate's own sources the masks were drawn round.</param>
    public sealed record Result(int Draws, int Skipped, double MaskedFraction, long Sources);

    /// <summary>Writes the mask beside every draw of <paramref name="exportRoot"/>'s injection manifest that has none.</summary>
    public static async Task<Result> RunAsync(string exportRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<DatasetDegradationExporter.InjectionRow>();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(exportRoot, DatasetDegradationExporter.InjectionManifestFileName), cancellationToken))
        {
            if (line.Length > 0 && System.Text.Json.JsonSerializer.Deserialize(line, DatasetDegradationJsonContext.Default.InjectionRow) is { } row)
            {
                rows.Add(row);
            }
        }

        var draws = 0;
        var skipped = 0;
        long masked = 0, read = 0, sources = 0;
        var done = 0;
        await Parallel.ForEachAsync(rows, cancellationToken, (row, ct) =>
        {
            var target = Path.Combine(exportRoot, StarRemovalEval.Native(KeepPathFor(row.Tile)));
            if (File.Exists(target))
            {
                Interlocked.Increment(ref skipped);
                return ValueTask.CompletedTask;
            }
            var (keep, leftOut, inside, found) = Mask(exportRoot, row);
            DatasetDegradationExporter.WritePlaneFile(keep, target);
            Interlocked.Increment(ref draws);
            Interlocked.Add(ref masked, leftOut);
            Interlocked.Add(ref read, inside);
            Interlocked.Add(ref sources, found);
            if (Interlocked.Increment(ref done) % 2000 == 0)
            {
                progress?.Report($"[plate-masks] {done}/{rows.Count} draws");
            }
            return ValueTask.CompletedTask;
        });
        return new Result(draws, skipped, read > 0 ? (double)masked / read : double.NaN, sources);
    }

    // One draw's mask: 0 on the plate's sources' sky zone, 1 elsewhere; the zone's pixels inside the rim, the pixels inside
    // the rim, and the sources found.
    private static (float[] Keep, long LeftOut, long Inside, int Sources) Mask(string exportRoot, DatasetDegradationExporter.InjectionRow row)
    {
        var sigma = StarRemovalEval.ReadPlanes(Path.Combine(exportRoot, StarRemovalEval.Native(DatasetDegradationExporter.SigmaPathFor(row.Tile))), 1, out var size)[0];
        var dir = row.Tile[..row.Tile.LastIndexOf('/')];
        var cleanRel = $"{dir}/x{row.CellX}_y{row.CellY}_{DatasetDegradationExporter.FrameClean}{DatasetTileExporter.TileExtension}";
        var channels = (int)(new FileInfo(Path.Combine(exportRoot, StarRemovalEval.Native(row.Tile))).Length / (2L * size * size));
        var plate = StarRemovalEval.Luminance(StarRemovalEval.ReadPlanes(Path.Combine(exportRoot, StarRemovalEval.Native(cleanRel)), channels, out _));
        var input = StarRemovalEval.Luminance(StarRemovalEval.ReadPlanes(Path.Combine(exportRoot, StarRemovalEval.Native(row.Tile)), channels, out _));

        var rim = AiNafnetInputs.StitchBorderPx;
        var absent = new BitMatrix(size, size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                absent[y, x] = x < rim || y < rim || x >= size - rim || y >= size - rim;
            }
        }
        var (found, near) = StarRemovalEval.PlateSourceZone(plate, input, sigma, absent, size, rim, StarRemovalEval.PlateFwhm(row.Stars));
        var keep = new float[size * size];
        long leftOut = 0, inside = 0;
        for (var i = 0; i < keep.Length; i++)
        {
            var y = i / size;
            var x = i % size;
            // Only the sky near a kept source is left out: an injected star's footprint there still counts, so the net
            // is still asked to remove every star it was given.
            var dropped = near[y, x] && input[i] == plate[i];
            keep[i] = dropped ? 0f : 1f;
            if (!absent[y, x])
            {
                inside++;
                if (dropped)
                {
                    leftOut++;
                }
            }
        }
        return (keep, leftOut, inside, found.Count);
    }
}
