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
/// exactly the sky the eval reads apart. That is <see cref="Rule.PlateSources"/>; <see cref="Rule.DrawnStars"/> is the other
/// way round, for draws on a real plate over bright nebula, where the finder misses most of what the plate kept.
/// </summary>
public static class StarRemovalMasks
{
    /// <summary>The mask's sidecar extension, beside the draw's tile.</summary>
    public const string KeepTileExtension = ".keep.f16";

    /// <summary>The file at an export's root that names the <see cref="Rule"/> its masks were written by.</summary>
    public const string RuleFileName = "plate-masks.rule";

    /// <summary><see cref="Rule.DrawnStars"/>: a drawn star's light counts where it stands over this share of the pixel's noise.</summary>
    public const double DrawnLightSigma = 0.25;

    /// <summary><see cref="Rule.DrawnStars"/>: that light counts only within this many PSF widths of a drawn star's centre.</summary>
    public const double DrawnCoreFwhm = 3.0;

    /// <summary><see cref="Rule.DrawnStars"/>: the sky within this many PSF widths of such light counts with it.</summary>
    public const double DrawnMarginFwhm = 1.0;

    /// <summary>Which pixels of a draw a mask counts in the loss.</summary>
    public enum Rule
    {
        /// <summary>D4: every pixel but the sky near a source the plate itself kept.</summary>
        PlateSources,

        /// <summary>
        /// Only the stars the draw was given: a pixel counts where a drawn star's light stands over
        /// <see cref="DrawnLightSigma"/> of its noise within <see cref="DrawnCoreFwhm"/> PSF widths of the star's centre, and
        /// within <see cref="DrawnMarginFwhm"/> PSF widths of such a pixel (so a hole dug round the star costs as the star
        /// left does). Everything else is 0. For draws on a real plate over bright nebula (R4g,
        /// docs/plans/star-remover-training.md): there the plate still holds faint stars no finder lists, and a target that
        /// counts them teaches the net to keep a faint star on bright background. A far halo is left to the draws on a
        /// synthetic background, whose target is clean everywhere.
        /// </summary>
        DrawnStars,
    }

    /// <summary>A draw's mask path: its tile's <c>.f16</c> replaced by <see cref="KeepTileExtension"/>.</summary>
    public static string KeepPathFor(string tilePath)
        => tilePath.EndsWith(DatasetTileExporter.TileExtension, StringComparison.Ordinal)
            ? tilePath[..^DatasetTileExporter.TileExtension.Length] + KeepTileExtension
            : throw new ArgumentException($"{tilePath} is not a {DatasetTileExporter.TileExtension} tile", nameof(tilePath));

    /// <summary>What a run wrote.</summary>
    /// <param name="Draws">Masks written.</param>
    /// <param name="Skipped">Draws whose mask was already there.</param>
    /// <param name="MaskedFraction">The share of the draws' pixels inside the rim the masks leave out.</param>
    /// <param name="Sources">
    /// The sources the masks were drawn round: the plate's own (<see cref="Rule.PlateSources"/>) or the stars the draws were
    /// given (<see cref="Rule.DrawnStars"/>).
    /// </param>
    public sealed record Result(int Draws, int Skipped, double MaskedFraction, long Sources);

    /// <summary>
    /// Why <paramref name="rule"/> cannot write into <paramref name="exportRoot"/>, or null when it can: a draw that has its
    /// mask is skipped, so an export whose masks another rule wrote would keep them under this rule's name. An export with
    /// masks and no <see cref="RuleFileName"/> was written before the file existed, by <see cref="Rule.PlateSources"/>.
    /// An export with no mask left takes either rule, whatever its record says.
    /// </summary>
    public static async Task<string?> RefusalAsync(string exportRoot, Rule rule, CancellationToken cancellationToken = default)
    {
        var recorded = await RecordedRuleAsync(exportRoot, await ReadRowsAsync(exportRoot, cancellationToken), cancellationToken);
        return recorded is { } other && other != rule
            ? $"{exportRoot} holds masks written by the {other} rule; a draw that has its mask is skipped, so the {rule} rule " +
              $"needs an export of its own, or this one with its *{KeepTileExtension} files deleted"
            : null;
    }

