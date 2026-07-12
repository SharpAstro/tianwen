using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
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

    /// <summary>The full report.</summary>
    public sealed record Report(
        int Sessions,
        int Subs,
        long StarsSampled,
        Percentiles SubFwhm,
        Percentiles SubHfd,
        Percentiles SubEllipticity,
        ImmutableArray<RadiusBin> FieldRadiusProfile,
        Percentiles MasterNoiseRelative);

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
        private readonly List<float> _fwhm = new();
        private readonly List<float> _hfd = new();
        private readonly List<float> _ecc = new();
        private readonly List<double> _noise = new();
        private readonly List<float>[] _binFwhm;
        private readonly List<float>[] _binEcc;
        private int _sessions;
        private int _subs;
        private long _starsSampled;

        public Accumulator(int radiusBins = 5, float snrMin = 5f, int maxStars = 3000)
        {
            _radiusBins = radiusBins;
            _snrMin = snrMin;
            _maxStars = maxStars;
            _binFwhm = new List<float>[radiusBins];
            _binEcc = new List<float>[radiusBins];
            for (var b = 0; b < radiusBins; b++)
            {
                _binFwhm[b] = new List<float>();
                _binEcc[b] = new List<float>();
            }
        }

        public async Task AddAsync(SessionRegistrar.RegisteredSession session, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            _sessions++;
            foreach (var sub in session.Subs)
            {
                _fwhm.Add(sub.Metrics.MedianFwhm);
                _hfd.Add(sub.Metrics.MedianHfd);
                _ecc.Add(sub.Metrics.MedianEllipticity);
                _subs++;
            }

            _noise.Add(RelativeBackgroundMad(session.Master));

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
                    _binFwhm[bin].Add(star.StarFWHM);
                    _binEcc[bin].Add(star.Ellipticity);
                    _starsSampled++;
                }
            }
            logger?.LogInformation("  [{Session}] PSF sampled {Stars} stars", session.Session.Id, stars.Count);
        }

        public Report Build()
        {
            var profile = ImmutableArray.CreateBuilder<RadiusBin>(_radiusBins);
            for (var b = 0; b < _radiusBins; b++)
            {
                profile.Add(new RadiusBin(
                    RMin: (double)b / _radiusBins,
                    RMax: (double)(b + 1) / _radiusBins,
                    MedianFwhm: Median(_binFwhm[b]),
                    MedianEllipticity: Median(_binEcc[b]),
                    Stars: _binFwhm[b].Count));
            }

            return new Report(
                Sessions: _sessions,
                Subs: _subs,
                StarsSampled: _starsSampled,
                SubFwhm: PercentilesOf(_fwhm),
                SubHfd: PercentilesOf(_hfd),
                SubEllipticity: PercentilesOf(_ecc),
                FieldRadiusProfile: profile.MoveToImmutable(),
                MasterNoiseRelative: PercentilesOf(_noise));
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
        sb.AppendLine();
        sb.AppendLine("## Per-sub PSF distribution (median-of-frame metrics)");
        sb.AppendLine();
        sb.AppendLine("| Metric | p5 | p25 | p50 | p75 | p95 |");
        sb.AppendLine("|--------|----|-----|-----|-----|-----|");
        AppendPct(sb, ci, "FWHM (px)", report.SubFwhm);
        AppendPct(sb, ci, "HFD (px)", report.SubHfd);
        AppendPct(sb, ci, "Ellipticity", report.SubEllipticity);
        sb.AppendLine();
        sb.AppendLine("## Field-radius PSF profile (session masters, centre -> corner)");
        sb.AppendLine();
        sb.AppendLine("Drives the deconvolver's position-varying synthetic-PSF sweep: sample FWHM per");
        sb.AppendLine("field-radius bin so corner degradation matches the optics.");
        sb.AppendLine();
        sb.AppendLine("| Radius (norm) | Median FWHM (px) | Median ellipticity | Stars |");
        sb.AppendLine("|---------------|------------------|--------------------|-------|");
        foreach (var bin in report.FieldRadiusProfile)
        {
            sb.AppendLine(string.Create(ci, $"| {bin.RMin:F2}-{bin.RMax:F2} | {bin.MedianFwhm:F3} | {bin.MedianEllipticity:F3} | {bin.Stars} |"));
        }
        sb.AppendLine();
        sb.AppendLine("## Noise floor (per-session master background sigma, relative to full-scale)");
        sb.AppendLine();
        sb.AppendLine("| Metric | p5 | p25 | p50 | p75 | p95 |");
        sb.AppendLine("|--------|----|-----|-----|-----|-----|");
        AppendPct(sb, ci, "MAD / max", report.MasterNoiseRelative);
        sb.AppendLine();

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
