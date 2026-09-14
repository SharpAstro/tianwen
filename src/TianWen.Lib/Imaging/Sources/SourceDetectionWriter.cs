using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Sources;

/// <summary>
/// Puts a detection on disk beside the frame it was taken from: the label map, the star / structure /
/// sky masks, the background and its noise as FITS sidecars, and the segment table as CSV. Each sidecar
/// is named by a suffix on the frame's own path (the <c>.rejection.fits</c> convention) and says what it
/// holds in a <c>MAPKIND</c> card, so a reader never infers a map's meaning from its file name.
/// </summary>
/// <remarks>
/// Every sidecar carries a TianWen <c>SWCREATE</c>, which is what keeps a folder scan from ingesting a
/// mask as a light (<see cref="Stacking.IntegrationFitsWriter.IsTianWenProduct(string?)"/>). The label
/// map is written as 32-bit integers, exact at any count; the masks as 8-bit 0 / 1; the background and
/// noise as floats in the frame's own units.
/// </remarks>
public static class SourceDetectionWriter
{
    /// <summary>The <c>SWCREATE</c> value on every sidecar written here.</summary>
    public const string SoftwareCreator = "TianWen.Imaging.Sources";

    /// <summary>Per-pixel segment label, 0 for sky (<c>MAPKIND</c> value).</summary>
    public const string LabelsMapKind = "LABELS";

    /// <summary>1 where a compact segment plus its margin lies (<c>MAPKIND</c> value).</summary>
    public const string StarMaskMapKind = "STARMASK";

    /// <summary>1 where an extended segment lies (<c>MAPKIND</c> value).</summary>
    public const string StructureMaskMapKind = "STRUCTMASK";

    /// <summary>1 where no source or margin lies (<c>MAPKIND</c> value).</summary>
    public const string SkyMaskMapKind = "SKYMASK";

    /// <summary>The mesh background interpolated to the pixel (<c>MAPKIND</c> value).</summary>
    public const string BackgroundMapKind = "BACKGROUND";

    /// <summary>The mesh noise (1.4826 MAD) interpolated to the pixel (<c>MAPKIND</c> value).</summary>
    public const string RmsMapKind = "RMS";

    public const string LabelsSuffix = ".labels.fits";
    public const string StarMaskSuffix = ".starmask.fits";
    public const string StructureMaskSuffix = ".structmask.fits";
    public const string SkyMaskSuffix = ".skymask.fits";
    public const string BackgroundSuffix = ".background.fits";
    public const string RmsSuffix = ".rms.fits";
    public const string TableSuffix = ".sources.csv";

    /// <summary>The CSV header the table is written under, one column per <see cref="Segment"/> field plus the sky position.</summary>
    public const string TableHeader = "label,area,x,y,ra_deg,dec_deg,peak_x,peak_y,peak,peak_snr,flux,x0,y0,x1,y1,elongation,core_fraction,peak_to_mean,peak_count,compact";

    /// <summary>
    /// The sidecar path for <paramref name="framePath"/> and <paramref name="suffix"/>: the frame's
    /// name without its extension, the suffix appended, in <paramref name="directory"/> when given and
    /// the frame's own directory otherwise. A <c>.fits.gz</c> or <c>.fit.fz</c> frame loses both parts.
    /// </summary>
    public static string SidecarPath(string framePath, string suffix, string? directory = null)
    {
        var name = Path.GetFileName(framePath);
        var ext = Path.GetExtension(name);
        if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fz", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^ext.Length];
            ext = Path.GetExtension(name);
        }

