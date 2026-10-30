using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Computes the text lines for the information panel from a document and state.
/// </summary>
public static class InfoPanelData
{
    /// <summary>
    /// One line saying which raw frame the live stack's picture was built at and how stale it is, or null before the first master
    /// (<paramref name="builtFrame"/> below 0). Frames are 0-based in, 1-based out, as the transport counter shows them; each part
    /// is dropped when unknown (no capture timestamp, no playhead, no publish instant).
    /// </summary>
    /// <param name="builtFrame">0-based raw frame index the shown master was built at, -1 when none.</param>
    /// <param name="builtTimestamp">The built frame's capture time (a SER timestamp), or null.</param>
    /// <param name="playhead">0-based raw playhead, or -1 when there is none.</param>
    /// <param name="now">The current wall clock, from the caller's time provider.</param>
    /// <param name="publishedAt">When the master was published, or null.</param>
    public static string? LiveStackBuiltLine(int builtFrame, DateTimeOffset? builtTimestamp, int playhead, DateTimeOffset now, DateTimeOffset? publishedAt)
    {
        if (builtFrame < 0)
        {
            return null;
        }

        var line = $"Live stack: built at frame {(builtFrame + 1).ToString(CultureInfo.InvariantCulture)}";
        if (builtTimestamp is { } stamp)
        {
            line += $" ({stamp.UtcDateTime.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture)} UT)";
        }

        if (playhead >= 0)
        {
            var behind = playhead - builtFrame;
            line += behind > 0
                ? $", {behind.ToString(CultureInfo.InvariantCulture)} {(behind == 1 ? "frame" : "frames")} behind the playhead"
                : ", at the playhead";
        }

        if (publishedAt is { } at)
        {
            var seconds = Math.Max(0, (long)(now - at).TotalSeconds);
            line += seconds < 60
                ? $", updated {seconds.ToString(CultureInfo.InvariantCulture)} s ago"
                : $", updated {(seconds / 60).ToString(CultureInfo.InvariantCulture)} min ago";
        }

        return line;
    }

    public static List<string> GetMetadataLines(IPreviewSource source)
    {
        var lines = new List<string>();
        var size = $"Size: {source.Width} x {source.Height} x {source.ChannelCount}ch";

        // A source with nothing to say about its frame still has a size.
        if (source.ImageMeta is not { } meta)
        {
            lines.Add(size);
            return lines;
        }

        if (!string.IsNullOrEmpty(meta.ObjectName))
        {
            lines.Add($"Object: {meta.ObjectName}");
        }

        lines.Add(size);

        if (!string.IsNullOrEmpty(meta.Telescope))
        {
            lines.Add($"Telescope: {meta.Telescope}");
        }
        if (!string.IsNullOrEmpty(meta.Instrument))
        {
            lines.Add($"Camera: {meta.Instrument}");
        }
        if (meta.ExposureDuration > TimeSpan.Zero)
        {
            lines.Add($"Exposure: {meta.ExposureDuration.TotalSeconds:F1}s");
        }
        // Gain / ISO / offset sit next to the exposure because they are the same fact: what the camera
        // was set to. All three are sentinel -1 when absent and are SUPPRESSED rather than printed as
        // -1, which is why each one is its own check instead of a formatted block. ISO is the
        // consumer-camera spelling (EXIF, so a raw import), gain the astro-camera one (FITS GAIN); a
        // file carries one or the other, never both.
        if (meta.Gain >= 0)
        {
            lines.Add($"Gain: {meta.Gain}");
        }
        if (meta.Iso >= 0)
        {
            lines.Add($"ISO: {meta.Iso}");
        }
        if (meta.Offset >= 0)
        {
            lines.Add($"Offset: {meta.Offset}");
        }
        if (meta.FocalLength > 0)
        {
            lines.Add($"Focal: {meta.FocalLength}mm");
        }
        if (meta.Aperture > 0)
        {
            // The focal ratio rides along where the focal length is known too (FOCRATIO's own derivation): it is what the
            // two numbers are usually read for.
            lines.Add(double.IsNaN(meta.DerivedFRatio)
                ? $"Aperture: {meta.Aperture}mm"
                : string.Create(CultureInfo.InvariantCulture, $"Aperture: {meta.Aperture}mm (f/{meta.DerivedFRatio:0.0})"));
        }
        if (meta.PixelSizeX > 0)
        {
            lines.Add($"Pixel: {meta.PixelSizeX:F2}um");
        }
        if (meta.BinX > 0)
        {
            lines.Add($"Bin: {meta.BinX}x{meta.BinY}");
        }
        if (meta.Filter.Name is { Length: > 0 })
        {
            // Prefer the RAW header text whenever the canonical name resolved to Unknown.
            // Filter.FromName is anchored, so every descriptive header string an imaging app writes
            // ("IDAS LPS-D3", "Antlia ALP-T", "Ha 3nm") canonicalises to the single Filter.Unknown --
            // while SPCC matches its throughput curve against that same raw text and uses it happily.
            // So the panel was reporting "Filter: Unknown" over an image whose colour calibration had
            // just been computed THROUGH that filter's transmission curve: the panel disclaiming
            // knowledge the pipeline was demonstrably acting on. Same defect as the WB sliders
            // reading 1.00 over a calibrated frame, and the same fix -- show what is actually in use.
            var raw = meta.Filter.FilterNameForFits;
            var canonical = meta.Filter.ShortName is { Length: > 0 } shortName ? shortName : meta.Filter.Name;
            // IsUnknown, not == Unknown: every file's filter carries its card text, so the equality
            // never held for a real frame and this line showed "Unknown" all along.
            var filterDisplay = meta.Filter.IsUnknown && raw is { Length: > 0 } ? raw : canonical;
            lines.Add($"Filter: {filterDisplay}");
        }
        if (!float.IsNaN(meta.CCDTemperature))
        {
            lines.Add($"Temp: {meta.CCDTemperature:F1}C");
        }
        if (meta.SensorType is not SensorType.Unknown)
        {
            lines.Add($"Sensor: {meta.SensorType}");
        }
        // Light is the default and needs no label; None is the ENUM default, i.e. no IMAGETYP /
        // FRAMETYP card at all (or one that did not map), so printing it renders the literal
        // "Frame: None" -- a sentinel wearing a value's clothes, the same failure as "Gain: -1".
        if (meta.FrameType is not (FrameType.Light or FrameType.None))
        {
            lines.Add($"Frame: {meta.FrameType}");
        }

        return lines;
    }

