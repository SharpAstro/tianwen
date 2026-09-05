using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// P2 / H5: how much of the deconvolver's conditioning scalar does a real master actually move?
/// Runs the DEPLOYED estimator over retained session masters and reports the psf01 spread under the
/// shipped SAS range and under candidate ranges with a lower floor.
/// </summary>
/// <remarks>
/// <para>Skipped unless <c>TIANWEN_PSF_STORE_DIR</c> points at a dataset out-dir (the one holding
/// <c>session-masters/</c>), the same variable <see cref="PsfStoreVsCurrentDetectorProbe"/> uses so
/// one export drives both. <c>TIANWEN_PSF_PROBE_MAX</c> caps the master count (default 12, <c>0</c>
/// for all); it reads a real archive off a spinning disk and is far too slow for the ordinary
/// suite.</para>
///
/// <para><b>It runs <see cref="HfdPsfEstimator"/> rather than reading the PSF store, and that is the
/// point.</b> The store's per-channel numbers come from a profile FIT over stacked stars; the
/// conditioning scalar at inference comes from the estimator's median of DETECTED star FWHM on the
/// luminance channel. They are different estimators of the same quantity and they do not agree, so
/// answering "what would a real frame present to the model?" from the store would answer a
/// neighbouring question. The whole hypothesis is about the deployed path, so the deployed path is
/// what runs here.</para>
///
/// <para><b>Why the radius and not the encoded value.</b> The shipped range clamps at 1 px, which is
/// where this archive sits, so an encoded value cannot be re-encoded under a different range: the
/// clamp has already destroyed the number. The probe reads
/// <see cref="HfdPsfEstimator.MeasureRadiusPxAsync"/>, which is that measurement before any
/// encoding.</para>
///
/// <para><b>A spread, not a mean.</b> The question is whether the scalar is a LEVER: a conditioning
/// input every frame pins to the same value carries no information, however correct that value is.
/// So the statistic is p95 minus p05 over masters, per range, and the fraction of masters sitting
/// exactly on the floor.</para>
/// </remarks>
public class PsfEncodingSpreadProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PSF_STORE_DIR";
    private const string MaxVar = "TIANWEN_PSF_PROBE_MAX";

    /// <summary>The shipped SAS AI4 range, plus the candidates H5 names. The last is deliberately
    /// wider than the plan's proposal so the trade is visible rather than assumed: a floor buys
    /// spread and a ceiling spends it, and both ends move the same archive.</summary>
    private static readonly (string Label, float Min, float Max)[] Ranges =
    [
        ("[1.0, 8.0] shipped", 1.0f, 8.0f),
        ("[0.5, 8.0] H5", 0.5f, 8.0f),
        ("[0.5, 4.0]", 0.5f, 4.0f),
        ("[0.75, 3.0]", 0.75f, 3.0f),
    ];

    private static float Pct(IReadOnlyList<float> sorted, double p)
        => sorted.Count == 0 ? float.NaN : sorted[(int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1)];

    [Fact]
    public async Task ReportThePsf01SpreadARealArchivePresents()
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");

        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");

        var ct = TestContext.Current.CancellationToken;
        var maxMasters = int.TryParse(Environment.GetEnvironmentVariable(MaxVar), out var m) ? m : 12;
        var allMasters = Directory.GetFiles(mastersDir, "*.fits").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var masters = maxMasters > 0 && allMasters.Length > maxMasters ? allMasters.Take(maxMasters).ToArray() : allMasters;

        output.WriteLine($"masters  {masters.Length} of {allMasters.Length} (cap {MaxVar}={maxMasters})");
        output.WriteLine($"estimator HfdPsfEstimator, SNR>={HfdPsfEstimator.MinSnr}, radius = median detected FWHM / 2");
        output.WriteLine("");
        output.WriteLine($"{"master",-46} {"stars",7} {"FWHM",6} {"radius",7}   " + string.Join("  ", Ranges.Select(r => r.Label.PadLeft(18))));

        var estimator = new HfdPsfEstimator();
        var radii = new List<float>();
        var perRange = Ranges.Select(_ => new List<float>()).ToArray();

        foreach (var masterPath in masters)
        {
            var name = Path.GetFileNameWithoutExtension(masterPath);
            var shortName = name[..Math.Min(46, name.Length)];

            if (!Image.TryReadFitsFile(masterPath, out var master) || master is null)
            {
                output.WriteLine($"{shortName,-46} (unreadable)");
                continue;
            }

            HfdPsfEstimator.Measurement measured;
            try
            {
                measured = await estimator.MeasureRadiusPxAsync(master, ct);
            }
            finally
            {
                master.Release();
            }

            if (measured.Stars == 0)
            {
                // The fallback radius is a constant, so counting it would report the estimator's
                // default as if it were this archive's PSF.
                output.WriteLine($"{shortName,-46} (no stars; estimator would use its {HfdPsfEstimator.DefaultRadiusPx} px default)");
                continue;
            }

            radii.Add(measured.RadiusPx);
            var encoded = new float[Ranges.Length];
            for (var i = 0; i < Ranges.Length; i++)
            {
                encoded[i] = HfdPsfEstimator.EncodeRadiusToPsf01(measured.RadiusPx, Ranges[i].Min, Ranges[i].Max);
                perRange[i].Add(encoded[i]);
            }

            output.WriteLine($"{shortName,-46} {measured.Stars,7} {measured.RadiusPx * 2f,6:F2} {measured.RadiusPx,7:F2}   "
                + string.Join("  ", encoded.Select(e => e.ToString("F3").PadLeft(18))));
        }

        Assert.SkipWhen(radii.Count == 0, "no master yielded a measurement");

        radii.Sort();
        output.WriteLine("");
        output.WriteLine($"radius   n={radii.Count}  p05={Pct(radii, 0.05):F2}  p50={Pct(radii, 0.50):F2}  p95={Pct(radii, 0.95):F2} px "
            + $"(FWHM {Pct(radii, 0.05) * 2f:F2} / {Pct(radii, 0.50) * 2f:F2} / {Pct(radii, 0.95) * 2f:F2})");
        output.WriteLine("");
        output.WriteLine($"{"range",-20} {"p05",7} {"p50",7} {"p95",7} {"spread",8} {"on floor",9} {"at ceiling",11}");

        for (var i = 0; i < Ranges.Length; i++)
        {
            var v = perRange[i];
            v.Sort();
            var onFloor = v.Count(x => x <= 1e-6f);
            var atCeiling = v.Count(x => x >= 1f - 1e-6f);
            output.WriteLine($"{Ranges[i].Label,-20} {Pct(v, 0.05),7:F3} {Pct(v, 0.50),7:F3} {Pct(v, 0.95),7:F3} "
                + $"{Pct(v, 0.95) - Pct(v, 0.05),8:F3} {onFloor,6}/{v.Count,-3} {atCeiling,8}/{v.Count,-3}");
        }

        output.WriteLine("");
        output.WriteLine("Read the SPREAD column and the floor count together. A conditioning input every");
        output.WriteLine("frame pins to one value is not a lever, and a range whose floor most masters sit");
        output.WriteLine("on has already thrown the distinction away before the model sees it.");
    }
}