        var stem = string.IsNullOrEmpty(ext) ? name : name[..^ext.Length];
        var dir = directory ?? Path.GetDirectoryName(framePath) ?? string.Empty;
        return Path.Combine(dir, stem + suffix);
    }

    /// <summary>The label map as a 32-bit integer image, 0 for sky.</summary>
    public static Image LabelsToImage(SegmentationMap map)
    {
        var plane = new float[map.Height, map.Width];
        var labels = map.Labels;
        var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
        for (var i = 0; i < flat.Length; i++)
        {
            flat[i] = labels[i];
        }

        return Image.FromChannel(plane, BitDepth.Int32, maxValue: map.Segments.Length, minValue: 0f);
    }

    /// <summary>A mask as an 8-bit image, 1 where the bit is set.</summary>
    public static Image MaskToImage(BitMatrix mask)
    {
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (mask[y, x])
                {
                    plane[y, x] = 1f;
                }
            }
        }

        return Image.FromChannel(plane, BitDepth.Int8, maxValue: 1f, minValue: 0f);
    }

    /// <summary>The background interpolated to every pixel, as a float image.</summary>
    public static Image BackgroundToImage(BackgroundMap map)
    {
        var plane = new float[map.Height, map.Width];
        map.FillBackground(MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length));
        return Image.FromChannel(plane);
    }

    /// <summary>The noise interpolated to every pixel, as a float image.</summary>
    public static Image RmsToImage(BackgroundMap map)
    {
        var plane = new float[map.Height, map.Width];
        map.FillRms(MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length));
        return Image.FromChannel(plane);
    }

    /// <summary>
    /// Writes one map with its <c>MAPKIND</c>, <c>IMAGETYP</c> and <c>SWCREATE</c> cards. The WCS is the
    /// frame's, so a mask solves where its frame solves; a reader wanting a bare statistic map passes
    /// none.
    /// </summary>
    public static void WriteMap(string path, Image map, string mapKind, WCS? wcs)
    {
        var extras = new Dictionary<string, (object Value, string Comment)>
        {
            ["SWCREATE"] = (SoftwareCreator, "Software that created this map"),
            ["IMAGETYP"] = ("SOURCEMAP", "Source-detection sidecar, see MAPKIND"),
            ["MAPKIND"] = (mapKind, MapKindComment(mapKind)),
        };
        map.WriteToFitsFile(path, wcs, extras);
    }

    /// <summary>
    /// The six sidecars for a detection, named by <see cref="SidecarPath"/>; returns the paths written
    /// in the order labels, star mask, structure mask, sky mask, background, rms.
    /// </summary>
    public static string[] WriteMaps(string framePath, SegmentationMap segments, BackgroundMap background, WCS? wcs, int maskMarginPx = 3, string? directory = null)
    {
        var paths = new[]
        {
            SidecarPath(framePath, LabelsSuffix, directory),
            SidecarPath(framePath, StarMaskSuffix, directory),
            SidecarPath(framePath, StructureMaskSuffix, directory),
            SidecarPath(framePath, SkyMaskSuffix, directory),
            SidecarPath(framePath, BackgroundSuffix, directory),
            SidecarPath(framePath, RmsSuffix, directory),
        };
        WriteMap(paths[0], LabelsToImage(segments), LabelsMapKind, wcs);
        WriteMap(paths[1], MaskToImage(segments.StarMask(maskMarginPx)), StarMaskMapKind, wcs);
        WriteMap(paths[2], MaskToImage(segments.StructureMask()), StructureMaskMapKind, wcs);
        WriteMap(paths[3], MaskToImage(segments.SkyMask(maskMarginPx)), SkyMaskMapKind, wcs);
        WriteMap(paths[4], BackgroundToImage(background), BackgroundMapKind, wcs);
        WriteMap(paths[5], RmsToImage(background), RmsMapKind, wcs);
        return paths;
    }

    /// <summary>
    /// The segment table as CSV under <see cref="TableHeader"/>: positions are 0-based detected-centroid
    /// pixels, the sky position is from the WCS when there is one (RA in degrees) and empty otherwise,
    /// <c>peak_snr</c> is the peak over the noise map at the peak, and <c>compact</c> is 0 or 1.
    /// </summary>
    public static async Task WriteTableAsync(string path, SegmentationMap segments, BackgroundMap background, WCS? wcs, CancellationToken cancellationToken = default)
    {
        var inv = CultureInfo.InvariantCulture;
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync(TableHeader);
        var sb = new StringBuilder(256);
        foreach (var s in segments.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sky = wcs?.PixelToSky(s.XCentroid, s.YCentroid);
            sb.Clear();
            sb.Append(s.Label).Append(',')
              .Append(s.Area).Append(',')
              .Append(s.XCentroid.ToString("F2", inv)).Append(',')
              .Append(s.YCentroid.ToString("F2", inv)).Append(',')
              .Append(sky is { } p ? (p.RA * 15.0).ToString("G9", inv) : string.Empty).Append(',')
              .Append(sky is { } q ? q.Dec.ToString("G9", inv) : string.Empty).Append(',')
              .Append(s.PeakX).Append(',')
              .Append(s.PeakY).Append(',')
              .Append(s.Peak.ToString("G7", inv)).Append(',')
              .Append((s.Peak / background.RmsAt(s.PeakX, s.PeakY)).ToString("G5", inv)).Append(',')
              .Append(s.Flux.ToString("G7", inv)).Append(',')
              .Append(s.X0).Append(',')
              .Append(s.Y0).Append(',')
              .Append(s.X1).Append(',')
              .Append(s.Y1).Append(',')
              .Append(s.Elongation.ToString("F3", inv)).Append(',')
              .Append(s.CoreFraction.ToString("F3", inv)).Append(',')
              .Append(s.PeakToMean.ToString("F2", inv)).Append(',')
              .Append(s.PeakCount).Append(',')
              .Append(s.IsCompact ? '1' : '0');
            await writer.WriteLineAsync(sb.ToString());
        }
    }

    private static string MapKindComment(string mapKind) => mapKind switch
    {
        LabelsMapKind => "Per-pixel segment label, 0 is sky",
        StarMaskMapKind => "1 on compact segments and their margin",
        StructureMaskMapKind => "1 on extended segments",
        SkyMaskMapKind => "1 where no source or margin lies",
        BackgroundMapKind => "Mesh background at the pixel, frame units",
        RmsMapKind => "Mesh noise (1.4826 MAD) at the pixel, frame units",
        _ => "Source-detection map",
    };
}