    /// <summary>
    /// The statistics as a TABLE: one header row, then one row per channel with its mean, median, MAD
    /// and measured background, and a Luma row carrying only its background. Five rows for a colour
    /// frame where there used to be thirteen lines.
    /// </summary>
    /// <remarks>
    /// <para>A table rather than lines because the strip's font is proportional. The old layout
    /// aligned its four lines per channel by padding with spaces, which lines nothing up in anything
    /// but a monospaced face; the renderer draws these cells at column stops instead, numbers right
    /// aligned, so the decimal points fall under one another whatever the face.</para>
    /// <para>The MEASURED background, never the one the display is currently solved from: this panel
    /// reports what is in the frame, and while a display anchor is held those two differ.</para>
    /// </remarks>
    public static (ImmutableArray<string> Header, ImmutableArray<ImmutableArray<string>> Rows)
        GetStatisticsTable(IPreviewSource source)
    {
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        // Keyed on the statistics, not the plane count: a Bayer mosaic carries three (one per photosite
        // colour, taken on the mosaic in place), and they are R, G, B as much as a debayered frame's are.
        var statistics = source.ChannelStatistics;
        var isColour = statistics.Length >= 3;

        for (var c = 0; c < statistics.Length; c++)
        {
            var stats = statistics[c];
            var label = isColour
                ? c switch { 0 => "R", 1 => "G", 2 => "B", _ => $"Ch{c}" }
                : $"Ch{c}";
            var measured = source.MeasuredPerChannelBackground;
            var bg = c < measured.Length ? measured[c] : measured[0];
            rows.Add([label, Compact(stats.Mean), Compact(stats.Median), Compact(stats.MAD), Compact(bg)]);
        }

        if (isColour)
        {
            rows.Add(["Luma", "", "", "", Compact(source.MeasuredLumaBackground)]);
        }

        return (["", "mean", "med", "MAD", "bg"], rows.ToImmutable());
    }

