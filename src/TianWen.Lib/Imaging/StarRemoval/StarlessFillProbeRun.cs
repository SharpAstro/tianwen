using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// <c>tianwen dataset fill-probe</c>: R2e's S4 read (docs/plans/star-remover-training.md, "S4, pre-registered") over a
/// store's starless plates. Per plate, R0's fill probe (<see cref="StarlessFillProbe"/>) twice on the same holes: today's
/// fill and the conditional draw of the steered texture (<see cref="TexturedHoleFill"/>). A hole never comes near what R0
/// subtracted or filled (the plate's own masks beside it) nor its ring.
/// </summary>
public static class StarlessFillProbeRun
{
    /// <summary>The file whose presence stops a run before the next plate.</summary>
    public const string StopFileName = "fill-probe.stop";

    private const string PlateSuffix = "_plate.fits";

    /// <summary>One plate's row: its PSF width and both fills' bands.</summary>
    public sealed record Row(string Stem, double Fwhm, ImmutableArray<FillProbeBand> Today, ImmutableArray<FillProbeBand> Textured);

    /// <summary>What a run did.</summary>
    public sealed record Result(int Measured, int Skipped, int Failed, bool Stopped, string OutPath);

    /// <summary>
    /// Probes the plates of <paramref name="platesDir"/> whose stems are in <paramref name="stems"/> (every plate when
    /// empty) and not yet in <paramref name="outPath"/>, <paramref name="holes"/> holes a radius, the textured fill under
    /// <paramref name="steering"/>.
    /// </summary>
    public static async Task<Result> RunAsync(string platesDir, string outPath, IReadOnlyCollection<string> stems, int holes,
        SyntheticBackground.Steering steering, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(outPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(outPath, cancellationToken))
            {
                if (line.Length > 0 && JsonSerializer.Deserialize(line, StarlessFillProbeJsonContext.Default.Row) is { } row)
                {
                    done.Add(row.Stem);
                }
            }
        }
        var wanted = new HashSet<string>(stems, StringComparer.Ordinal);
        var stopFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", StopFileName);
        var plates = FileEnumeration.EnumerateFiles(platesDir, PlateSuffix, recursive: false)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int measured = 0, skipped = 0, failed = 0;
        foreach (var path in plates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stem = Path.GetFileName(path)[..^PlateSuffix.Length];
            if ((wanted.Count > 0 && !wanted.Contains(stem)) || done.Contains(stem))
            {
                skipped++;
                continue;
            }
            if (File.Exists(stopFile))
            {
                File.Delete(stopFile);
                progress?.Report($"[fill-probe] {StopFileName} found: stopping before {stem}");
                return new Result(measured, skipped, failed, Stopped: true, outPath);
            }
            var profile = await StarlessFieldProfile.ReadAsync(Path.Combine(platesDir, stem + "_plate.profile.json"), cancellationToken);
            if (profile is null || !Image.TryReadFitsFile(path, out var image)
                || ReadMask(Path.Combine(platesDir, stem + "_plate.subtracted.fits.gz")) is not { } subtracted
                || ReadMask(Path.Combine(platesDir, stem + "_plate.inpainted.fits.gz")) is not { } inpainted)
            {
                progress?.Report($"[fill-probe] {stem}: the plate, its masks or its profile are missing or unreadable");
                failed++;
                continue;
            }
            var (_, width, height) = image.Shape;
            var absent = image.AbsentPixels();
            var excluded = new BitMatrix(height, width);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    excluded[y, x] = subtracted[y, x] || inpainted[y, x] || (absent is { } a && a[y, x]);
                }
            }
            var fwhm = profile.Luminance.Fwhm;
            var today = StarlessFillProbe.Measure(image, excluded, fwhm, StarlessFillProbe.DefaultRadii, holes, seed: 1, textured: null, cancellationToken);
            var textured = StarlessFillProbe.Measure(image, excluded, fwhm, StarlessFillProbe.DefaultRadii, holes, seed: 1, steering, cancellationToken);
            var row = new Row(stem, fwhm, today, textured);
            await File.AppendAllTextAsync(outPath, JsonSerializer.Serialize(row, StarlessFillProbeJsonContext.Default.Row) + "\n", cancellationToken);
            progress?.Report($"[fill-probe] {stem}: " + string.Join("; ", today.Zip(textured).Select(static p =>
                FormattableString.Invariant($"r{p.First.Radius} structured {p.First.StructuredHoles}: error {p.First.StructureErrorStructured:F2} -> {p.Second.StructureErrorStructured:F2}, energy {p.First.EnergyStructured:F2} -> {p.Second.EnergyStructured:F2}, along {p.First.AlignmentStructured:F2} -> {p.Second.AlignmentStructured:F2}"))));
            measured++;
        }
        return new Result(measured, skipped, failed, Stopped: false, outPath);
    }

    // A stored mask (SourceDetectionWriter.MaskToImage): set where the map's first plane is over a half.
    private static BitMatrix? ReadMask(string path)
    {
        if (!File.Exists(path) || !Image.TryReadFitsFile(path, out var map))
        {
            return null;
        }
        var (_, width, height) = map.Shape;
        var plane = map.GetChannelSpan(0);
        var mask = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                mask[y, x] = plane[(y * width) + x] > 0.5f;
            }
        }
        return mask;
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(StarlessFillProbeRun.Row))]
internal sealed partial class StarlessFillProbeJsonContext : JsonSerializerContext
{
}
