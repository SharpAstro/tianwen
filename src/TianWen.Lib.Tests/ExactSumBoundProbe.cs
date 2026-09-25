using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Diagnostic probe, the evidence #490 asked for: over real FITS files, opened as the viewer opens them, how
/// often each statistics walk of a document open proves its running sum exact, so that parallel bands give it
/// (<c>Image.TraverseInBands</c>), and how often it falls back to walk order, and why. Not an assertion, read
/// the output.
/// </summary>
/// <remarks>
/// Run: set <c>TIANWEN_EXACT_SUM_PROBE</c> to folders separated by <c>;</c>, then
/// <c>dotnet test TianWen.Lib.Tests -c Release --filter ExactSumBoundProbe</c>. The verdict depends on the whole
/// walk (its smallest exponent and its total magnitude), never on the number of bands, so the probe bands every
/// frame whatever its size.
/// </remarks>
[Collection("Imaging")]
public class ExactSumBoundProbe(ITestOutputHelper output)
{
    private const string EnvVar = "TIANWEN_EXACT_SUM_PROBE";

    [Fact]
    public async Task HowOftenARealFrameProvesItsSumExact()
    {
        var setting = Environment.GetEnvironmentVariable(EnvVar);
        Assert.SkipWhen(setting is not { Length: > 0 }, $"{EnvVar} not set");
        var ct = TestContext.Current.CancellationToken;

        var byFolder = new SortedDictionary<string, (int Files, int Walks, int Exact, int NonFinite, int Bound, List<string> Examples)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in (setting ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var path in FileEnumeration.EnumerateFiles(root, [".fits", ".fit", ".fts"], recursive: true).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!Image.TryReadFitsFile(path, out var raw))
                {
                    continue;
                }

                AstroImageDocument document;
                try
                {
                    document = await AstroImageDocument.AdoptImageAsync(raw, cancellationToken: ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // A frame the viewer cannot open either: say which and why, and go on.
                    output.WriteLine($"NOT OPENED {path}: {ex.GetType().Name}: {ex.Message} ({raw.ChannelCount} channel(s), {raw.Width} x {raw.Height}, {raw.ImageMeta.SensorType}, min {raw.MinValue}, max {raw.MaxValue})");
                    continue;
                }
                var image = document.UnstretchedImage;
                var folder = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root);
                (int Files, int Walks, int Exact, int NonFinite, int Bound, List<string> Examples) entry =
                    byFolder.TryGetValue(folder, out var existing) ? existing : (0, 0, 0, 0, 0, new List<string>());
                entry.Files++;

                foreach (var (channel, cfa) in Walks(image))
                {
                    var probe = image.Statistics(channel, removePedestral: false, pixelStride: 1, cfa);
                    var scale = probe.RescaledMaxValue ?? 1f;
                    var threshold = (uint)probe.Threshold;
                    var walk = image.TraverseInBands(channel, ignoreBlack: false, pixelStride: 1, cfa, scale, threshold,
                        new uint[threshold], pedestal: 0f, sumFirst: true, new uint[threshold], image.MinValue * scale,
                        minSamplesPerBand: 1 << 16);
                    entry.Walks++;
                    if (walk.SumFromBands)
                    {
                        entry.Exact++;
                        continue;
                    }

                    var (smallest, minExponent, magnitude, nonFinite) = Explain(image, channel, cfa, scale, threshold);
                    if (nonFinite)
                    {
                        entry.NonFinite++;
                    }
                    else
                    {
                        entry.Bound++;
                    }

                    if (entry.Examples.Count < 3)
                    {
                        var over = Math.Log2(magnitude) - (minExponent - 97);
                        entry.Examples.Add(nonFinite
                            ? $"{Path.GetFileName(path)} {Describe(channel, cfa)}: an infinity"
                            : $"{Path.GetFileName(path)} {Describe(channel, cfa)}: smallest |x| {smallest:G3}, total {magnitude:G3}, 2^{over:F1} over the bound");
                    }
                }

                byFolder[folder] = entry;
            }
        }

        var files = 0;
        var walks = 0;
        var exact = 0;
        foreach (var (folder, entry) in byFolder)
        {
            files += entry.Files;
            walks += entry.Walks;
            exact += entry.Exact;
            output.WriteLine($"{folder,-48} files {entry.Files,4}  walks {entry.Walks,5}  from bands {entry.Exact,5}  walk order: bound {entry.Bound,4}, infinity {entry.NonFinite,3}");
            foreach (var example in entry.Examples)
            {
                output.WriteLine($"    {example}");
            }
        }
        output.WriteLine($"TOTAL files {files}, walks {walks}, from bands {exact} ({(walks > 0 ? 100.0 * exact / walks : 0):F1} percent)");
    }

    // What decided a walk the bands could not take: its smallest nonzero |addend|, that addend's exponent, the
    // sum of magnitudes, and whether an infinity was counted. The same addends TraverseInBands sums.
    private static (float Smallest, int MinExponent, double Magnitude, bool NonFinite) Explain(
        Image image, int channel, CfaChannel? cfa, float scale, uint threshold)
    {
        var (_, width, height) = image.Shape;
        var data = image.GetChannelSpan(channel);
        Span<(int Row, int Col)> starts = stackalloc (int Row, int Col)[2];
        var phases = image.CfaPhaseStarts(cfa, starts);
        var step = Image.CfaStep(cfa, 1);
        var smallest = float.MaxValue;
        var minExponent = 256;
        var magnitude = 0.0;
        var nonFinite = false;
        for (var phase = 0; phase < phases; phase++)
        {
            var (rowStart, colStart) = starts[phase];
            for (var h = rowStart; h < height; h += step)
            {
                for (var w = colStart; w < width; w += step)
                {
                    var raw = data[h * width + w];
                    if (float.IsNaN(raw))
                    {
                        continue;
                    }
                    var x = raw * scale - 0f;
                    if (!(x < threshold))
                    {
                        continue;
                    }
                    magnitude += Math.Abs((double)x);
                    if (x == 0f)
                    {
                        continue;
                    }
                    var exponent = (BitConverter.SingleToInt32Bits(x) >>> 23) & 0xFF;
                    if (exponent == 0xFF)
                    {
                        nonFinite = true;
                        continue;
                    }
                    minExponent = Math.Min(minExponent, Math.Max(exponent, 1));
                    smallest = Math.Min(smallest, Math.Abs(x));
                }
            }
        }

        return (smallest, minExponent, magnitude, nonFinite);
    }

    private static IEnumerable<(int Channel, CfaChannel? Cfa)> Walks(Image image)
    {
        if (image.IsCfaMosaic)
        {
            yield return (0, CfaChannel.Red);
            yield return (0, CfaChannel.Green);
            yield return (0, CfaChannel.Blue);
            yield return (0, null);
            yield break;
        }
        for (var c = 0; c < image.ChannelCount; c++)
        {
            yield return (c, null);
        }
    }

    private static string Describe(int channel, CfaChannel? cfa) => cfa is { } colour ? colour.ToString() : $"channel {channel}";
}
