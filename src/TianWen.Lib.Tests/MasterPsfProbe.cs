using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Env-gated diagnostic: runs the REAL per-channel PSF estimator over master FITS files named on an
/// env var and prints FWHM / Moffat beta / fit residual per channel, plus the green-to-red and
/// blue-to-red ratios. Not an assertion about anything; it exists so two masters can be compared
/// under identical code. Skips when the var is unset, so a bare <c>dotnet test</c> is unaffected
/// (same shape as <c>DatasetQualityGateTests.DumpBadVsGoodMetricDistributions</c>).
///
/// <para><b>Why this rather than a quick script.</b> The archive's per-channel numbers come from
/// <see cref="PsfProfileFit"/>, which fits a Moffat in LOG space to an annulus-subtracted stacked
/// profile, and nothing else is comparable to them. Two attempts to shortcut it with a flux-weighted
/// second moment over a fixed aperture were both invalid in the same way: sweeping the aperture gave
/// FWHM 3.46 px at r=4 rising linearly to 20.3 px at r=18, because an unsubtracted annulus pedestal
/// makes the outer ring dominate the moment, so the estimator reports the window rather than the
/// star. This is what settled that the archive's "green is 35% narrower than red" is largely an AHD
/// demosaic artifact (G/R 0.767 on an AHD master versus 0.947 on a Bayer-drizzled one, same session,
/// same 236 subs), and the decisive number was the fit residual the moment estimator does not even
/// produce: AHD red log-RMS 0.957 against drizzle's 0.130, i.e. the model never described that
/// profile at all.</para>
///
/// <para>Usage: set <c>TIANWEN_MASTER_PSF_PROBE</c> to a <c>;</c>-separated list of FITS paths, and
/// optionally <c>TIANWEN_MASTER_PSF_OUT</c> to a file to copy the table into.</para>
/// </summary>
public class MasterPsfProbe
{
    // The dataset PSF report's own detection settings, so the numbers are comparable to the store
    // rather than to a differently-tuned detection.
    private const float SnrMin = 5f;
    private const int MaxStars = 3000;

    [Fact]
    public async Task ProbeMasters()
    {
        var ct = TestContext.Current.CancellationToken;
        var spec = Environment.GetEnvironmentVariable("TIANWEN_MASTER_PSF_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(spec), "TIANWEN_MASTER_PSF_PROBE not set");

        var outPath = Environment.GetEnvironmentVariable("TIANWEN_MASTER_PSF_OUT");
        var lines = new List<string>();
        void Emit(string s)
        {
            lines.Add(s);
            System.Console.Error.WriteLine(s);
        }

        Emit($"{"master",-34} {"ch",-3} {"stars",7} {"FWHM",8} {"beta",8} {"rms",9}");
        foreach (var path in spec!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Image.TryReadFitsFile(path, out var img))
            {
                Emit($"{Path.GetFileName(path),-34} COULD NOT READ");
                continue;
            }

            var label = Path.GetFileNameWithoutExtension(path);
            if (label.Length > 34)
            {
                label = label[^34..];
            }
            var (channels, _, _) = img.Shape;
            var fwhm = new double[channels];
            for (var c = 0; c < channels; c++)
            {
                var stars = await img.FindStarsAsync(channel: c, snrMin: SnrMin, maxStars: MaxStars, cancellationToken: ct);
                var fit = PsfProfileFit.Measure(img, c, stars);
                if (fit is null)
                {
                    Emit($"{label,-34} {c,-3} {stars.Count,7} {"n/a",8} {"n/a",8} {"n/a",9}");
                    continue;
                }
                fwhm[c] = fit.Fwhm;
                Emit($"{label,-34} {c,-3} {stars.Count,7} {fit.Fwhm,8:F3} {fit.MoffatBeta,8:F2} {fit.MoffatLogRms,9:F4}");
            }
            if (channels >= 3 && fwhm[0] > 0)
            {
                Emit($"{label,-34} ratios  G/R {fwhm[1] / fwhm[0]:F3}   B/R {fwhm[2] / fwhm[0]:F3}");
            }
        }

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            await File.WriteAllLinesAsync(outPath, lines, ct);
        }
        Assert.NotEmpty(lines);
    }
}
