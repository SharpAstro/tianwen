using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Archive PSF/noise distribution report for the dataset builder (docs/plans/ai-denoise-deconv.md
/// §2.4 P0 deliverable, task #41). Characterises the registered sessions two ways:
/// <list type="bullet">
///   <item><b>PSF distribution</b> — per-sub median FWHM / HFD / ellipticity percentiles (the
///     population the denoiser sees) plus a <b>field-radius profile</b> of median FWHM + ellipticity
///     binned centre→corner (detected on each session master). The field-radius profile is the input
///     to the deconvolver's position-varying synthetic-PSF sweep (§2.2): a fast lens's corners are
///     genuinely broader than its centre, so the degradation must sample by field radius, and this
///     report says what range to sweep.</item>
///   <item><b>Noise floor</b> — per-session master background σ (MAD relative to full-scale), a
///     coarse cross-session noise characterisation.</item>
/// </list>
/// Pure analysis over <see cref="SessionRegistrar.RegisteredSession"/>s; no tile format coupling.
/// </summary>
public static class DatasetPsfNoiseReport
{
    /// <summary>A five-number summary of one metric across the sampled population.</summary>
    public sealed record Percentiles(double P5, double P25, double P50, double P75, double P95)
    {
        public static Percentiles Empty { get; } = new(0, 0, 0, 0, 0);
    }

    /// <summary>Median FWHM + ellipticity of stars whose normalised field radius (0 = frame centre,
    /// 1 = corner) falls in <c>[RMin, RMax)</c>, over all session masters.</summary>
    public sealed record RadiusBin(double RMin, double RMax, double MedianFwhm, double MedianEllipticity, int Stars);

    /// <summary>Per-optical-train sub-report. The field-radius PSF profile lives HERE, never
    /// aggregated across trains: a Newtonian's coma grows with field radius while a refractor's does
    /// not, so a merged profile would smear the position-varying degradation the deconvolver sweep
    /// must reproduce. Keyed by <see cref="CalibrationResolver.CalTrain.OpticalTrain"/> (camera +
    /// telescope + focal length -- i.e. one profile per OTA/camera combination).</summary>
    public sealed record TrainReport(
        string OpticalTrain,
        int Sessions,
        int Subs,
        long StarsSampled,
        Percentiles SubFwhm,
        Percentiles SubHfd,
        Percentiles SubEllipticity,
        ImmutableArray<RadiusBin> FieldRadiusProfile,
        Percentiles MasterNoiseRelative);

    /// <summary>The full report: an archive-wide population summary (the per-sub metrics + noise
    /// floor the denoiser sees across everything) plus a per-optical-train breakdown, each carrying
    /// its OWN field-radius PSF profile.</summary>
    public sealed record Report(
        int Sessions,
        int Subs,
        long StarsSampled,
        Percentiles SubFwhm,
        Percentiles SubHfd,
        Percentiles SubEllipticity,
        Percentiles MasterNoiseRelative,
        ImmutableArray<TrainReport> Trains);

    /// <summary>
    /// Builds the report over all <paramref name="sessions"/> at once (convenience for tests +
    /// small runs). The archive-scale builder should use <see cref="Accumulator"/> instead so each
    /// session master is released after its stats are folded in, rather than held for the whole run.
    /// </summary>
    public static async Task<Report> BuildAsync(
        IReadOnlyList<SessionRegistrar.RegisteredSession> sessions,
        int radiusBins = 5,
        float snrMin = 5f,
        int maxStars = 3000,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var acc = new Accumulator(radiusBins, snrMin, maxStars);
        foreach (var session in sessions)
        {
            await acc.AddAsync(session, logger, cancellationToken);
        }
        return acc.Build();
    }

    /// <summary>
    /// Incremental report builder: fold one <see cref="SessionRegistrar.RegisteredSession"/> in at a
    /// time (<see cref="AddAsync"/>) then <see cref="Build"/>. Per-sub metrics come from the gate's
    /// retained <see cref="FrameMetrics"/> (no detection); the field-radius profile re-detects stars
    /// on each session master (one detection per session, on the sharpest/deepest frame — the one the
    /// deconv sweep degrades). Nothing but small accumulators is retained across sessions, so the
    /// archive-scale build can release each master after folding it in.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly int _radiusBins;
        private readonly float _snrMin;
        private readonly int _maxStars;
        // One accumulator per optical train (OTA/camera). The field-radius profile is optics-specific
        // -- it must not merge a coma-heavy Newtonian with a flat-field refractor -- so everything is
        // bucketed by CalTrain and the overall population summary is derived by concatenation.
        private readonly Dictionary<CalibrationResolver.CalTrain, TrainAcc> _byTrain = new();

        public Accumulator(int radiusBins = 5, float snrMin = 5f, int maxStars = 3000)
        {
            _radiusBins = radiusBins;
            _snrMin = snrMin;
            _maxStars = maxStars;
        }

        public async Task AddAsync(SessionRegistrar.RegisteredSession session, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            var train = CalibrationResolver.CalTrain.OpticalTrain(session.Session.Lights[0]);
            if (!_byTrain.TryGetValue(train, out var acc))
            {
                _byTrain[train] = acc = new TrainAcc(train.Describe(), _radiusBins);
            }

            acc.Sessions++;
            foreach (var sub in session.Subs)
            {
                acc.Fwhm.Add(sub.Metrics.MedianFwhm);
                acc.Hfd.Add(sub.Metrics.MedianHfd);
                acc.Ecc.Add(sub.Metrics.MedianEllipticity);
                acc.Subs++;
            }

            acc.Noise.Add(RelativeBackgroundMad(session.Master));

            var stars = await session.Master.FindStarsAsync(
                channel: 0, snrMin: _snrMin, maxStars: _maxStars, cancellationToken: cancellationToken);
            var cx = session.CanvasWidth * 0.5;
            var cy = session.CanvasHeight * 0.5;
            var halfDiag = 0.5 * Math.Sqrt((double)session.CanvasWidth * session.CanvasWidth + (double)session.CanvasHeight * session.CanvasHeight);
            if (halfDiag > 0)
            {
                foreach (var star in stars)
                {
                    var dx = star.XCentroid - cx;
                    var dy = star.YCentroid - cy;
                    var rNorm = Math.Sqrt(dx * dx + dy * dy) / halfDiag;
                    var bin = Math.Min(_radiusBins - 1, (int)(rNorm * _radiusBins));
                    if (bin < 0) bin = 0;
                    acc.BinFwhm[bin].Add(star.StarFWHM);
                    acc.BinEcc[bin].Add(star.Ellipticity);
                    acc.StarsSampled++;
                }
            }
            logger?.LogInformation("  [{Session}] PSF sampled {Stars} stars ({Train})", session.Session.Id, stars.Count, acc.Label);
        }

        public Report Build()
        {
            var trains = ImmutableArray.CreateBuilder<TrainReport>(_byTrain.Count);
            // Overall population = concatenation across trains (only the field-radius profile stays
            // per-train). Trains are ordered by label so the report is deterministic across runs.
            var allFwhm = new List<float>();
            var allHfd = new List<float>();
            var allEcc = new List<float>();
            var allNoise = new List<double>();
            var totalSessions = 0;
            var totalSubs = 0;
            long totalStars = 0;

            foreach (var acc in _byTrain.Values.OrderBy(a => a.Label, StringComparer.Ordinal))
            {
                var profile = ImmutableArray.CreateBuilder<RadiusBin>(_radiusBins);
                for (var b = 0; b < _radiusBins; b++)
                {
                    profile.Add(new RadiusBin(
                        RMin: (double)b / _radiusBins,
                        RMax: (double)(b + 1) / _radiusBins,
                        MedianFwhm: Median(acc.BinFwhm[b]),
                        MedianEllipticity: Median(acc.BinEcc[b]),
                        Stars: acc.BinFwhm[b].Count));
                }
                trains.Add(new TrainReport(
                    OpticalTrain: acc.Label,
                    Sessions: acc.Sessions,
                    Subs: acc.Subs,
                    StarsSampled: acc.StarsSampled,
                    SubFwhm: PercentilesOf(acc.Fwhm),
                    SubHfd: PercentilesOf(acc.Hfd),
                    SubEllipticity: PercentilesOf(acc.Ecc),
                    FieldRadiusProfile: profile.MoveToImmutable(),
                    MasterNoiseRelative: PercentilesOf(acc.Noise)));

                allFwhm.AddRange(acc.Fwhm);
                allHfd.AddRange(acc.Hfd);
                allEcc.AddRange(acc.Ecc);
                allNoise.AddRange(acc.Noise);
                totalSessions += acc.Sessions;
                totalSubs += acc.Subs;
                totalStars += acc.StarsSampled;
            }

            return new Report(
                Sessions: totalSessions,
                Subs: totalSubs,
                StarsSampled: totalStars,
                SubFwhm: PercentilesOf(allFwhm),
                SubHfd: PercentilesOf(allHfd),
                SubEllipticity: PercentilesOf(allEcc),
                MasterNoiseRelative: PercentilesOf(allNoise),
                Trains: trains.MoveToImmutable());
        }

        /// <summary>Per-train accumulator: the same small metric lists + radius bins the whole report
        /// used to keep once, now held one instance per optical train.</summary>
        private sealed class TrainAcc
        {
            public string Label { get; }
            public int Sessions;
            public int Subs;
            public long StarsSampled;
            public readonly List<float> Fwhm = new();
            public readonly List<float> Hfd = new();
            public readonly List<float> Ecc = new();
            public readonly List<double> Noise = new();
            public readonly List<float>[] BinFwhm;
            public readonly List<float>[] BinEcc;

            public TrainAcc(string label, int radiusBins)
            {
                Label = label;
                BinFwhm = new List<float>[radiusBins];
                BinEcc = new List<float>[radiusBins];
                for (var b = 0; b < radiusBins; b++)
                {
                    BinFwhm[b] = new List<float>();
                    BinEcc[b] = new List<float>();
                }
            }
        }
    }

    /// <summary>Renders the report as a human-readable Markdown file (the P0 "archive PSF/noise
    /// distribution report" deliverable).</summary>
    public static async Task WriteMarkdownAsync(Report report, string path, CancellationToken cancellationToken = default)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Dataset PSF / Noise Distribution Report");
        sb.AppendLine();
        sb.AppendLine(string.Create(ci, $"- Sessions: {report.Sessions}"));
        sb.AppendLine(string.Create(ci, $"- Subs (registered): {report.Subs}"));
        sb.AppendLine(string.Create(ci, $"- Stars sampled (field-radius profile): {report.StarsSampled}"));
        sb.AppendLine(string.Create(ci, $"- Optical trains (OTA/camera): {report.Trains.Length}"));
        sb.AppendLine();
        sb.AppendLine("## Per-sub PSF distribution (median-of-frame metrics, all trains)");
        sb.AppendLine();
        sb.AppendLine("| Metric | p5 | p25 | p50 | p75 | p95 |");
        sb.AppendLine("|--------|----|-----|-----|-----|-----|");
        AppendPct(sb, ci, "FWHM (px)", report.SubFwhm);
        AppendPct(sb, ci, "HFD (px)", report.SubHfd);
        AppendPct(sb, ci, "Ellipticity", report.SubEllipticity);
        sb.AppendLine();
        sb.AppendLine("## Noise floor (per-session master background sigma, relative to full-scale, all trains)");
        sb.AppendLine();
        sb.AppendLine("| Metric | p5 | p25 | p50 | p75 | p95 |");
        sb.AppendLine("|--------|----|-----|-----|-----|-----|");
        AppendPct(sb, ci, "MAD / max", report.MasterNoiseRelative);
        sb.AppendLine();
        sb.AppendLine("## Field-radius PSF profile (per optical train, centre -> corner)");
        sb.AppendLine();
        sb.AppendLine("Drives the deconvolver's position-varying synthetic-PSF sweep: sample FWHM per");
        sb.AppendLine("field-radius bin so corner degradation matches the optics. Reported PER OPTICAL");
        sb.AppendLine("TRAIN -- a Newtonian's coma grows toward the corner while a refractor's field");
        sb.AppendLine("stays flat, so a single merged profile would smear both. Sweep each train against");
        sb.AppendLine("its own row set.");
        sb.AppendLine();
        foreach (var train in report.Trains)
        {
            sb.AppendLine(string.Create(ci, $"### {train.OpticalTrain}"));
            sb.AppendLine();
            sb.AppendLine(string.Create(ci,
                $"- Sessions: {train.Sessions} | Subs: {train.Subs} | Stars: {train.StarsSampled}"));
            sb.AppendLine(string.Create(ci,
                $"- FWHM p50: {train.SubFwhm.P50:F3} px | Ellipticity p50: {train.SubEllipticity.P50:F3} | Noise p50: {train.MasterNoiseRelative.P50:F5}"));
            sb.AppendLine();
            sb.AppendLine("| Radius (norm) | Median FWHM (px) | Median ellipticity | Stars |");
            sb.AppendLine("|---------------|------------------|--------------------|-------|");
            foreach (var bin in train.FieldRadiusProfile)
            {
                sb.AppendLine(string.Create(ci, $"| {bin.RMin:F2}-{bin.RMax:F2} | {bin.MedianFwhm:F3} | {bin.MedianEllipticity:F3} | {bin.Stars} |"));
            }
            sb.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken);
    }

    private static void AppendPct(StringBuilder sb, CultureInfo ci, string label, Percentiles p) =>
        sb.AppendLine(string.Create(ci, $"| {label} | {p.P5:F3} | {p.P25:F3} | {p.P50:F3} | {p.P75:F3} | {p.P95:F3} |"));

    /// <summary>MAD of the master's channel 0 divided by <see cref="Image.MaxValue"/> — a
    /// full-scale-relative background sigma proxy (background-dominated, robust to the ~few % star
    /// pixels), comparable across cameras/scales.</summary>
    private static double RelativeBackgroundMad(Image master)
    {
        var span = master.GetChannelSpan(0);
        var buf = new float[span.Length];
        var n = 0;
        for (var i = 0; i < span.Length; i++)
        {
            if (!float.IsNaN(span[i])) buf[n++] = span[i];
        }
        if (n == 0) return 0.0;
        var slice = buf.AsSpan(0, n);
        var median = StatisticsHelper.MedianFast(slice);
        for (var i = 0; i < slice.Length; i++)
        {
            slice[i] = MathF.Abs(slice[i] - median);
        }
        var mad = StatisticsHelper.MedianFast(slice);
        var max = master.MaxValue;
        return max > 0 ? mad / max : mad;
    }

    private static double Median(List<float> values)
    {
        if (values.Count == 0) return 0.0;
        values.Sort();
        return values[values.Count / 2];
    }

    private static Percentiles PercentilesOf<T>(List<T> values) where T : struct, IConvertible
    {
        if (values.Count == 0) return Percentiles.Empty;
        var arr = new double[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            arr[i] = values[i].ToDouble(CultureInfo.InvariantCulture);
        }
        Array.Sort(arr);
        return new Percentiles(
            Pick(arr, 0.05), Pick(arr, 0.25), Pick(arr, 0.50), Pick(arr, 0.75), Pick(arr, 0.95));
    }

    private static double Pick(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var idx = (int)Math.Round(q * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
