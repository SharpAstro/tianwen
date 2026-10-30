using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Reads what each denoiser leaves on one starless plate at every a trous scale (<see cref="MultiScaleNoise"/>, 1 to 32 px, on
/// the same sky pixels), so N2N's residual is judged beside NoiseXTerminator's by its mottle as well as its single-pixel noise
/// (denoiser-training.md, "Residual noise by scale"). The plates come from the CLI: <c>s0_starless.fits</c> from
/// <c>image remove-stars</c> and <c>nxt1</c> to <c>nxt3</c> from <c>image denoise</c> fed its own output; N2N is run here, once
/// and on its own output, since the CLI's denoise takes NoiseXTerminator wherever it is licensed.
/// </summary>
[Collection("Imaging")]
public class PlateNoiseByScaleProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PLATE_NOISE_DIR";

    [Fact]
    public async Task ReportEachDenoisersResidualByScale()
    {
        var dir = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), $"{DirVar} not set");
        var starlessPath = Path.Combine(dir!, "s0_starless.fits");
        Assert.SkipUnless(File.Exists(starlessPath), $"no {starlessPath}");

        var ct = TestContext.Current.CancellationToken;
        Assert.SkipUnless(Image.TryReadFitsFile(starlessPath, out var starless) && starless is not null, "could not read the starless plate");
        var reference = MultiScaleNoise.Measure(starless);
        Assert.SkipWhen(reference is null, "the starless plate has too little sky to read");

        var rows = new List<(string Name, MultiScaleNoise Noise)> { ("starless (input)", reference) };
        foreach (var name in new[] { "nxt1", "nxt2", "nxt3" })
        {
            var path = Path.Combine(dir!, name + ".fits");
            if (File.Exists(path) && Image.TryReadFitsFile(path, out var plate) && plate is not null
                && MultiScaleNoise.Measure(plate, onSkyOf: reference) is { } noise)
            {
                rows.Add(("NXT x" + name[^1], noise));
            }
        }

        // N2N reads [0, 1]; the ratios below compare each plate with its own input, so the scale cancels.
        var unit = starless.HasUnitScalePeak ? starless : starless.ScaleFloatValuesToUnitInPlace();
        var unitReference = MultiScaleNoise.Measure(unit, onSkyOf: reference);
        if (new ModelResolver().TryResolve(N2nDenoiser.ModelFileName, out _) && unitReference is not null)
        {
            using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
            using var n2n = new N2nDenoiser(new ModelResolver(), factory.CreateLogger<N2nDenoiser>());
            var current = unit;
            for (var pass = 1; pass <= 2; pass++)
            {
                current = await n2n.EnhanceAsync(current, ct);
                if (MultiScaleNoise.Measure(current, onSkyOf: reference) is { } noise)
                {
                    // Re-expressed on the input's own scale, so the row reads against the starless plate's.
                    var scale = reference.Bands[0] / unitReference.Bands[0];
                    rows.Add(($"N2N x{pass}", noise with { Bands = [.. noise.Bands.Select(b => b * scale)] }));
                }
                current.WriteToFitsFile(Path.Combine(dir!, $"n2n{pass}.fits"));
            }
        }
        else
        {
            output.WriteLine($"{N2nDenoiser.ModelFileName} not resolvable: N2N rows left out");
        }

        output.WriteLine($"sky pixels {reference.SkyPixels}; each band as a share of the starless plate's own");
        output.WriteLine($"{"plate",-18}" + string.Concat(MultiScaleNoise.Scales.Select(s => $"{s + " px",9}")) + $"{"coarse",9}");
        foreach (var (name, noise) in rows)
        {
            output.WriteLine($"{name,-18}"
                + string.Concat(noise.Bands.Select((b, i) => $"{b / reference.Bands[i],9:F3}"))
                + $"{noise.Coarse / reference.Coarse,9:F3}");
        }
        output.WriteLine($"{"starless absolute",-18}" + string.Concat(reference.Bands.Select(b => $"{b,9:E2}")));
    }
}
