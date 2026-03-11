using System;
using System.Collections.Generic;
using ImageMagick;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Computes the text lines for the information panel from a document and state.
/// </summary>
public static class InfoPanelData
{
    public static List<string> GetMetadataLines(AstroImageDocument document)
    {
        var lines = new List<string>();
        var image = document.UnstretchedImage;
        var meta = image.ImageMeta;

        if (!string.IsNullOrEmpty(meta.ObjectName))
        {
            lines.Add($"Object: {meta.ObjectName}");
        }

        lines.Add($"Size: {image.Width} x {image.Height} x {image.ChannelCount}ch");
        lines.Add($"BitDepth: {image.BitDepth}");
        lines.Add($"Range: [{image.MinValue:F1}, {image.MaxValue:F1}]");

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
        if (meta.FocalLength > 0)
        {
            lines.Add($"Focal: {meta.FocalLength}mm");
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
            var filterDisplay = meta.Filter.ShortName is { Length: > 0 } shortName ? shortName : meta.Filter.Name;
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
        if (meta.FrameType is not FrameType.Light)
        {
            lines.Add($"Frame: {meta.FrameType}");
        }

        return lines;
    }

    public static List<string> GetStatisticsLines(AstroImageDocument document)
    {
        var lines = new List<string>();

        for (var c = 0; c < document.ChannelStatistics.Length; c++)
        {
            var stats = document.ChannelStatistics[c];
            var label = document.UnstretchedImage.ChannelCount >= 3
                ? c switch { 0 => "R", 1 => "G", 2 => "B", _ => $"Ch{c}" }
                : $"Ch{c}";

            var pad = new string(' ', label.Length + 2);
            lines.Add($"{label}: mean={stats.Mean:F1}");
            lines.Add($"{pad}med={stats.Median:F1}");
            lines.Add($"{pad}MAD={stats.MAD:F1}");

            var bg = c < document.PerChannelBackground.Length
                ? document.PerChannelBackground[c]
                : document.PerChannelBackground[0];
            lines.Add($"{pad}bg={bg:F4}");
        }

        if (document.UnstretchedImage.ChannelCount >= 3)
        {
            lines.Add($"Luma bg={document.LumaBackground:F4}");
        }

        return lines;
    }

    public static List<string> GetCursorLines(ViewerState state)
    {
        var lines = new List<string>();

        if (state.CursorPixelInfo is { } info)
        {
            lines.Add($"Pos: ({info.X}, {info.Y})");
            if (info.Values.Length == 1)
            {
                var v = info.Values[0];
                lines.Add($"Val: {v:F4} ({v * Quantum.Max:F0})");
            }
            else if (info.Values.Length >= 3)
            {
                var r = info.Values[0];
                var g = info.Values[1];
                var b = info.Values[2];
                lines.Add($"R: {r:F4} ({r * Quantum.Max:F0})");
                lines.Add($"G: {g:F4} ({g * Quantum.Max:F0})");
                lines.Add($"B: {b:F4} ({b * Quantum.Max:F0})");
            }
            if (info.RA.HasValue && info.Dec.HasValue)
            {
                lines.Add($"RA: {CoordinateUtils.HoursToHMS(info.RA.Value)}");
                lines.Add($"Dec: {CoordinateUtils.DegreesToDMS(info.Dec.Value)}");
            }
        }

        return lines;
    }
}
