using System;
using System.Collections.Generic;
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
            var filterDisplay = meta.Filter == Filter.Unknown && raw is { Length: > 0 } ? raw : canonical;
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

            // The MEASURED background, never the one the display is currently solved from: this panel
            // reports what is in the frame, and while a display anchor is held those two differ.
            var measured = document.MeasuredPerChannelBackground;
            var bg = c < measured.Length ? measured[c] : measured[0];
            lines.Add($"{pad}bg={bg:F4}");
        }

        if (document.UnstretchedImage.ChannelCount >= 3)
        {
            lines.Add($"Luma bg={document.MeasuredLumaBackground:F4}");
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
                // Name the channel when a view selected one, so the single value is not ambiguous.
                // "Val" stays for a mono image, where there is nothing to disambiguate.
                var label = state.ChannelView switch
                {
                    ChannelView.Red => "R",
                    ChannelView.Green => "G",
                    ChannelView.Blue => "B",
                    ChannelView.Channel0 => "Ch0",
                    ChannelView.Channel1 => "Ch1",
                    ChannelView.Channel2 => "Ch2",
                    _ => "Val"
                };
                lines.Add($"{label}: {v:F4} ({v * 65535.0:F0})");
            }
            else if (info.Values.Length >= 3)
            {
                var r = info.Values[0];
                var g = info.Values[1];
                var b = info.Values[2];
                lines.Add($"R: {r:F4} ({r * 65535.0:F0})");
                lines.Add($"G: {g:F4} ({g * 65535.0:F0})");
                lines.Add($"B: {b:F4} ({b * 65535.0:F0})");
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
