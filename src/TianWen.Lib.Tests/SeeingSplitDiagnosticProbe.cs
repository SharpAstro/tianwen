using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;
using static TianWen.Lib.Tests.DeconvolutionProbeMeasures;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where does a seeing split's width difference go? The store's per-sub widths for the Orion
/// 2025-10-15 night ran 1.71 to 2.60 px and the two thirds stacked from them measured 2.84 and 2.76,
/// the same within noise (E2.10a's third run). One estimator, whole frame, at every stage between a
/// sub and a master: the three sharpest and three softest subs as the analyzer sees them (a mono
/// rendition of the raw mosaic) and as a colour plane (AHD), the two thirds, the whole-session staged
/// master, and the retained drizzle master of the same night. Whichever stage the difference vanishes
/// at is the one E2.10 has to change.
/// </summary>
/// <remarks>Skipped unless <c>TIANWEN_E210_DIAG=1</c>, with <c>TIANWEN_E210_PAIR_DIR</c> (the pair's
/// output dir, holding the full master beside <c>sharp/</c> and <c>soft/</c>) and
/// <c>TIANWEN_PSF_STORE_DIR</c> (the store for the sub list and the retained master).</remarks>
public class SeeingSplitDiagnosticProbe(ITestOutputHelper output)
{
    private const string SessionKey = "Great-Orion-Nebula/2025-10-15";

    private async Task ReportAsync(string label, Image image, CancellationToken ct, bool fitGreen = false)
    {
        var (channels, width, height) = image.Shape;
        var cells = new List<string>();
        for (var c = 0; c < channels; c++)
        {
            var (fwhm, stars) = await MeasuredFwhmAsync(FullPlane(image, c), width, height, ct);
            cells.Add($"ch{c} {fwhm,5:F2} px / {stars,5} stars");
        }

        if (fitGreen)
        {
            // The bright-star profile fit, which a faint detection cannot inflate: the width the
            // estimator step would read off this frame.
            var green = Math.Min(1, channels - 1);
            var (fit, diag) = await FitStarProfileAsync(FullPlane(image, green), width, height, PsfProfileFit.StarSelection.SignalFloor, 5f, 3000, ct);
            cells.Add(fit is { } f ? $"fit ch{green} {f.Fwhm:F2} px beta {f.MoffatBeta:F1}" : $"fit ch{green} {Describe(diag)}");
        }

        output.WriteLine($"{label,-46} {width,5} x {height,-5} {string.Join("   ", cells)}");
    }

    [Fact]
    public async Task ReportTheWidthAtEveryStageBetweenASubAndAMaster()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TIANWEN_E210_DIAG") == "1", "TIANWEN_E210_DIAG is not 1");
        var pairDir = Environment.GetEnvironmentVariable("TIANWEN_E210_PAIR_DIR");
        var storeDir = Environment.GetEnvironmentVariable("TIANWEN_PSF_STORE_DIR");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(pairDir) || string.IsNullOrWhiteSpace(storeDir), "TIANWEN_E210_PAIR_DIR and TIANWEN_PSF_STORE_DIR are both needed");
        var ct = TestContext.Current.CancellationToken;

        var store = await DatasetPsfStore.ReadAsync(Path.Combine(storeDir!, "stats", DatasetPsfStore.FileName), cancellationToken: ct);
        var record = store.Values.FirstOrDefault(r => r.SessionId.Contains(SessionKey, StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(record?.SubFile is null, $"no record with sub identity for {SessionKey}");
        var ranked = record!.SubFile!.Zip(record.SubFwhm, (f, w) => (File: f, Fwhm: w)).OrderBy(t => t.Fwhm).ToArray();
        var picks = ranked.Take(3).Concat(ranked.TakeLast(3)).ToArray();

        output.WriteLine($"session   {record.SessionId}; store widths {ranked[0].Fwhm:F2} to {ranked[^1].Fwhm:F2} px over {ranked.Length} subs (the analyzer's, mono mosaic)");
        output.WriteLine("estimator HfdPsfEstimator median FWHM over every detection at snr 5, each plane normalised to a peak of 1 first; whole frame");
        output.WriteLine("");

        foreach (var (file, storeFwhm) in picks)
        {
            if (!Image.TryReadFitsFile(file, out var raw) || raw is null)
            {
                output.WriteLine($"{Path.GetFileName(file),-44} (unreadable)");
                continue;
            }

            try
            {
                var name = Path.GetFileName(file)[..Math.Min(34, Path.GetFileName(file).Length)];
                var mono = await raw.DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken: ct);
                try
                {
                    await ReportAsync($"sub {name} store {storeFwhm:F2} mono", mono, ct);
                }
                finally
                {
                    if (!ReferenceEquals(mono, raw)) mono.Release();
                }

                foreach (var algorithm in new[] { DebayerAlgorithm.AHD, DebayerAlgorithm.VNG })
                {
                    var colour = await raw.DebayerAsync(algorithm, cancellationToken: ct);
                    try
                    {
                        await ReportAsync($"sub {name} store {storeFwhm:F2} {algorithm}", colour, ct, fitGreen: true);
                    }
                    finally
                    {
                        if (!ReferenceEquals(colour, raw)) colour.Release();
                    }
                }
            }
            finally
            {
                raw.Release();
            }
        }

        output.WriteLine("");
        var masters = new List<(string Label, string? Path)>
        {
            ("third: sharp (Float16Staged, 20)", Directory.GetFiles(Path.Combine(pairDir!, "sharp"), "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop") && !p.Contains("rejection"))),
            ("third: soft (Float16Staged, 21)", Directory.GetFiles(Path.Combine(pairDir!, "soft"), "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop") && !p.Contains("rejection"))),
            ("whole night (Float16Staged, 71)", Directory.GetFiles(pairDir!, "master_*.fits").FirstOrDefault(p => !p.Contains("autocrop") && !p.Contains("rejection"))),
            ("retained master (BayerDrizzle, 60)", Directory.GetFiles(Path.Combine(storeDir!, "session-masters"), "*.fits").FirstOrDefault(p => Path.GetFileName(p).Contains("Great-Orion-Nebula_2025-10-15", StringComparison.OrdinalIgnoreCase))),
        };
        foreach (var (label, path) in masters)
        {
            if (path is null || !Image.TryReadFitsFile(path, out var master) || master is null)
            {
                output.WriteLine($"{label,-44} (missing or unreadable)");
                continue;
            }

            try
            {
                await ReportAsync(label, master, ct, fitGreen: true);
            }
            finally
            {
                master.Release();
            }
        }
    }
}