    /// <summary>Writes the mask beside every draw of <paramref name="exportRoot"/>'s injection manifest that has none (D4's rule).</summary>
    public static Task<Result> RunAsync(string exportRoot, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(exportRoot, Rule.PlateSources, progress, cancellationToken);

    /// <summary>Writes <paramref name="rule"/>'s mask beside every draw of <paramref name="exportRoot"/>'s injection manifest that has none.</summary>
    /// <exception cref="InvalidOperationException">The export holds masks another rule wrote (<see cref="RefusalAsync"/>).</exception>
    public static async Task<Result> RunAsync(string exportRoot, Rule rule, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var rows = await ReadRowsAsync(exportRoot, cancellationToken);
        if (await RecordedRuleAsync(exportRoot, rows, cancellationToken) is { } other && other != rule)
        {
            throw new InvalidOperationException($"{exportRoot} holds masks written by the {other} rule, not {rule}");
        }
        await File.WriteAllTextAsync(Path.Combine(exportRoot, RuleFileName), rule.ToString(), cancellationToken);

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
            var (keep, leftOut, inside, found) = rule is Rule.DrawnStars ? DrawnStarsMask(exportRoot, row) : Mask(exportRoot, row);
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

    private static async Task<List<DatasetDegradationExporter.InjectionRow>> ReadRowsAsync(string exportRoot, CancellationToken cancellationToken)
    {
        var rows = new List<DatasetDegradationExporter.InjectionRow>();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(exportRoot, DatasetDegradationExporter.InjectionManifestFileName), cancellationToken))
        {
            if (line.Length > 0 && System.Text.Json.JsonSerializer.Deserialize(line, DatasetDegradationJsonContext.Default.InjectionRow) is { } row)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    // The rule the masks the export HOLDS were written by: none where no draw has its mask (a record left behind by masks
    // since deleted says nothing, and the next run writes it anew), else its record, or D4's for masks from before the
    // record existed.
    private static async Task<Rule?> RecordedRuleAsync(string exportRoot, List<DatasetDegradationExporter.InjectionRow> rows, CancellationToken cancellationToken)
    {
        if (!rows.Any(row => File.Exists(Path.Combine(exportRoot, StarRemovalEval.Native(KeepPathFor(row.Tile))))))
        {
            return null;
        }
        var record = Path.Combine(exportRoot, RuleFileName);
        if (!File.Exists(record))
        {
            return Rule.PlateSources;
        }
        var text = (await File.ReadAllTextAsync(record, cancellationToken)).Trim();
        return Enum.TryParse<Rule>(text, out var rule) ? rule : throw new InvalidDataException($"{record} names no rule: '{text}'");
    }

    // A draw's noise plane as the exporter wrote it, its plate and its input as luminance, and the tile's size.
    private static (float[] Sigma, float[] Plate, float[] Input, int Size) ReadDraw(string exportRoot, DatasetDegradationExporter.InjectionRow row)
    {
        var sigma = StarRemovalEval.ReadPlanes(Path.Combine(exportRoot, StarRemovalEval.Native(DatasetDegradationExporter.SigmaPathFor(row.Tile))), 1, out var size)[0];
        var dir = row.Tile[..row.Tile.LastIndexOf('/')];
        var cleanRel = $"{dir}/x{row.CellX}_y{row.CellY}_{DatasetDegradationExporter.FrameClean}{DatasetTileExporter.TileExtension}";
        var channels = (int)(new FileInfo(Path.Combine(exportRoot, StarRemovalEval.Native(row.Tile))).Length / (2L * size * size));
        var plate = StarRemovalEval.Luminance(StarRemovalEval.ReadPlanes(Path.Combine(exportRoot, StarRemovalEval.Native(cleanRel)), channels, out _));
        var input = StarRemovalEval.Luminance(StarRemovalEval.ReadPlanes(Path.Combine(exportRoot, StarRemovalEval.Native(row.Tile)), channels, out _));
        return (sigma, plate, input, size);
    }

    // One draw's mask: 0 on the plate's sources' sky zone, 1 elsewhere; the zone's pixels inside the rim, the pixels inside
    // the rim, and the sources found.
    private static (float[] Keep, long LeftOut, long Inside, int Sources) Mask(string exportRoot, DatasetDegradationExporter.InjectionRow row)
    {
        var (sigma, plate, input, size) = ReadDraw(exportRoot, row);
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

    // One draw's mask by Rule.DrawnStars: 1 where a drawn star's light stands near its centre and in the margin round
    // that light, 0 elsewhere; the pixels inside the rim left out, the pixels inside the rim, and the stars drawn.
    private static (float[] Keep, long LeftOut, long Inside, int Sources) DrawnStarsMask(string exportRoot, DatasetDegradationExporter.InjectionRow row)
    {
        var (sigma, plate, input, size) = ReadDraw(exportRoot, row);
        var fwhm = StarRemovalEval.PlateFwhm(row.Stars);
        // The noise plane is in the trainer's units; the light is read against it in the tile's.
        var lightOverPlane = (float)(DrawnLightSigma / Lib.Imaging.Degradation.StretchedNoise.PlaneScale);

        // The drawn light that counts: over the noise, and within the core's reach of a star the draw was given.
        var lit = new BitMatrix(size, size);
        var core = DrawnCoreFwhm * fwhm;
        var coreReach = (int)Math.Ceiling(core);
        foreach (var star in row.Stars)
        {
            var cx = (int)Math.Round(star.X);
            var cy = (int)Math.Round(star.Y);
            for (var y = Math.Max(0, cy - coreReach); y <= Math.Min(size - 1, cy + coreReach); y++)
            {
                for (var x = Math.Max(0, cx - coreReach); x <= Math.Min(size - 1, cx + coreReach); x++)
                {
                    var i = (y * size) + x;
                    if (((x - star.X) * (x - star.X)) + ((y - star.Y) * (y - star.Y)) <= core * core
                        && MathF.Abs(input[i] - plate[i]) > lightOverPlane * sigma[i])
                    {
                        lit[y, x] = true;
                    }
                }
            }
        }

        var counted = new BitMatrix(size, size);
        var margin = DrawnMarginFwhm * fwhm;
        var marginReach = (int)Math.Ceiling(margin);
        for (var cy = 0; cy < size; cy++)
        {
            for (var cx = 0; cx < size; cx++)
            {
                if (!lit[cy, cx])
                {
                    continue;
                }
                for (var y = Math.Max(0, cy - marginReach); y <= Math.Min(size - 1, cy + marginReach); y++)
                {
                    for (var x = Math.Max(0, cx - marginReach); x <= Math.Min(size - 1, cx + marginReach); x++)
                    {
                        if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= margin * margin)
                        {
                            counted[y, x] = true;
                        }
                    }
                }
            }
        }

        var rim = AiNafnetInputs.StitchBorderPx;
        var keep = new float[size * size];
        long leftOut = 0, inside = 0;
        for (var i = 0; i < keep.Length; i++)
        {
            var y = i / size;
            var x = i % size;
            keep[i] = counted[y, x] ? 1f : 0f;
            if (x >= rim && y >= rim && x < size - rim && y < size - rim)
            {
                inside++;
                if (!counted[y, x])
                {
                    leftOut++;
                }
            }
        }
        return (keep, leftOut, inside, row.Stars.Length);
    }
}