    /// <summary>
    /// A number at the precision its size deserves and no more: four decimals below one (a unit-range
    /// background is 0.0123), two up to ten, one up to a thousand, none beyond -- so a column of ADU
    /// means and a column of unit-range backgrounds are each as short as they can be while still
    /// telling two frames apart.
    /// </summary>
    internal static string Compact(double value)
    {
        if (double.IsNaN(value))
        {
            return "-";
        }
        var magnitude = Math.Abs(value);
        var format = magnitude >= 1000.0 ? "F0" : magnitude >= 10.0 ? "F1" : magnitude >= 1.0 ? "F2" : "F4";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>A statistic the histogram may not have (a null median or MAD) shows as a dash.</summary>
    internal static string Compact(double? value) => value is { } v ? Compact(v) : "-";

    /// <summary>
    /// One sample as the readout prints it: the [0, 1] value the pipeline works in, then the count the source
    /// recorded where it recorded one (<see cref="PixelInfo.FullScaleAdu"/>). It used to multiply every value
    /// by 65535, so an 8-bit capture's 74 read as 19018 and looked like a 16-bit file.
    /// </summary>
    internal static string Sample(float value, float? fullScaleAdu)
        => fullScaleAdu is { } scale ? $"{value:F4} ({value * scale:F0})" : $"{value:F4}";

    /// <summary>
    /// The status line's star measurements, or null where it should say none. HFD, not HFR: the median is of each star's
    /// half-flux DIAMETER, and the line once said HFR over it, twice the radius it named. Only while the frame has no grade:
    /// a graded frame's strip says the same and more under Grade (<see cref="GetGradeReadout"/>), a single frame included,
    /// so a second copy in the status line said it twice.
    /// </summary>
    public static string? StarsStatus(AstroImageDocument? document, ViewerState state, FrameFindings findings)
    {
        var graded = document is { FilePath: { Length: > 0 } path }
            && state.FrameGrades.ContainsKey(System.IO.Path.GetFullPath(path));
        return !graded && findings.Stars is { Count: > 0 } stars
            ? string.Create(CultureInfo.InvariantCulture, $"Stars: {stars.Count}  HFD: {findings.MedianHfd:F2}  FWHM: {findings.MedianFwhm:F2}")
            : null;
    }

    /// <summary>
    /// The info strip's Grade section for the frame on show (<see cref="LightGrading"/>): its score as a share of the best
    /// of its group graded so far, what the score is made of, and which frame is that best. <see cref="GradeReadout.Relative"/>
    /// is null until the frame is graded, when the lines say why it is not. Null for a document with no file, which no
    /// folder lists and nothing grades: a frame handed over in memory.
    /// </summary>
    public static GradeReadout? GetGradeReadout(ViewerState state, AstroImageDocument document)
    {
        if (string.IsNullOrEmpty(document.FilePath))
        {
            return null;
        }

        var grades = state.FrameGrades;
        if (!grades.TryGetValue(System.IO.Path.GetFullPath(document.FilePath), out var grade))
        {
            return new GradeReadout(
                [document.Findings.Stars is null ? "Measuring the stars..." : "Not graded: no stars found"], null, null);
        }

        var relative = LightGrading.Relative(grades, grade);
        var graded = 0;
        string? bestPath = null;
        var bestScore = 0f;
        foreach (var (path, other) in grades)
        {
            if (other.Key != grade.Key)
            {
                continue;
            }
            graded++;
            if (other.Score > bestScore)
            {
                bestScore = other.Score;
                bestPath = path;
            }
        }

        var group = grade.Key.Describe();
        var lines = ImmutableArray.CreateBuilder<string>();
        lines.Add(relative >= 1f
            ? $"Quality: the best of {graded} ({group})"
            : $"Quality: {relative * 100f:0}% of the best of {graded} ({group})");
        var metrics = grade.Metrics;
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"Stars: {metrics.StarCount}, HFD {metrics.MedianHfd:0.00}, FWHM {metrics.MedianFwhm:0.00}, ecc {metrics.MedianEllipticity:0.00}"));
        return new GradeReadout(lines.ToImmutable(), relative,
            relative < 1f && bestPath is not null ? System.IO.Path.GetFileName(bestPath) : null);
    }

    /// <summary>
    /// The readout over the most a pixel of this frame can say: every plane's value, and RA and Dec where the frame is
    /// solved. The strip keeps room for this many lines whether or not the pointer is over the picture, read off the
    /// readout itself so a line added to it is kept room for too.
    /// </summary>
    public static List<string> GetWidestCursorLines(int channelCount, bool solved, ChannelView view)
        => GetCursorLines(new PixelInfo(0, 0, new float[channelCount >= 3 ? 3 : 1], solved ? 0.0 : null, solved ? 0.0 : null), view);

    public static List<string> GetCursorLines(ViewerState state)
        => state.CursorPixelInfo is { } info ? GetCursorLines(info, state.ChannelView) : [];

    private static List<string> GetCursorLines(PixelInfo info, ChannelView view)
    {
        var lines = new List<string>();
        lines.Add($"Pos: ({info.X}, {info.Y})");
        if (info.Values.Length == 1)
        {
            var v = info.Values[0];
            // Name the channel when a view selected one, so the single value is not ambiguous.
            // "Val" stays for a mono image, where there is nothing to disambiguate.
            var label = view switch
            {
                ChannelView.Red => "R",
                ChannelView.Green => "G",
                ChannelView.Blue => "B",
                ChannelView.Channel0 => "Ch0",
                ChannelView.Channel1 => "Ch1",
                ChannelView.Channel2 => "Ch2",
                _ => "Val"
            };
            lines.Add($"{label}: {Sample(v, info.FullScaleAdu)}");
        }
        else if (info.Values.Length >= 3)
        {
            lines.Add($"R: {Sample(info.Values[0], info.FullScaleAdu)}");
            lines.Add($"G: {Sample(info.Values[1], info.FullScaleAdu)}");
            lines.Add($"B: {Sample(info.Values[2], info.FullScaleAdu)}");
        }
        if (info.RA.HasValue && info.Dec.HasValue)
        {
            lines.Add($"RA: {CoordinateUtils.HoursToHMS(info.RA.Value)}");
            lines.Add($"Dec: {CoordinateUtils.DegreesToDMS(info.Dec.Value)}");
        }

        return lines;
    }
}

/// <summary>
/// The Grade section's lines, the frame's grade relative to its group's best once it has one, and the best frame's file name
/// while it is another frame: a name, which the strip trims rather than wraps.
/// </summary>
public readonly record struct GradeReadout(ImmutableArray<string> Lines, float? Relative, string? BestName);
