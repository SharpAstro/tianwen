using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Imaging.Degradation;

namespace TianWen.AI.Imaging;

/// <summary>
/// Which cells hold bright structure (docs/plans/denoiser-training.md, "E16b, pre-registered"): the ONE rule, read
/// by the training export's bright cells and the eval caches' alike, so the two cannot disagree on what bright is.
/// </summary>
public static class DatasetBrightCells
{
    /// <summary>The bright level, in stretched units: the scorer's 0.45 edge, above which E16a's model left a level
    /// exactly as it came.</summary>
    public const double Level = 0.45;

    /// <summary>A pixel at or above this level counts for nothing: a saturated core, whose noise the clip removed,
    /// must not qualify a cell.</summary>
    public const double Ceiling = 0.95;

    /// <summary>The share of a cell's pixels (inside the scorer's rim) that must be bright.</summary>
    public const double MinFraction = 0.10;

    /// <summary>
    /// The share of a stretched CHW tile's pixels, inside <see cref="HalfPairNoise.RimPx"/>, whose level
    /// (<see cref="HalfPairNoise.Level"/>: the channels' mean low-passed as the scorer bins it) lies in
    /// [<see cref="Level"/>, <see cref="Ceiling"/>).
    /// </summary>
    public static double BrightFraction(ReadOnlySpan<float> chw, int channels, int size)
    {
        var level = HalfPairNoise.Level(chw, channels, size);
        var rim = HalfPairNoise.RimPx;
        var inside = 0;
        var bright = 0;
        for (var y = rim; y < size - rim; y++)
        {
            for (var x = rim; x < size - rim; x++)
            {
                inside++;
                if (level[(y * size) + x] is >= (float)Level and < (float)Ceiling)
                {
                    bright++;
                }
            }
        }
        return inside == 0 ? 0.0 : (double)bright / inside;
    }

    /// <summary>Whether a stretched CHW tile is a bright cell: at least <see cref="MinFraction"/> of it bright.</summary>
    public static bool IsBright(ReadOnlySpan<float> chw, int channels, int size)
        => BrightFraction(chw, channels, size) >= MinFraction;
}

/// <summary>
/// A list of cells, one per line as <c>x TAB y TAB session id</c> (a session id holds '|' and spaces, never a tab),
/// with '#' comment lines: what <c>tianwen dataset bright-cells</c> writes, the degrade export's
/// <c>--extra-cells</c> reads, and the trainer's prepare reads to add or leave out the same cells.
/// </summary>
public static class DatasetCellList
{
    /// <summary>Reads a list into its cells by session.</summary>
    public static Dictionary<string, HashSet<(int X, int Y)>> Read(string path)
    {
        var bySession = new Dictionary<string, HashSet<(int X, int Y)>>(StringComparer.Ordinal);
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var parts = line.Split('\t');
            if (parts.Length != 3
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                throw new FormatException($"{path}:{number}: expected 'x<TAB>y<TAB>session id', got '{line}'");
            }
            if (!bySession.TryGetValue(parts[2], out var cells))
            {
                bySession[parts[2]] = cells = [];
            }
            cells.Add((x, y));
        }
        return bySession;
    }

    /// <summary>Writes a list, a header of comment lines first.</summary>
    public static void Write(string path, IEnumerable<string> header, IEnumerable<(string SessionId, int X, int Y)> cells)
    {
        var lines = header.Select(static h => "# " + h)
            .Concat(cells.Select(static c => string.Create(CultureInfo.InvariantCulture, $"{c.X}\t{c.Y}\t{c.SessionId}")));
        File.WriteAllLines(path, lines);
    }
}
