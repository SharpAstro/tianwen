using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>One of AutoStakkert's alignment points: its box's edge and its centre, in the frames' pixels.</summary>
public readonly record struct AutoStakkertAlignmentPoint(int Size, int X, int Y);

/// <summary>
/// An AutoStakkert! session file (<c>.as3</c>, "machine- and human-readable"), which AutoStakkert 3 writes beside every stack:
/// its settings, its alignment points and, per frame of the capture in capture order, where it placed the planet (its
/// "Planet Stabilization" track). Read to set our registration beside AutoStakkert's on the same frames
/// (docs/plans/planetary-restoration.md, R5 part 3), never to reproduce its stack.
/// </summary>
/// <param name="Version">AutoStakkert's version (<c>_as_version</c>), empty when the file does not say.</param>
/// <param name="FrameCount">The capture's frame count as AutoStakkert read it (<c>_frames_count</c>), -1 when absent.</param>
/// <param name="Settings">Every setting, by its name as the file spells it (<c>_quality_type</c>, <c>_reference_num_frames</c>).</param>
/// <param name="AlignmentPoints">The alignment points, in the file's order.</param>
/// <param name="Track">Each frame's planet position, x and y in the frame's pixels, in capture order.</param>
public sealed record AutoStakkertSession(
    string Version,
    int FrameCount,
    ImmutableDictionary<string, string> Settings,
    ImmutableArray<AutoStakkertAlignmentPoint> AlignmentPoints,
    ImmutableArray<(double X, double Y)> Track)
{
    /// <summary>A setting's value, or null when the file has none by that name.</summary>
    public string? Setting(string name) => Settings.TryGetValue(name, out var value) ? value : null;

    /// <summary>The session in <paramref name="path"/>, or null when it is not an AutoStakkert session file.</summary>
    public static AutoStakkertSession? TryRead(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    /// <summary>
    /// The session <paramref name="reader"/> holds, or null when its first line is not AutoStakkert's
    /// <c>&lt;AutoStakkert&gt;</c>. A section starts on a line at the margin (<c>Settings</c>, <c>Alignment Points</c>,
    /// <c>Planet Stabilization</c>) and its entries are the indented lines under it; a line this reader does not know is
    /// passed over, so a later version's extra sections do no harm.
    /// </summary>
    public static AutoStakkertSession? Parse(TextReader reader)
    {
        if (reader.ReadLine()?.Trim() != "<AutoStakkert>")
        {
            return null;
        }

        var settings = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var points = ImmutableArray.CreateBuilder<AutoStakkertAlignmentPoint>();
        var track = ImmutableArray.CreateBuilder<(double X, double Y)>();
        var section = "";
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            if (!char.IsWhiteSpace(line[0]))
            {
                section = line.Trim();
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 0)
            {
                continue;
            }
            switch (section)
            {
                case "Settings":
                    // A setting whose value is empty (an output prefix left blank) is a name alone.
                    settings[fields[0]] = fields.Length > 1 ? string.Join(' ', fields, 1, fields.Length - 1) : "";
                    break;
                case "Alignment Points" when fields.Length == 4 && fields[0] == "ap"
                    && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                    && int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    && int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y):
                    points.Add(new AutoStakkertAlignmentPoint(size, x, y));
                    break;
                case "Planet Stabilization" when fields.Length == 3 && fields[0] == "f"
                    && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                    && double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var py):
                    track.Add((px, py));
                    break;
            }
        }

        var frameCount = settings.TryGetValue("_frames_count", out var count)
            && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        return new AutoStakkertSession(settings.TryGetValue("_as_version", out var version) ? version : "", frameCount,
            settings.ToImmutable(), points.ToImmutable(), track.ToImmutable());
    }
}
