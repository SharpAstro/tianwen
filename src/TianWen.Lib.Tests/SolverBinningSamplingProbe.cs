using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// What does the shipped pre-detection binning gate actually do to SAMPLING?
    /// </summary>
    /// <remarks>
    /// <para><c>CatalogPlateSolver</c> bins to a ~1.5"/px target whenever the declared plate scale is
    /// finer than that, on the stated grounds that 1.5"/px is "still well above seeing". That is a
    /// claim about a quantity the gate never looks at: binned <c>FWHM_px = FWHM_arcsec / 1.5</c>, so
    /// the gate lands under 2 px -- below critical sampling -- for any seeing better than 3", which is
    /// most usable nights rather than a corner case.</para>
    /// <para>This reports the measurement the gate is missing: median star FWHM at bin 1 against the
    /// factor the gate would pick, over every committed real frame. Gated on
    /// <c>TIANWEN_BINNING_PROBE</c> so it stays off the suite.</para>
    /// </remarks>
    [Collection("Imaging")]
    public class SolverBinningSamplingProbe(ITestOutputHelper output)
    {
        [Fact(Timeout = 900_000)]
        public async Task ReportSamplingAgainstTheShippedBinningGate()
        {
            Assert.SkipWhen(
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIANWEN_BINNING_PROBE")),
                "Set TIANWEN_BINNING_PROBE=1");

            var ct = TestContext.Current.CancellationToken;
            string[] fixtures =
            [
                "2026-01-18_23-26-51__-5.00_60.00s_0002",
                "2026-02-15_00-56-23__-5.00_60.00s_0058",
                "PlateSolveTestFile",
                "RGGB_frame_bx0_by0_top_down",
                "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop",
                "Vela_SNR_Panel_8_1-Multi-NB-mono-Hydrogen-alpha-Oxygen_III-crop",
                "PHD2SimGuider",
                "image_file-snr-20_stars-28_1280x960x16",
            ];

            output.WriteLine($"{"frame",-58} {"size",-11} {"\"/px",7} {"gate",4}  {"bin",3} {"stars",6} {"FWHM px",8} {"FWHM \"",8}");

            foreach (var name in fixtures)
            {
                Image image;
                try
                {
                    image = await SharedTestData.ExtractGZippedFitsImageAsync(name, isReadOnly: false, cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"{name,-58} SKIPPED: {ex.GetType().Name}");
                    continue;
                }

                var dim = image.GetImageDim();
                var scale = dim?.PixelScale ?? double.NaN;

                // The shipped gate, reproduced exactly.
                var gateFactor = 1;
                var pixelScaleX10 = (int)Math.Round(scale * 10);
                if (pixelScaleX10 > 0 && pixelScaleX10 < 15)
                {
                    gateFactor = (15 + pixelScaleX10 - 1) / pixelScaleX10;
                }

                foreach (var factor in new[] { 1, 2, 3, gateFactor }.Distinct().Order())
                {
                    var detectionImage = factor > 1 ? image.Downsample(factor) : image;
                    var stars = await detectionImage.FindStarsAsync(
                        detectionImage.ReferenceStarChannel, snrMin: 5f, maxStars: 500, minStars: 50,
                        maxRetries: 0, maxFirstPassNoiseSigma: Image.MaxFirstPassNoiseSigma,
                        cancellationToken: ct);

                    var widths = stars.Where(s => s.StarFWHM > 0f).Select(s => s.StarFWHM).Order().ToArray();
                    var medianPx = widths.Length > 0 ? widths[widths.Length / 2] : float.NaN;
                    var arcsec = medianPx * scale * factor;
                    var mark = factor == gateFactor ? " <- the gate picks this" : "";
                    output.WriteLine(
                        $"{(factor == 1 ? name : ""),-58} {(factor == 1 ? $"{image.Width}x{image.Height}" : ""),-11} "
                        + $"{(factor == 1 ? $"{scale,7:F3}" : new string(' ', 7))} {(factor == 1 ? $"{gateFactor,4}" : new string(' ', 4))}  "
                        + $"{factor,3} {stars.Count,6} {medianPx,8:F2} {arcsec,8:F2}{mark}");
                }

                image.Release();
            }
        }
    }
}
