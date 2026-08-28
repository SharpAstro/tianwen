using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Answers "does a star-detection change invalidate the stored PSF measurements?" with a number, by
/// re-measuring the retained session masters through the SAME production code that wrote the store and
/// diffing the result against it.
/// </summary>
/// <remarks>
/// <para>Skipped unless <c>TIANWEN_PSF_STORE_DIR</c> points at a dataset out-dir (the one holding
/// <c>stats/psf-sessions.jsonl</c> and <c>session-masters/</c>); it reads a real archive off a spinning
/// disk and is far too slow for the ordinary suite. <c>TIANWEN_PSF_PROBE_MAX</c> caps the master count
/// (default 8, <c>0</c> for all).</para>
///
/// <para><b>It re-runs <see cref="DatasetPsfNoiseReport.MeasureMasterAsync"/> rather than calling
/// <c>FindStarsAsync</c> itself, and that is the whole point.</b> The store's numbers are not raw
/// detections: they are stars detected in EVERY channel and matched across them
/// (<c>MatchStarsAcrossChannels</c>), binned by field radius, and the published percentiles then band
/// that set to the 55th-90th flux percentile. An earlier version of this probe pooled every detection
/// instead and produced a 1.53x "count ratio" that was mostly selection, not detector -- a confidently
/// wrong answer to the question being asked. Driving the production path makes the selection identical
/// on both sides by construction, so a difference IS the detector.</para>
///
/// <para>The flux band is applied here to both sides symmetrically, because the report applies it and
/// the published figures reflect it. Percentiles are reported at p05 and p95 as well as the median:
/// banding is what the report does, but a close-pair population is a small tail by count and a median
/// is exactly the statistic that cannot see it, so the low percentiles and the count are where a
/// deblending change shows up.</para>
///
/// <para>A re-measure is <c>tianwen dataset build --force-psf</c>, which REPLACES existing records;
/// <c>--regen-psf</c> only fills gaps and cannot correct a record that is present and wrong.</para>
/// </remarks>
public class PsfStoreVsCurrentDetectorProbe(ITestOutputHelper output)
{
    private const string DirVar = "TIANWEN_PSF_STORE_DIR";
    private const string MaxVar = "TIANWEN_PSF_PROBE_MAX";

    private static float Pct(IReadOnlyList<float> sorted, double p)
        => sorted.Count == 0 ? float.NaN : sorted[(int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1)];

    /// <summary>
    /// Pools one channel's radius bins and keeps the 55th-90th flux percentile, mirroring the report's
    /// own <c>FluxBand</c> (which pools across radius bins before cutting, and gives up under 40 stars
    /// because percentiles of fewer mean nothing). Returns the surviving FWHM values, sorted.
    /// </summary>
    private static List<float> BandedFwhm(DatasetPsfNoiseReport.RadiusSamples[]? bins)
    {
        var result = new List<float>();
        if (bins is null)
        {
            return result;
        }

        var flux = new List<float>();
        foreach (var bin in bins)
        {
            if (bin.Flux is null)
            {
                return result;
            }
            flux.AddRange(bin.Flux);
        }

        if (flux.Count < 40)
        {
            return result;
        }

        flux.Sort();
        var low = flux[(int)(0.55 * (flux.Count - 1))];
        var high = flux[(int)(0.90 * (flux.Count - 1))];

        foreach (var bin in bins)
        {
            if (bin.Flux is null || bin.Fwhm is null)
            {
                continue;
            }

            var n = Math.Min(bin.Flux.Length, bin.Fwhm.Length);
            for (var i = 0; i < n; i++)
            {
                var f = bin.Fwhm[i];
                if (bin.Flux[i] >= low && bin.Flux[i] <= high && float.IsFinite(f) && f > 0)
                {
                    result.Add(f);
                }
            }
        }

        result.Sort();
        return result;
    }

    private static int CountAll(DatasetPsfNoiseReport.RadiusSamples[]? bins)
    {
        var n = 0;
        if (bins is null)
        {
            return n;
        }

        foreach (var bin in bins)
        {
            if (bin.Fwhm is not null)
            {
                n += bin.Fwhm.Length;
            }
        }

        return n;
    }

