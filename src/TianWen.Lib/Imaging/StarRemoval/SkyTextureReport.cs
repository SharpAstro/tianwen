using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// <c>tianwen dataset sky-texture</c>: <see cref="SkyTexture"/> over every plate of a plate builder's output
/// (<c>&lt;stem&gt;_plate.fits</c> with its <c>&lt;stem&gt;_plate.profile.json</c>), a row a plate in a JSONL file. Resumes: a
/// plate already in the file is skipped; <see cref="StopFileName"/> beside the file stops it before the next plate.
/// </summary>
public static class SkyTextureReport
{
    /// <summary>The report's default file name.</summary>
    public const string FileName = "sky-texture.jsonl";

    /// <summary>The file whose presence beside the report stops a run before its next plate.</summary>
    public const string StopFileName = "sky-texture.stop";

    private const string PlateSuffix = "_plate.fits";

    /// <summary>One plate's row.</summary>
    public sealed record Row(string Stem, int Channels, int Width, int Height, SkyTexture.Measurement Texture);

    /// <summary>What a run did.</summary>
    public sealed record Result(int Measured, int Skipped, int Failed, bool Stopped, string OutPath);

    /// <summary>Measures every plate in <paramref name="platesDir"/> not yet in <paramref name="outPath"/>.</summary>
    public static async Task<Result> RunAsync(string platesDir, string outPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(outPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(outPath, cancellationToken))
            {
                if (line.Length > 0 && JsonSerializer.Deserialize(line, SkyTextureJsonContext.Default.Row) is { } row)
                {
                    done.Add(row.Stem);
                }
            }
        }
        var stopFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", StopFileName);
        var plates = FileEnumeration.EnumerateFiles(platesDir, PlateSuffix, recursive: false)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int measured = 0, skipped = 0, failed = 0;
        foreach (var path in plates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var stem = name[..^PlateSuffix.Length];
            if (done.Contains(stem))
            {
                skipped++;
                continue;
            }
            if (File.Exists(stopFile))
            {
                progress?.Report($"[sky-texture] {StopFileName} found; stopping before {stem}");
                return new Result(measured, skipped, failed, Stopped: true, outPath);
            }
            var profile = await StarlessFieldProfile.ReadAsync(StarlessFieldProfile.PathFor(platesDir, stem), cancellationToken);
            if (profile is null || !Image.TryReadFitsFile(path, out var image))
            {
                progress?.Report($"[sky-texture] {stem}: {(profile is null ? "no field profile" : "unreadable plate")}, skipped");
                failed++;
                continue;
            }
            try
            {
                var (channels, width, height) = image.Shape;
                var luminance = new float[width * height];
                for (var c = 0; c < channels; c++)
                {
                    var plane = image.GetChannelSpan(c);
                    for (var i = 0; i < luminance.Length; i++)
                    {
                        luminance[i] += plane[i] / channels;
                    }
                }
                var texture = SkyTexture.Measure(luminance, width, height, image.AbsentPixels(), profile.Luminance.Fwhm);
                var row = new Row(stem, channels, width, height, texture);
                await File.AppendAllTextAsync(outPath, JsonSerializer.Serialize(row, SkyTextureJsonContext.Default.Row) + "\n", cancellationToken);
                measured++;
                progress?.Report(Describe(row));
            }
            finally
            {
                image.Release();
            }
        }
        return new Result(measured, skipped, failed, Stopped: false, outPath);
    }

    /// <summary>One line for a plate: its sources, what was read, the spectral index, each scale's robust RMS over the
    /// finest one's, and the finer scales' kurtosis.</summary>
    public static string Describe(Row row)
    {
        var t = row.Texture;
        var first = t.Scales[0].RobustRms;
        return string.Create(CultureInfo.InvariantCulture,
            $"[sky-texture] {row.Stem}: fwhm {t.Fwhm:F2} px, {t.Sources} sources, read {t.ReadFraction:P0}, beta {t.SpectralIndex:F2}; " +
            $"rms over 1 px: {string.Join(" ", t.Scales.Skip(1).Select(s => (s.RobustRms / first).ToString("F3", CultureInfo.InvariantCulture)))}; " +
            $"tail past {SkyTexture.TailSigma:0} sigma: {string.Join(" ", t.Scales.Take(4).Select(static s => s.TailFraction.ToString("0.0e0", CultureInfo.InvariantCulture)))}; " +
            $"by class: {string.Join(", ", t.Classes.Select(static c => string.Create(CultureInfo.InvariantCulture, $"{c.MinTexture:0.#}+ {c.Pixels} px beta {c.SpectralIndex:F2}")))}");
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SkyTextureReport.Row))]
internal sealed partial class SkyTextureJsonContext : JsonSerializerContext
{
}
