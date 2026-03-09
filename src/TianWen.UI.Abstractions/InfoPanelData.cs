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
    public static List<string> GetMetadataLines(FitsDocument document)
    {
        var lines = new List<string>();
        var meta = document.RawImage.ImageMeta;
        var img = document.RawImage;

        lines.Add($"Size: {img.Width} x {img.Height} x {img.ChannelCount}ch");
        lines.Add($"BitDepth: {img.BitDepth}");
        lines.Add($"Range: [{img.MinValue:F1}, {img.MaxValue:F1}]");

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
        if (meta.Filter.Name is { Length: > 0 } filterName)
        {
            lines.Add($"Filter: {filterName}");
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

    public static List<string> GetStatisticsLines(FitsDocument document)
    {
        var lines = new List<string>();

        for (var c = 0; c < document.ChannelStatistics.Length; c++)
        {
            var stats = document.ChannelStatistics[c];
            var label = document.RawImage.ChannelCount >= 3
                ? c switch { 0 => "R", 1 => "G", 2 => "B", _ => $"Ch{c}" }
                : $"Ch{c}";

            lines.Add($"{label}: mean={stats.Mean:F1}");
            lines.Add($"{new string(' ', label.Length + 2)}med={stats.Median:F1}");
            lines.Add($"{new string(' ', label.Length + 2)}MAD={stats.MAD:F1}");
        }

        return lines;
    }

    public static List<string> GetWcsLines(FitsDocument document)
    {
        var lines = new List<string>();

        if (document.Wcs is { } wcs)
        {
            lines.Add($"RA: {CoordinateUtils.HoursToHMS(wcs.CenterRA)}");
            lines.Add($"Dec: {CoordinateUtils.DegreesToDMS(wcs.CenterDec)}");
            if (wcs.HasCDMatrix)
            {
                lines.Add($"Scale: {wcs.PixelScaleArcsec:F2}\"/px");
            }
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