    [Fact]
    public async Task ReportWhetherTheStoredPsfStillMatchesTheCurrentDetector()
    {
        var root = Environment.GetEnvironmentVariable(DirVar);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{DirVar} not set");

        var storePath = Path.Combine(root!, "stats", DatasetPsfStore.FileName);
        var mastersDir = Path.Combine(root!, "session-masters");
        Assert.SkipUnless(File.Exists(storePath), $"no PSF store at {storePath}");
        Assert.SkipUnless(Directory.Exists(mastersDir), $"no session-masters at {mastersDir}");

        var ct = TestContext.Current.CancellationToken;
        var stored = await DatasetPsfStore.ReadAsync(storePath, logger: null, ct);

        output.WriteLine($"store    {stored.Count} sessions, written {File.GetLastWriteTimeUtc(storePath):u}");

        var maxMasters = int.TryParse(Environment.GetEnvironmentVariable(MaxVar), out var m) ? m : 8;
        var allMasters = Directory.GetFiles(mastersDir, "*.fits");
        var masters = allMasters.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (maxMasters > 0 && masters.Length > maxMasters)
        {
            masters = [.. masters.Take(maxMasters)];
        }

        output.WriteLine($"masters  {masters.Length} of {allMasters.Length} (cap {MaxVar}={maxMasters})");
        output.WriteLine("");
        output.WriteLine($"{"session",-40} {"n old",7} {"n new",7} {"ratio",6} | {"p50 old",8} {"p50 new",8} {"dp50",7} | {"dp05",7} {"dp95",7}");

        var countRatios = new List<float>();
        var d50 = new List<float>();
        var d05 = new List<float>();
        var d95 = new List<float>();
        var matched = 0;

        foreach (var masterPath in masters)
        {
            var name = Path.GetFileNameWithoutExtension(masterPath);
            var shortName = name[..Math.Min(40, name.Length)];

            // The session id is "dir|CAMERA[|OBJECT[|FILTER]]" and the master file name is that whole id
            // with every separator turned into an underscore, so match it WHOLE. Matching only the
            // leading path segments is ambiguous and silently wrong: a session directory can hold more
            // than one target (Pleiades and Triangulum Galaxy share one night here), so a prefix match
            // pairs the second master with the first master's record and reports the difference between
            // two different objects as a detector change.
            var hit = stored.FirstOrDefault(kv =>
                string.Equals(kv.Key.Replace('/', '_').Replace('|', '_'), name, StringComparison.Ordinal));
            if (hit.Value is not { BinsByChannel: not null } storedPsf)
            {
                output.WriteLine($"{shortName,-40} (no stored record with bins)");
                continue;
            }

            if (!Image.TryReadFitsFile(masterPath, out var master) || master is null)
            {
                output.WriteLine($"{shortName,-40} (unreadable)");
                continue;
            }

            DatasetPsfNoiseReport.SessionPsf fresh;
            try
            {
                // The production measurement path, so selection + binning are identical to the store's.
                // Sub arrays are carried through untouched and play no part in the bins, so they are
                // empty here: this probe compares the MASTER measurement only.
                fresh = await DatasetPsfNoiseReport.MeasureMasterAsync(
                    sessionId: storedPsf.SessionId,
                    opticalTrain: storedPsf.OpticalTrain,
                    master: master,
                    canvasWidth: master.Width,
                    canvasHeight: master.Height,
                    subFwhm: [],
                    subHfd: [],
                    subEllipticity: [],
                    masterStrategy: storedPsf.MasterStrategy,
                    cancellationToken: ct);
            }
            finally
            {
                master.Release();
            }

            var channels = Math.Min(storedPsf.BinsByChannel!.Length, fresh.BinsByChannel?.Length ?? 0);
            var oldBanded = new List<float>();
            var newBanded = new List<float>();
            var oldCount = 0;
            var newCount = 0;
            for (var c = 0; c < channels; c++)
            {
                oldBanded.AddRange(BandedFwhm(storedPsf.BinsByChannel[c]));
                newBanded.AddRange(BandedFwhm(fresh.BinsByChannel![c]));
                oldCount += CountAll(storedPsf.BinsByChannel[c]);
                newCount += CountAll(fresh.BinsByChannel![c]);
            }
            oldBanded.Sort();
            newBanded.Sort();

            if (oldBanded.Count == 0 || newBanded.Count == 0)
            {
                output.WriteLine($"{shortName,-40} (band empty: {oldBanded.Count} old / {newBanded.Count} new)");
                continue;
            }

            // Count on the UNBANDED set: banding keeps a fixed fraction by construction, so a banded
            // count cannot show that the detector found more stars.
            var ratio = (float)newCount / Math.Max(1, oldCount);
            var od50 = Pct(oldBanded, 0.50);
            var nd50 = Pct(newBanded, 0.50);
            var dd05 = Pct(newBanded, 0.05) - Pct(oldBanded, 0.05);
            var dd95 = Pct(newBanded, 0.95) - Pct(oldBanded, 0.95);

            output.WriteLine(
                $"{shortName,-40} {oldCount,7} {newCount,7} {ratio,6:F3} | " +
                $"{od50,8:F3} {nd50,8:F3} {nd50 - od50,7:F3} | {dd05,7:F3} {dd95,7:F3}");

            countRatios.Add(ratio);
            d50.Add(nd50 - od50);
            d05.Add(dd05);
            d95.Add(dd95);
            matched++;
        }

        output.WriteLine("");
        if (matched == 0)
        {
            output.WriteLine("no session matched -- check the id-to-filename mapping");
            return;
        }

        countRatios.Sort();
        d50.Sort();
        d05.Sort();
        d95.Sort();

        output.WriteLine($"matched  {matched} sessions, measured through DatasetPsfNoiseReport.MeasureMasterAsync");
        output.WriteLine($"count    new/old matched-star count: p05={Pct(countRatios, 0.05):F3} p50={Pct(countRatios, 0.50):F3} p95={Pct(countRatios, 0.95):F3}");
        output.WriteLine($"dp50     new-minus-old banded FWHM:  p05={Pct(d50, 0.05):F3} p50={Pct(d50, 0.50):F3} p95={Pct(d50, 0.95):F3} px");
        output.WriteLine($"dp05     new-minus-old banded FWHM:  p05={Pct(d05, 0.05):F3} p50={Pct(d05, 0.50):F3} p95={Pct(d05, 0.95):F3} px");
        output.WriteLine($"dp95     new-minus-old banded FWHM:  p05={Pct(d95, 0.05):F3} p50={Pct(d95, 0.50):F3} p95={Pct(d95, 0.95):F3} px");
        output.WriteLine("");
        output.WriteLine("Read dp05 and the count ratio, not dp50: a deblended close pair is a small tail by");
        output.WriteLine("count and cannot move a median, but it lands at the NARROW end of the distribution.");
    }
}
