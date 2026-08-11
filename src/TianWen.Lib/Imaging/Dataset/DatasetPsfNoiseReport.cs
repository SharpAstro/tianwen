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
///   <item><b>PSF distribution</b>: per-sub median FWHM / HFD / ellipticity percentiles (the
///     population the denoiser sees) plus a <b>field-radius profile</b> of median FWHM + ellipticity
///     binned centre→corner (detected on each session master). The field-radius profile is the input
///     to the deconvolver's position-varying synthetic-PSF sweep (§2.2): a fast lens's corners are
///     genuinely broader than its centre, so the degradation must sample by field radius, and this
///     report says what range to sweep.</item>
///   <item><b>Noise floor</b>: per-session master background σ (MAD relative to full-scale), a
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

    /// <summary>The raw per-bin star samples for ONE session, before any cross-session median.</summary>
    /// <param name="Fwhm">Every sampled star's FWHM whose field radius fell in this bin.</param>
    /// <param name="Ellipticity">The same stars' ellipticities, index-aligned with <paramref name="Fwhm"/>.</param>
    public sealed record RadiusSamples(float[] Fwhm, float[] Ellipticity);

    /// <summary>
    /// One session's contribution to the report, in the form that gets PERSISTED
    /// (<see cref="DatasetPsfStore"/>) so the report survives a partial or resumed run.
    ///
    /// <para>These are raw samples, not per-session summaries, and that is load-bearing: the report's
    /// field-radius profile is a median over every star in a bin across all sessions of an optical
    /// train, which a stored median-of-medians could not reconstruct. Persisting the samples means a
    /// resumed run rebuilds a byte-identical report to the one an uninterrupted run would have
    /// produced.</para>
    /// </summary>
    /// <param name="SessionId">Portable session id; the store's key (last record per id wins).</param>
    /// <param name="OpticalTrain">
    /// <see cref="CalibrationResolver.CalTrain.Describe"/> of the session's train. Stored rather than
    /// re-derived because re-deriving needs the session's frames, which a resumed run has not read.
    /// </param>
    /// <param name="Bins">Per-bin samples, indexed by bin; length is the report's radius-bin count.</param>
    public sealed record SessionPsf(
        string SessionId,
        string OpticalTrain,
        float[] SubFwhm,
        float[] SubHfd,
        float[] SubEllipticity,
        double MasterNoiseRelative,
        RadiusSamples[] Bins);

    /// <summary>Per-optical-train sub-report. The field-radius PSF profile lives HERE, never
    /// aggregated across trains: a Newtonian's coma grows with field radius while a refractor's does
    /// not, so a merged profile would smear the position-varying degradation the deconvolver sweep
    /// must reproduce. Keyed by <see cref="CalibrationResolver.CalTrain.OpticalTrain"/> (camera +
    /// telescope + focal length -- i.e. one profile per OTA/camera combination).</summary>
    /// <param name="RecordedAs">The distinct header labels that folded into this train, sorted; a
    /// single entry equal to <paramref name="OpticalTrain"/> in the ordinary case. More than one
    /// means <see cref="TelescopeAliases"/> merged differently-spelled headers, and the report says
    /// so: a merge that changes how many sessions back a profile has to be visible in the artifact,
    /// or the reader cannot tell a genuine 38-session train from an over-eager alias.</param>
    public sealed record TrainReport(
        string OpticalTrain,
        int Sessions,
        int Subs,
        long StarsSampled,
        Percentiles SubFwhm,
        Percentiles SubHfd,
        Percentiles SubEllipticity,
        ImmutableArray<RadiusBin> FieldRadiusProfile,
        Percentiles MasterNoiseRelative,
        ImmutableArray<string> RecordedAs);

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
    /// Measures ONE registered session into a persistable <see cref="SessionPsf"/>: the per-sub
    /// metrics the gate already retained (no detection), the master's relative background sigma, and
    /// one star detection on the master binned by field radius.
    ///
    /// <para>Separated from <see cref="Accumulator.Add(SessionPsf, ILogger?)"/> so the archive builder
    /// can persist the record and fold the very same object, which is what lets a resumed run rebuild
    /// the report without the master it no longer has. This is the only place a measurement is
    /// produced.</para>
    /// </summary>
    public static async Task<SessionPsf> MeasureSessionAsync(
        SessionRegistrar.RegisteredSession session,
        int radiusBins = 5,
        float snrMin = 5f,
        int maxStars = 3000,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var label = CalibrationResolver.CalTrain.OpticalTrain(session.Session.Lights[0]).Describe();

        var subFwhm = new float[session.Subs.Length];
        var subHfd = new float[session.Subs.Length];
        var subEcc = new float[session.Subs.Length];
        for (var i = 0; i < session.Subs.Length; i++)
        {
            var metrics = session.Subs[i].Metrics;
            subFwhm[i] = metrics.MedianFwhm;
            subHfd[i] = metrics.MedianHfd;
            subEcc[i] = metrics.MedianEllipticity;
        }

        var binFwhm = new List<float>[radiusBins];
        var binEcc = new List<float>[radiusBins];
        for (var b = 0; b < radiusBins; b++)
        {
            binFwhm[b] = new List<float>();
            binEcc[b] = new List<float>();
        }

        var stars = await session.Master.FindStarsAsync(
            channel: 0, snrMin: snrMin, maxStars: maxStars, cancellationToken: cancellationToken);
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
                var bin = Math.Min(radiusBins - 1, (int)(rNorm * radiusBins));
                if (bin < 0) bin = 0;
                binFwhm[bin].Add(star.StarFWHM);
                binEcc[bin].Add(star.Ellipticity);
            }
        }

        var bins = new RadiusSamples[radiusBins];
        for (var b = 0; b < radiusBins; b++)
        {
            bins[b] = new RadiusSamples(binFwhm[b].ToArray(), binEcc[b].ToArray());
        }

        logger?.LogInformation("  [{Session}] PSF sampled {Stars} stars ({Train})", session.Session.Id, stars.Count, label);
        return new SessionPsf(
            SessionId: session.Session.Id,
            OpticalTrain: label,
            SubFwhm: subFwhm,
            SubHfd: subHfd,
            SubEllipticity: subEcc,
            MasterNoiseRelative: RelativeBackgroundMad(session.Master),
            Bins: bins);
    }

    /// <summary>
    /// Incremental report builder: fold one <see cref="SessionRegistrar.RegisteredSession"/> in at a
    /// time (<see cref="AddAsync"/>) then <see cref="Build"/>. Per-sub metrics come from the gate's
    /// retained <see cref="FrameMetrics"/> (no detection); the field-radius profile re-detects stars
    /// on each session master (one detection per session, on the sharpest/deepest frame, the one the
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
        // bucketed by train and the overall population summary is derived by concatenation. Keyed by
        // the train's DESCRIBED label rather than the CalTrain value, because a record read back from
        // the store carries only the label (its frames were never re-read) and must bucket with a
        // freshly measured session of the same train.
        private readonly Dictionary<string, TrainAcc> _byTrain = new(StringComparer.Ordinal);

        public Accumulator(int radiusBins = 5, float snrMin = 5f, int maxStars = 3000)
        {
            _radiusBins = radiusBins;
            _snrMin = snrMin;
            _maxStars = maxStars;
        }

        /// <summary>Measures a freshly registered session then folds it in. The measurement half is
        /// <see cref="MeasureSessionAsync"/> so a caller that wants to PERSIST the record (the
        /// archive builder, via <see cref="DatasetPsfStore"/>) measures once and folds the same
        /// record, rather than there being a second way to compute one.</summary>
        public async Task AddAsync(SessionRegistrar.RegisteredSession session, ILogger? logger = null, CancellationToken cancellationToken = default)
            => Add(await MeasureSessionAsync(session, _radiusBins, _snrMin, _maxStars, logger, cancellationToken), logger);

        /// <summary>
        /// Folds one session's persisted samples into the accumulator. This is the ONLY path that
        /// mutates the accumulator, so a record read back from <see cref="DatasetPsfStore"/> and a
        /// record just measured are treated identically by construction.
        /// </summary>
        public void Add(SessionPsf record, ILogger? logger = null)
        {
            if (record.Bins.Length != _radiusBins)
            {
                // Only reachable if the radius-bin count changed between runs, which would make the
                // stored samples unbinnable. Loud rather than silent: a dropped session is exactly
                // the failure this store exists to prevent.
                logger?.LogWarning(
                    "PSF record for {Session} has {Actual} radius bin(s), expected {Expected} -- not folded into the report. Delete {Store} to re-measure at the new bin count.",
                    record.SessionId, record.Bins.Length, _radiusBins, DatasetPsfStore.FileName);
                return;
            }

            // Keyed by the STORED label, so a resumed session (whose frames were never re-read) lands
            // in the same train bucket as a freshly measured one -- but canonicalised first, so one
            // lens recorded under two TELESCOP spellings is one train here even though the store
            // faithfully kept both names. Display-time merge: see TelescopeAliases.
            var label = TelescopeAliases.CanonicalizeLabel(record.OpticalTrain);
            if (!_byTrain.TryGetValue(label, out var acc))
            {
                _byTrain[label] = acc = new TrainAcc(label, _radiusBins);
            }
            acc.RecordedAs.Add(record.OpticalTrain);

            acc.Sessions++;
            acc.Fwhm.AddRange(record.SubFwhm);
            acc.Hfd.AddRange(record.SubHfd);
            acc.Ecc.AddRange(record.SubEllipticity);
            acc.Subs += record.SubFwhm.Length;
            acc.Noise.Add(record.MasterNoiseRelative);
            for (var b = 0; b < _radiusBins; b++)
            {
                acc.BinFwhm[b].AddRange(record.Bins[b].Fwhm);
                acc.BinEcc[b].AddRange(record.Bins[b].Ellipticity);
                acc.StarsSampled += record.Bins[b].Fwhm.Length;
            }
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
                    RecordedAs: [.. acc.RecordedAs],
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
            /// <summary>Distinct header labels folded into this train (usually just one). A
            /// SortedSet so the rendered note is deterministic across runs, like everything else in
            /// this report.</summary>
            public readonly SortedSet<string> RecordedAs = new(StringComparer.Ordinal);
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
            // Only when an alias actually merged something: on the ordinary single-spelling train
            // this line would be noise repeating the heading.
            if (train.RecordedAs.Length > 1)
            {
                sb.AppendLine(string.Create(ci,
                    $"- Merged from {train.RecordedAs.Length} header spellings: {string.Join("; ", train.RecordedAs)}"));
            }
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

    /// <summary>MAD of the master's channel 0 divided by <see cref="Image.MaxValue"/>; a
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
