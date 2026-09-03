using Microsoft.Extensions.Logging;
using nom.tam.fits;
using nom.tam.util;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Lunar;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// The gradient-distribution report over the retained session masters (gradient-remover-training.md,
    /// G1): what the classical fit finds in real integrated frames, per master and per plane, joined to
    /// the geometry that is supposed to explain it. A sibling of <see cref="DatasetPsfNoiseReport"/>,
    /// rendered from an append-only store (<see cref="DatasetGradientStore"/>) for the same reasons.
    /// </summary>
    /// <remarks>
    /// <para><b>What is measured.</b> Each master is fitted with <see cref="ClassicalBackgroundExtractor"/>
    /// at its defaults. Per plane: the model's peak-to-peak over the frame, in units of the frame's own
    /// per-pixel background sigma (robust sigma of source minus model) and of the level; the polynomial's
    /// linear share, the direction its linear term brightens in, and the sign and size of its curvature
    /// (<see cref="DescribeShape"/>). Per master: the target's altitude, azimuth, airmass and parallactic
    /// angle at the master's epoch, the Moon's altitude, illumination and separation, and, when the
    /// master plate-solves, the pixel-frame direction of the horizon and of the Moon, so the brightening
    /// direction can be compared with the two directions sky glow is expected to come from.</para>
    /// <para><b>The canvas ring is masked, not fitted.</b> A TianWen master is written on the
    /// registration canvas and the integrator writes exact 0 where no frame covered it. That ring is
    /// 1 to 2 percent of the frame and sits far below the sky, so a fit that saw it would read it as a
    /// dark plateau at the edge; <see cref="MaskAbsent"/> turns exact zeros into NaN first, which the
    /// extractor already treats as absent.</para>
    /// <para><b>Epoch caveat.</b> A master carries one <c>DATE-OBS</c>, the reference sub's, while the
    /// integration spans a session; the parallactic angle at that instant stands in for the
    /// exposure-weighted one. The sub epochs are not on the master, so the report says so rather than
    /// pretending to a precision it does not have.</para>
    /// <para><b>The threshold sweep</b> re-runs the fit at the settings the two reasoned defaults
    /// (<see cref="BackgroundExtractionOptions.StructureThresholdSigma"/>,
    /// <see cref="BackgroundExtractionOptions.SurfaceStructureThresholdSigma"/>) could have had, and
    /// records how far each moves the model from the default's, in sigma. A default is well placed where
    /// the model stops moving; with no ground truth on a real master that is the measurement available.</para>
    /// </remarks>
    public static class DatasetGradientReport
    {
        /// <summary>Store file name under <c>&lt;outDir&gt;/stats</c>.</summary>
        public const string StoreFileName = "gradient-masters.jsonl";

        /// <summary>Rendered report under <c>&lt;outDir&gt;/stats</c>.</summary>
        public const string ReportFileName = "gradient-report.md";

        /// <summary>Structure thresholds (polynomial stage) the sweep tries beside the default 3.</summary>
        public static readonly ImmutableArray<float> StructureThresholdSweep = [2f, 4f, 6f];

        /// <summary>Surface structure thresholds the sweep tries, with the surface stage ON (default 10 among them).</summary>
        public static readonly ImmutableArray<float> SurfaceThresholdSweep = [5f, 10f, 20f, 40f];

        private const float MadToSigma = 1.4826f;

        /// <summary>
        /// One fitted plane of one master.
        /// </summary>
        /// <param name="Plane">Channel index.</param>
        /// <param name="Level">The fit's level (median of the model), image units.</param>
        /// <param name="BackgroundSigma">Robust per-pixel sigma (1.4826 x MAD) of source minus model over the
        /// finite pixels, image units: the frame's own noise floor, stars included but outvoted.</param>
        /// <param name="PeakToPeak">Range of the model over the finite pixels, image units.</param>
        /// <param name="PeakToPeakSigma"><paramref name="PeakToPeak"/> / <paramref name="BackgroundSigma"/>.</param>
        /// <param name="PeakToPeakRelative"><paramref name="PeakToPeak"/> / <paramref name="Level"/>.</param>
        /// <param name="LinearShare">Range of the polynomial's linear part over that of linear plus quadratic, 0..1
        /// (1 = a pure ramp).</param>
        /// <param name="GradientAngleDeg">Direction the linear term BRIGHTENS in, pixel frame: 0 along +X (columns),
        /// 90 along +Y (rows, down a top-down frame). NaN for a flat plane.</param>
        /// <param name="CurvatureMajorSigma">Hessian eigenvalue of larger magnitude over sigma (negative = dome,
        /// positive = bowl), image units per normalised unit squared.</param>
        /// <param name="CurvatureMinorSigma">The other eigenvalue over sigma.</param>
        /// <param name="Shape">Flat / Ramp / Dome / Bowl / Saddle (<see cref="DescribeShape"/>).</param>
        /// <param name="KeptFraction">Fraction of the valid working pixels the final fit used.</param>
        /// <param name="Iterations">Polynomial-stage iterations run.</param>
        /// <param name="Converged">Whether the kept fraction settled before the cap.</param>
        /// <param name="Coefficients">The fit's coefficients (<see cref="BackgroundPolynomial"/> order, image units).</param>
        public sealed record PlaneGradient(
            int Plane,
            float Level,
            float BackgroundSigma,
            float PeakToPeak,
            float PeakToPeakSigma,
            float PeakToPeakRelative,
            float LinearShare,
            float GradientAngleDeg,
            float CurvatureMajorSigma,
            float CurvatureMinorSigma,
            string Shape,
            float KeptFraction,
            int Iterations,
            bool Converged,
            ImmutableArray<double> Coefficients);

        /// <summary>
        /// One setting of the threshold sweep on one master, averaged over its planes and expressed in each
        /// plane's own background sigma.
        /// </summary>
        /// <param name="Parameter">Option name; <c>ProtectStructure</c> with value 0 is the structure mask OFF.</param>
        /// <param name="Value">The setting.</param>
        /// <param name="KeptFraction">Kept fraction at that setting.</param>
        /// <param name="PeakToPeakSigma">The variant model's range over sigma.</param>
        /// <param name="DeltaRmsSigma">RMS of (variant model minus default model) over sigma: how far the setting moves the answer.</param>
        /// <param name="DeltaPeakToPeakSigma">Range of that difference over sigma: the worst it moves anywhere.</param>
        public sealed record SweepPoint(string Parameter, float Value, float KeptFraction, float PeakToPeakSigma, float DeltaRmsSigma, float DeltaPeakToPeakSigma);

        /// <summary>
        /// One master's record: the fit per plane, the sweep, and the covariates at the master's epoch.
        /// Angles "in frame" share <see cref="PlaneGradient.GradientAngleDeg"/>'s convention and are NaN
        /// when the master did not plate-solve.
        /// </summary>
        public sealed record MasterGradient(
            string Master,
            string Camera,
            string Telescope,
            string Filter,
            string ObjectName,
            string Strategy,
            int StackedFrames,
            int Width,
            int Height,
            int Channels,
            float AbsentFraction,
            DateTimeOffset Epoch,
            float SiteLatitude,
            float SiteLongitude,
            double RaHours,
            double DecDeg,
            bool Solved,
            double AltitudeDeg,
            double AzimuthDeg,
            double Airmass,
            double ParallacticAngleDeg,
            double HorizonAngleInFrameDeg,
            double MoonAltitudeDeg,
            double MoonIllumination,
            double MoonSeparationDeg,
            double MoonAngleInFrameDeg,
            ImmutableArray<PlaneGradient> Planes,
            ImmutableArray<SweepPoint> Sweep,
            long ElapsedMs)
        {
            /// <summary>Circular mean of the planes' brightening directions, degrees; NaN when no plane has one.</summary>
            public double BrighteningAngleDeg => CircularMeanDeg(Planes.Select(p => (double)p.GradientAngleDeg));

            /// <summary>Mean over planes of the model range in sigma.</summary>
            public double MeanPeakToPeakSigma => MeanFinite(Planes.Select(p => (double)p.PeakToPeakSigma));

            /// <summary>Mean over planes of the model range relative to the level.</summary>
            public double MeanPeakToPeakRelative => MeanFinite(Planes.Select(p => (double)p.PeakToPeakRelative));

            /// <summary>Mean over planes of the kept fraction.</summary>
            public double MeanKeptFraction => MeanFinite(Planes.Select(p => (double)p.KeptFraction));

            /// <summary>Signed angle from the horizon direction to the brightening direction, (-180, 180]; NaN when unsolved.</summary>
            public double BrighteningMinusHorizonDeg => SignedDelta(BrighteningAngleDeg, HorizonAngleInFrameDeg);

            /// <summary>Signed angle from the Moon's direction to the brightening direction, (-180, 180]; NaN when unsolved or the Moon is unknown.</summary>
            public double BrighteningMinusMoonDeg => SignedDelta(BrighteningAngleDeg, MoonAngleInFrameDeg);

            /// <summary>Whether the Moon was above the horizon at the epoch.</summary>
            public bool MoonUp => MoonAltitudeDeg > 0;

            /// <summary>The most common plane shape.</summary>
            public string DominantShape => Planes.IsDefaultOrEmpty
                ? "Unfitted"
                : Planes.GroupBy(p => p.Shape).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
        }

        /// <summary>What the polynomial's coefficients say about the surface's shape, in image units.</summary>
        public readonly record struct ShapeDescriptor(
            float LinearPeakToPeak, float QuadraticPeakToPeak, float LinearShare, float GradientAngleDeg,
            float CurvatureMajor, float CurvatureMinor, string Shape);

        /// <summary>Inputs of a report run.</summary>
        /// <param name="MasterFiles">The master FITS files to measure.</param>
        /// <param name="OutputDir">Root under which <c>stats/</c> receives the store and the report.</param>
        /// <param name="Sweep">Run the threshold sweep (eight extra fits per master).</param>
        /// <param name="Solve">Plate-solve each master for the frame's orientation (needs a solver).</param>
        /// <param name="Force">Re-measure masters already in the store instead of skipping them.</param>
        public sealed record RunOptions(ImmutableArray<string> MasterFiles, string OutputDir, bool Sweep = true, bool Solve = true, bool Force = false);

        /// <summary>Outcome of a report run.</summary>
        public sealed record RunResult(int Measured, int Skipped, int Failed, int Solved, string StorePath, string ReportPath);

        /// <summary>
        /// Measures every master in <paramref name="options"/> that is not already in the store, appending
        /// each record as it completes and re-rendering the report after each, so a killed run keeps what
        /// it finished. Faults are isolated per master.
        /// </summary>
        public static async Task<RunResult> RunAsync(
            RunOptions options, IPlateSolver? solver, ILogger? logger = null, IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var statsDir = Path.Combine(options.OutputDir, "stats");
            Directory.CreateDirectory(statsDir);
            var storePath = Path.Combine(statsDir, StoreFileName);
            var reportPath = Path.Combine(statsDir, ReportFileName);
            var store = await DatasetGradientStore.ReadAsync(storePath, logger, cancellationToken);

            var files = options.MasterFiles.Sort(StringComparer.OrdinalIgnoreCase);
            var measured = 0;
            var skipped = 0;
            var failed = 0;
            var solved = 0;
            var index = 0;
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                var key = Path.GetFileName(path);
                if (!options.Force && store.ContainsKey(key))
                {
                    skipped++;
                    progress?.Report($"[gradient] {index}/{files.Length} in store, skipped: {key}");
                    continue;
                }

                try
                {
                    if (!Image.TryReadFitsFile(path, out var image, out _))
                    {
                        failed++;
                        progress?.Report($"[gradient] {index}/{files.Length} UNREADABLE: {key}");
                        continue;
                    }

                    MasterGradient record;
                    try
                    {
                        var (strategy, stackedFrames) = ReadMasterCards(path);
                        record = await MeasureMasterAsync(image, path, strategy, stackedFrames, options.Solve ? solver : null, options.Sweep, logger, cancellationToken);
                    }
                    finally
                    {
                        image.Release();
                    }

                    await DatasetGradientStore.AppendAsync(storePath, record, cancellationToken);
                    store[key] = record;
                    measured++;
                    if (record.Solved)
                    {
                        solved++;
                    }
                    progress?.Report(string.Create(CultureInfo.InvariantCulture,
                        $"[gradient] {index}/{files.Length} {key}: p-p {record.MeanPeakToPeakSigma:F1} sigma ({record.MeanPeakToPeakRelative:P1} of level), {record.DominantShape}, brightening {record.BrighteningAngleDeg:F0} deg, kept {record.MeanKeptFraction:F2}, alt {record.AltitudeDeg:F0}, moon {record.MoonAltitudeDeg:F0} deg / {record.MoonIllumination:P0}, {(record.Solved ? "solved" : "unsolved")}, {record.ElapsedMs} ms"));
                    await WriteMarkdownAsync(store.Values, reportPath, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Fault-isolated per master, as the dataset bake is per session: one unreadable or
                    // pathological file must not cost the other hundred their report.
                    failed++;
                    logger?.LogError(ex, "Gradient report: {Master} failed", key);
                    progress?.Report($"[gradient] {index}/{files.Length} FAILED {key}: {ex.Message}");
                }
            }

            if (measured == 0 && store.Count > 0)
            {
                // A run that only skipped still leaves a report that matches the store.
                await WriteMarkdownAsync(store.Values, reportPath, cancellationToken);
            }

            return new RunResult(measured, skipped, failed, solved, storePath, reportPath);
        }

        /// <summary>
        /// Measures one master: the default fit per plane, the sweep, and the covariates. The caller keeps
        /// ownership of <paramref name="master"/>; <paramref name="masterPath"/> is only read when
        /// <paramref name="solver"/> is given (the solver works from the file).
        /// </summary>
        public static async Task<MasterGradient> MeasureMasterAsync(
            Image master, string masterPath, string strategy, int stackedFrames,
            IPlateSolver? solver, bool sweep, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(master);
            var sw = Stopwatch.StartNew();
            var meta = master.ImageMeta;
            var (channels, width, height) = master.Shape;

            var masked = MaskAbsent(master, out var absentFraction);
            var extractor = new ClassicalBackgroundExtractor();
            var defaults = BackgroundExtractionOptions.Default;

            ImmutableArray<PlaneGradient> planes;
            ImmutableArray<SweepPoint> sweepPoints;
            var baseline = await extractor.ExtractAsync(masked, defaults, cancellationToken);
            try
            {
                if (baseline.Planes.Length != channels)
                {
                    throw new NotSupportedException(
                        $"the gradient report expects a debayered or mono master (one fitted plane per channel), got {baseline.Planes.Length} planes for {channels} channel(s)");
                }

                var builder = ImmutableArray.CreateBuilder<PlaneGradient>(channels);
                for (var p = 0; p < channels; p++)
                {
                    builder.Add(MeasurePlane(p, masked.GetChannelSpan(p), baseline.Background.GetChannelSpan(p), width, height, baseline.Planes[p]));
                }
                planes = builder.MoveToImmutable();

                sweepPoints = sweep
                    ? await SweepAsync(extractor, masked, baseline.Background, planes, defaults, cancellationToken)
                    : ImmutableArray<SweepPoint>.Empty;
            }
            finally
            {
                baseline.Cleaned.Release();
                baseline.Background.Release();
                masked.Release();
            }

            // Geometry. The solve gives the frame's orientation on the sky and a better centre than the
            // header; without one the direction covariates stay NaN and the altitude ones use the header.
            WCS? wcs = null;
            if (solver is not null && File.Exists(masterPath))
            {
                var hint = double.IsFinite(meta.TargetRA) && double.IsFinite(meta.TargetDec) ? new WCS(meta.TargetRA, meta.TargetDec) : null as WCS?;
                try
                {
                    var solve = await solver.SolveFileAsync(masterPath, master.GetImageDim(hint), searchOrigin: hint,
                        searchRadius: hint is null ? null : 5.0, cancellationToken: cancellationToken);
                    wcs = solve.Solution;
                }
                catch (PlateSolverException ex)
                {
                    logger?.LogWarning(ex, "Gradient report: {Master} did not plate-solve; frame directions stay unknown", Path.GetFileName(masterPath));
                }
            }
            var solved = wcs is { HasCDMatrix: true };
            var raHours = solved ? wcs!.Value.CenterRA : meta.TargetRA;
            var decDeg = solved ? wcs!.Value.CenterDec : meta.TargetDec;

            var epoch = meta.ExposureStartTime;
            double lat = meta.Latitude;
            double lon = meta.Longitude;
            var hasEpoch = epoch.Year >= 1900;
            var lst = hasEpoch ? SiteContext.ComputeLST(epoch, lon) : double.NaN;
            var ha = CoordinateUtils.ConditionHA(lst - raHours);
            var alt = SiteContext.AltitudeDegrees(lat, ha, decDeg);
            var az = SiteContext.AzimuthDegrees(lat, ha, decDeg);
            var airmass = double.IsNaN(alt) ? double.NaN : Session.AirmassFromAltitude(alt);
            var parallactic = double.IsNaN(ha) || double.IsNaN(decDeg) || double.IsNaN(lat) ? double.NaN : CoordinateUtils.ParallacticAngleDeg(ha, decDeg, lat);
            // The zenith is at the parallactic angle; the horizon (where sky glow comes from) is opposite.
            var horizonInFrame = solved && !double.IsNaN(parallactic) ? wcs!.Value.SkyPositionAngleToPixelAngleDeg(parallactic + 180.0) : double.NaN;

            var moonAlt = double.NaN;
            var moonIllumination = double.NaN;
            var moonSeparation = double.NaN;
            var moonInFrame = double.NaN;
            if (hasEpoch && !double.IsNaN(lat) && !double.IsNaN(lon)
                && VSOP87a.Reduce(CatalogIndex.Moon, epoch, lat, lon, out var moonRa, out var moonDec, out _, out var moonAltitude, out _))
            {
                moonAlt = moonAltitude;
                moonIllumination = MeeusMoon.GetPhase(epoch.ToJulian()).Illumination;
                if (double.IsFinite(raHours) && double.IsFinite(decDeg))
                {
                    moonSeparation = CoordinateUtils.AngularSeparationDeg(raHours, decDeg, moonRa, moonDec);
                    if (solved)
                    {
                        moonInFrame = wcs!.Value.SkyPositionAngleToPixelAngleDeg(CoordinateUtils.PositionAngleDeg(raHours, decDeg, moonRa, moonDec));
                    }
                }
            }

            return new MasterGradient(
                Path.GetFileName(masterPath),
                meta.Instrument ?? "",
                meta.Telescope ?? "",
                // The name the header carried, not the catalogue's classification of it: an unrecognised
                // filter reads "Unknown" through Name while its FITS name is the fact.
                meta.Filter.FilterNameForFits ?? "",
                meta.ObjectName ?? "",
                strategy ?? "",
                stackedFrames,
                width, height, channels, absentFraction,
                epoch, meta.Latitude, meta.Longitude,
                raHours, decDeg, solved,
                alt, az, airmass, parallactic, horizonInFrame,
                moonAlt, moonIllumination, moonSeparation, moonInFrame,
                planes, sweepPoints, sw.ElapsedMilliseconds);
        }

        /// <summary>
        /// A copy of <paramref name="source"/> with every exact-zero or non-finite pixel set to NaN, which the
        /// extractor treats as absent. The integrator writes 0 where no frame covered the canvas.
        /// </summary>
        internal static Image MaskAbsent(Image source, out float absentFraction)
        {
            var (channels, width, height) = source.Shape;
            var n = width * height;
            var data = Image.CreateChannelData(channels, height, width);
            long absent = 0;
            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            for (var c = 0; c < channels; c++)
            {
                var src = source.GetChannelSpan(c);
                var dst = MemoryMarshal.CreateSpan(ref data[c][0, 0], n);
                for (var i = 0; i < n; i++)
                {
                    var v = src[i];
                    if (v == 0f || !float.IsFinite(v))
                    {
                        dst[i] = float.NaN;
                        absent++;
                    }
                    else
                    {
                        dst[i] = v;
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }
            }
            if (min > max)
            {
                min = max = 0f;
            }
            absentFraction = (float)((double)absent / ((long)channels * n));
            return new Image(data, BitDepth.Float32, max, min, source.Pedestal, source.ImageMeta);
        }

        /// <summary>Per-plane measurement against the default fit's model.</summary>
        internal static PlaneGradient MeasurePlane(int plane, ReadOnlySpan<float> source, ReadOnlySpan<float> model, int width, int height, ChannelFitDiagnostics diag)
        {
            var (sigma, lo, hi) = ResidualSigmaAndRange(source, model);
            var peakToPeak = hi > lo ? hi - lo : 0f;
            var shape = DescribeShape(diag.Coefficients.IsDefault ? ReadOnlySpan<double>.Empty : diag.Coefficients.AsSpan(), width, height, sigma);
            return new PlaneGradient(
                plane, diag.Level, sigma, peakToPeak,
                Ratio(peakToPeak, sigma), Ratio(peakToPeak, diag.Level),
                shape.LinearShare, shape.GradientAngleDeg,
                Ratio(shape.CurvatureMajor, sigma), Ratio(shape.CurvatureMinor, sigma),
                shape.Shape, diag.KeptFraction, diag.Iterations, diag.Converged,
                diag.Coefficients.IsDefault ? ImmutableArray<double>.Empty : diag.Coefficients);
        }

        /// <summary>
        /// Reads the polynomial's coefficients (<see cref="BackgroundPolynomial"/> order) as a shape: the
        /// linear part's range over the normalised square, the quadratic part's, their share, the direction
        /// the linear term brightens in (pixel frame, using the plane's width and height to turn the two
        /// normalised slopes into one per-pixel vector), and the Hessian's eigenvalues. The label is
        /// <c>Flat</c> when the whole shape is under half a sigma, <c>Ramp</c> when the quadratic range is
        /// under a quarter of the linear one, else <c>Dome</c> / <c>Bowl</c> / <c>Saddle</c> by the
        /// eigenvalue signs. Degrees above 2 contribute to the model but not to this description.
        /// </summary>
        public static ShapeDescriptor DescribeShape(ReadOnlySpan<double> coefficients, int width, int height, float sigma)
        {
            if (coefficients.Length < 3)
            {
                return new ShapeDescriptor(0f, 0f, 1f, float.NaN, 0f, 0f, coefficients.IsEmpty ? "Unfitted" : "Flat");
            }

            var c10 = coefficients[1];
            var c01 = coefficients[2];
            var gx = width > 1 ? c10 * 2.0 / (width - 1) : 0.0;
            var gy = height > 1 ? c01 * 2.0 / (height - 1) : 0.0;
            var angle = gx == 0.0 && gy == 0.0
                ? float.NaN
                : (float)CoordinateUtils.ConditionDegrees(double.RadiansToDegrees(Math.Atan2(gy, gx)));
            var linearPeakToPeak = 2.0 * (Math.Abs(c10) + Math.Abs(c01));

            var quadraticPeakToPeak = 0.0;
            var major = 0.0;
            var minor = 0.0;
            if (coefficients.Length >= 6)
            {
                var c20 = coefficients[3];
                var c11 = coefficients[4];
                var c02 = coefficients[5];
                var qLo = double.PositiveInfinity;
                var qHi = double.NegativeInfinity;
                const int Grid = 32;
                for (var j = 0; j <= Grid; j++)
                {
                    var yn = j * 2.0 / Grid - 1.0;
                    for (var i = 0; i <= Grid; i++)
                    {
                        var xn = i * 2.0 / Grid - 1.0;
                        var q = c20 * xn * xn + c11 * xn * yn + c02 * yn * yn;
                        qLo = Math.Min(qLo, q);
                        qHi = Math.Max(qHi, q);
                    }
                }
                quadraticPeakToPeak = qHi - qLo;

                var halfTrace = c20 + c02;
                var det = 4.0 * c20 * c02 - c11 * c11;
                var disc = Math.Sqrt(Math.Max(0.0, halfTrace * halfTrace - det));
                var l1 = halfTrace + disc;
                var l2 = halfTrace - disc;
                (major, minor) = Math.Abs(l1) >= Math.Abs(l2) ? (l1, l2) : (l2, l1);
            }

            var total = linearPeakToPeak + quadraticPeakToPeak;
            var share = total > 0.0 ? (float)(linearPeakToPeak / total) : 1f;
            string shape;
            if (sigma > 0f && total < 0.5 * sigma)
            {
                shape = "Flat";
            }
            else if (quadraticPeakToPeak < 0.25 * linearPeakToPeak)
            {
                shape = "Ramp";
            }
            else if (major < 0.0 && minor < 0.0)
            {
                shape = "Dome";
            }
            else if (major > 0.0 && minor > 0.0)
            {
                shape = "Bowl";
            }
            else
            {
                shape = "Saddle";
            }

            return new ShapeDescriptor((float)linearPeakToPeak, (float)quadraticPeakToPeak, share, angle, (float)major, (float)minor, shape);
        }

        private static async Task<ImmutableArray<SweepPoint>> SweepAsync(
            ClassicalBackgroundExtractor extractor, Image masked, Image baselineBackground, ImmutableArray<PlaneGradient> baselinePlanes,
            BackgroundExtractionOptions defaults, CancellationToken cancellationToken)
        {
            var points = ImmutableArray.CreateBuilder<SweepPoint>(StructureThresholdSweep.Length + 1 + SurfaceThresholdSweep.Length);
            foreach (var t in StructureThresholdSweep)
            {
                points.Add(await MeasureVariantAsync(extractor, masked, baselineBackground, baselinePlanes,
                    nameof(BackgroundExtractionOptions.StructureThresholdSigma), t, defaults with { StructureThresholdSigma = t }, cancellationToken));
            }
            points.Add(await MeasureVariantAsync(extractor, masked, baselineBackground, baselinePlanes,
                nameof(BackgroundExtractionOptions.ProtectStructure), 0f, defaults with { ProtectStructure = false }, cancellationToken));
            foreach (var t in SurfaceThresholdSweep)
            {
                points.Add(await MeasureVariantAsync(extractor, masked, baselineBackground, baselinePlanes,
                    nameof(BackgroundExtractionOptions.SurfaceStructureThresholdSigma), t,
                    defaults with { SurfaceRefinement = true, SurfaceStructureThresholdSigma = t }, cancellationToken));
            }
            return points.MoveToImmutable();
        }

        private static async Task<SweepPoint> MeasureVariantAsync(
            ClassicalBackgroundExtractor extractor, Image masked, Image baselineBackground, ImmutableArray<PlaneGradient> baselinePlanes,
            string parameter, float value, BackgroundExtractionOptions options, CancellationToken cancellationToken)
        {
            var result = await extractor.ExtractAsync(masked, options, cancellationToken);
            try
            {
                var channels = baselinePlanes.Length;
                var kept = 0.0;
                var peakToPeak = 0.0;
                var deltaRms = 0.0;
                var deltaPeakToPeak = 0.0;
                var counted = 0;
                for (var p = 0; p < channels; p++)
                {
                    var sigma = baselinePlanes[p].BackgroundSigma;
                    if (!(sigma > 0f))
                    {
                        continue;
                    }
                    var source = masked.GetChannelSpan(p);
                    var variant = result.Background.GetChannelSpan(p);
                    var baseline = baselineBackground.GetChannelSpan(p);
                    var vLo = float.PositiveInfinity;
                    var vHi = float.NegativeInfinity;
                    var dLo = float.PositiveInfinity;
                    var dHi = float.NegativeInfinity;
                    var sumSq = 0.0;
                    var count = 0;
                    for (var i = 0; i < source.Length; i++)
                    {
                        if (!float.IsFinite(source[i]))
                        {
                            continue;
                        }
                        var v = variant[i];
                        var d = v - baseline[i];
                        vLo = Math.Min(vLo, v);
                        vHi = Math.Max(vHi, v);
                        dLo = Math.Min(dLo, d);
                        dHi = Math.Max(dHi, d);
                        sumSq += (double)d * d;
                        count++;
                    }
                    if (count == 0)
                    {
                        continue;
                    }
                    kept += result.Planes[p].KeptFraction;
                    peakToPeak += (vHi - vLo) / sigma;
                    deltaRms += Math.Sqrt(sumSq / count) / sigma;
                    deltaPeakToPeak += (dHi - dLo) / sigma;
                    counted++;
                }
                if (counted == 0)
                {
                    return new SweepPoint(parameter, value, float.NaN, float.NaN, float.NaN, float.NaN);
                }
                return new SweepPoint(parameter, value, (float)(kept / counted), (float)(peakToPeak / counted), (float)(deltaRms / counted), (float)(deltaPeakToPeak / counted));
            }
            finally
            {
                result.Cleaned.Release();
                result.Background.Release();
            }
        }

        private static (float Sigma, float ModelMin, float ModelMax) ResidualSigmaAndRange(ReadOnlySpan<float> source, ReadOnlySpan<float> model)
        {
            var buffer = new float[source.Length];
            var m = 0;
            var lo = float.PositiveInfinity;
            var hi = float.NegativeInfinity;
            for (var i = 0; i < source.Length; i++)
            {
                var s = source[i];
                if (!float.IsFinite(s))
                {
                    continue;
                }
                var b = model[i];
                buffer[m++] = s - b;
                lo = Math.Min(lo, b);
                hi = Math.Max(hi, b);
            }
            if (m == 0)
            {
                return (0f, 0f, 0f);
            }
            var (_, mad) = StatisticsHelper.MedianAndMad(buffer.AsSpan(0, m));
            return (MadToSigma * mad, lo, hi);
        }

        /// <summary>The integration cards the master carries about itself: <c>STRATEGY</c> and <c>STACK_N</c>.</summary>
        internal static (string Strategy, int StackedFrames) ReadMasterCards(string path)
        {
            using var reader = new BufferedFile(path, FileAccess.Read, FileShare.Read, 4 * 2880);
            using var fits = new Fits(reader, path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
            var header = fits.ReadFirstImageHduHeaderOnly()?.Header;
            if (header is null)
            {
                return ("", 0);
            }
            return (header.GetStringValue("STRATEGY") ?? "", header.GetIntValue("STACK_N", 0));
        }

        /// <summary>Renders the report from every record, atomically (write beside, then move over).</summary>
        public static async Task WriteMarkdownAsync(IEnumerable<MasterGradient> masters, string path, CancellationToken cancellationToken = default)
        {
            var text = RenderMarkdown(masters);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, text, cancellationToken);
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>The report as Markdown.</summary>
        public static string RenderMarkdown(IEnumerable<MasterGradient> masters)
        {
            var ci = CultureInfo.InvariantCulture;
            var list = masters.OrderBy(m => m.Master, StringComparer.OrdinalIgnoreCase).ToList();
            var planes = list.SelectMany(m => m.Planes).ToList();
            var solved = list.Where(m => m.Solved).ToList();
            var moonUp = solved.Where(m => m.MoonUp && !double.IsNaN(m.MoonAngleInFrameDeg)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("# Dataset Gradient Distribution Report");
            sb.AppendLine();
            sb.AppendLine(string.Create(ci, $"- Masters: {list.Count} ({solved.Count} plate-solved, {list.Count(m => m.MoonUp)} with the Moon up at the epoch)"));
            sb.AppendLine(string.Create(ci, $"- Planes fitted: {planes.Count}"));
            sb.AppendLine(string.Create(ci, $"- Canvas ring masked as absent (exact-zero pixels): {Pct(list.Select(m => (double)m.AbsentFraction)).P50:P1} of a frame at the median"));
            sb.AppendLine("- Fit: ClassicalBackgroundExtractor defaults (degree 2 on a 1/4 block mean, 2/4 sigma rejection, structure protection at 3 sigma, surface stage OFF)");
            sb.AppendLine("- Units: \"sigma\" is each plane's own per-pixel background sigma (1.4826 x MAD of source minus model); \"level\" is the fit's median");
            sb.AppendLine("- Epoch caveat: covariates are evaluated at the master's DATE-OBS, which is the REFERENCE sub's start, not an exposure-weighted mid-session instant. Over a multi-hour session the parallactic angle turns by tens of degrees, so the horizon direction below carries that much uncertainty");
            sb.AppendLine();

            sb.AppendLine("## Amplitude (per plane, all masters)");
            sb.AppendLine();
            sb.AppendLine("| Metric | p5 | p25 | p50 | p75 | p95 |");
            sb.AppendLine("|--------|----|-----|-----|-----|-----|");
            AppendPct(sb, ci, "Model peak-to-peak / sigma", Pct(planes.Select(p => (double)p.PeakToPeakSigma)));
            AppendPct(sb, ci, "Model peak-to-peak / level", Pct(planes.Select(p => (double)p.PeakToPeakRelative)), "F4");
            AppendPct(sb, ci, "Linear share (0..1)", Pct(planes.Select(p => (double)p.LinearShare)));
            AppendPct(sb, ci, "|Curvature| major / sigma", Pct(planes.Select(p => (double)Math.Abs(p.CurvatureMajorSigma))));
            AppendPct(sb, ci, "Kept fraction", Pct(planes.Select(p => (double)p.KeptFraction)));
            AppendPct(sb, ci, "Iterations", Pct(planes.Select(p => (double)p.Iterations)), "F0");
            sb.AppendLine();
            var shapes = planes.GroupBy(p => p.Shape).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => string.Create(ci, $"{g.Key} {g.Count()}"));
            sb.AppendLine(string.Create(ci, $"Shape census (planes): {string.Join(", ", shapes)}."));
            sb.AppendLine(string.Create(ci, $"Planes with a dome (negative major curvature) at more than a quarter of the linear range: {planes.Count(p => p.Shape == "Dome")} of {planes.Count}."));
            sb.AppendLine();

            sb.AppendLine("## Direction (plate-solved masters)");
            sb.AppendLine();
            sb.AppendLine("Angles are in the pixel frame (0 = +X along the columns, 90 = +Y down the rows). \"Brightening\" is the direction the");
            sb.AppendLine("linear term increases in, circular mean over the planes. \"Horizon\" is the anti-zenith at the epoch, \"Moon\" the");
            sb.AppendLine("Moon's position angle from the field centre. |difference| is on 0..180; with no relation it is uniform there, so a");
            sb.AppendLine("quarter falls under 45 degrees by chance.");
            sb.AppendLine();
            var dHorizon = solved.Select(m => Math.Abs(m.BrighteningMinusHorizonDeg)).Where(double.IsFinite).ToList();
            var dMoon = moonUp.Select(m => Math.Abs(m.BrighteningMinusMoonDeg)).Where(double.IsFinite).ToList();
            sb.AppendLine("| Metric | Masters | p5 | p25 | p50 | p75 | p95 | within 45 deg |");
            sb.AppendLine("|--------|---------|----|-----|-----|-----|-----|---------------|");
            AppendDirectionRow(sb, ci, "|brightening - horizon|", dHorizon);
            AppendDirectionRow(sb, ci, "|brightening - Moon| (Moon up)", dMoon);
            sb.AppendLine();
            sb.AppendLine("| 30-degree bin | brightening - horizon | brightening - Moon (Moon up) |");
            sb.AppendLine("|---------------|-----------------------|------------------------------|");
            for (var bin = 0; bin < 6; bin++)
            {
                var lo = bin * 30;
                var hi = lo + 30;
                sb.AppendLine(string.Create(ci,
                    $"| {lo}-{hi} | {dHorizon.Count(d => d >= lo && (d < hi || (bin == 5 && d <= hi)))} | {dMoon.Count(d => d >= lo && (d < hi || (bin == 5 && d <= hi)))} |"));
            }
            sb.AppendLine();

            AppendGroupTable(sb, ci, "## By filter", "Filter", list, m => m.Filter.Length > 0 ? m.Filter : "(no filter recorded)");
            AppendGroupTable(sb, ci, "## By camera", "Camera", list, m => m.Camera.Length > 0 ? m.Camera : "(no camera recorded)");

            sb.AppendLine("## Threshold sensitivity (the two reasoned defaults)");
            sb.AppendLine();
            sb.AppendLine("Each row re-runs the fit at that setting on every master and measures how far its model moves from the default's,");
            sb.AppendLine("in sigma (RMS over the frame, and the largest difference anywhere). A default is well placed where the model has");
            sb.AppendLine("stopped moving; a row that moves the model by more than a sigma somewhere is a setting the data can tell apart.");
            sb.AppendLine();
            var swept = list.Where(m => !m.Sweep.IsDefaultOrEmpty).ToList();
            sb.AppendLine(string.Create(ci, $"Masters swept: {swept.Count} of {list.Count}."));
            sb.AppendLine();
            sb.AppendLine("### StructureThresholdSigma (polynomial stage; default 3)");
            sb.AppendLine();
            sb.AppendLine("| Setting | Kept p50 | p-p / sigma p50 | delta RMS p50 | p95 | delta p-p p50 | p95 |");
            sb.AppendLine("|---------|----------|-----------------|---------------|-----|---------------|-----|");
            foreach (var t in StructureThresholdSweep.Where(t => t < 3f))
            {
                AppendSweepRow(sb, ci, swept, nameof(BackgroundExtractionOptions.StructureThresholdSigma), t, t.ToString(ci));
            }
            sb.AppendLine(string.Create(ci,
                $"| 3 (default) | {Pct(planes.Select(p => (double)p.KeptFraction)).P50:F3} | {Pct(planes.Select(p => (double)p.PeakToPeakSigma)).P50:F2} | 0 | 0 | 0 | 0 |"));
            foreach (var t in StructureThresholdSweep.Where(t => t > 3f))
            {
                AppendSweepRow(sb, ci, swept, nameof(BackgroundExtractionOptions.StructureThresholdSigma), t, t.ToString(ci));
            }
            AppendSweepRow(sb, ci, swept, nameof(BackgroundExtractionOptions.ProtectStructure), 0f, "off (no structure mask)");
            sb.AppendLine();
            sb.AppendLine("### SurfaceStructureThresholdSigma with the surface stage ON (default 10; the surface itself is OFF by default)");
            sb.AppendLine();
            sb.AppendLine("Deltas are against the polynomial-only default, so the 10 row IS what switching SurfaceRefinement on adds.");
            sb.AppendLine();
            sb.AppendLine("| Setting | Kept p50 | p-p / sigma p50 | delta RMS p50 | p95 | delta p-p p50 | p95 |");
            sb.AppendLine("|---------|----------|-----------------|---------------|-----|---------------|-----|");
            foreach (var t in SurfaceThresholdSweep)
            {
                AppendSweepRow(sb, ci, swept, nameof(BackgroundExtractionOptions.SurfaceStructureThresholdSigma), t, t == 10f ? "10 (default)" : t.ToString(ci));
            }
            sb.AppendLine();

            sb.AppendLine("## Per master");
            sb.AppendLine();
            sb.AppendLine("Sorted by amplitude. Alt is the field's altitude at the epoch; d-horizon and d-Moon are the signed angles from the");
            sb.AppendLine("horizon and Moon directions to the brightening direction (n/a when unsolved or the Moon is down).");
            sb.AppendLine();
            sb.AppendLine("| Master | Camera | Filter | Object | N | Alt | Moon alt / illum | p-p / sigma | p-p / level | Shape | Brightening | d-horizon | d-Moon | Kept |");
            sb.AppendLine("|--------|--------|--------|--------|---|-----|------------------|-------------|-------------|-------|-------------|-----------|--------|------|");
            foreach (var m in list.OrderByDescending(m => double.IsNaN(m.MeanPeakToPeakSigma) ? double.NegativeInfinity : m.MeanPeakToPeakSigma))
            {
                var moon = double.IsNaN(m.MoonAltitudeDeg) ? "n/a" : string.Create(ci, $"{m.MoonAltitudeDeg:F0} / {m.MoonIllumination:P0}");
                var dMoonText = m.MoonUp ? Fmt(ci, m.BrighteningMinusMoonDeg, "F0") : "n/a";
                sb.AppendLine(string.Create(ci,
                    $"| {Shorten(Path.GetFileNameWithoutExtension(m.Master))} | {m.Camera} | {m.Filter} | {m.ObjectName} | {m.StackedFrames} | {Fmt(ci, m.AltitudeDeg, "F0")} | {moon} | {Fmt(ci, m.MeanPeakToPeakSigma, "F1")} | {Fmt(ci, m.MeanPeakToPeakRelative, "F4")} | {m.DominantShape} | {Fmt(ci, m.BrighteningAngleDeg, "F0")} | {Fmt(ci, m.BrighteningMinusHorizonDeg, "F0")} | {dMoonText} | {Fmt(ci, m.MeanKeptFraction, "F2")} |"));
            }
            sb.AppendLine();
            return sb.ToString();
        }

        private static void AppendGroupTable(StringBuilder sb, CultureInfo ci, string heading, string column, List<MasterGradient> list, Func<MasterGradient, string> key)
        {
            sb.AppendLine(heading);
            sb.AppendLine();
            sb.AppendLine(string.Create(ci, $"| {column} | Masters | Planes | p-p / sigma p50 | p95 | p-p / level p50 | Linear share p50 | Dome planes |"));
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var g in list.GroupBy(key).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var gp = g.SelectMany(m => m.Planes).ToList();
                var pp = Pct(gp.Select(p => (double)p.PeakToPeakSigma));
                sb.AppendLine(string.Create(ci,
                    $"| {g.Key} | {g.Count()} | {gp.Count} | {pp.P50:F2} | {pp.P95:F2} | {Pct(gp.Select(p => (double)p.PeakToPeakRelative)).P50:F4} | {Pct(gp.Select(p => (double)p.LinearShare)).P50:F3} | {gp.Count(p => p.Shape == "Dome")} |"));
            }
            sb.AppendLine();
        }

        private static void AppendSweepRow(StringBuilder sb, CultureInfo ci, List<MasterGradient> swept, string parameter, float value, string label)
        {
            var points = swept.SelectMany(m => m.Sweep).Where(p => p.Parameter == parameter && p.Value == value).ToList();
            if (points.Count == 0)
            {
                sb.AppendLine(string.Create(ci, $"| {label} | n/a | n/a | n/a | n/a | n/a | n/a |"));
                return;
            }
            var rms = Pct(points.Select(p => (double)p.DeltaRmsSigma));
            var pp = Pct(points.Select(p => (double)p.DeltaPeakToPeakSigma));
            sb.AppendLine(string.Create(ci,
                $"| {label} | {Pct(points.Select(p => (double)p.KeptFraction)).P50:F3} | {Pct(points.Select(p => (double)p.PeakToPeakSigma)).P50:F2} | {rms.P50:F2} | {rms.P95:F2} | {pp.P50:F2} | {pp.P95:F2} |"));
        }

        private static void AppendDirectionRow(StringBuilder sb, CultureInfo ci, string label, List<double> deltas)
        {
            if (deltas.Count == 0)
            {
                sb.AppendLine(string.Create(ci, $"| {label} | 0 | n/a | n/a | n/a | n/a | n/a | n/a |"));
                return;
            }
            var p = Pct(deltas);
            var within = (double)deltas.Count(d => d <= 45.0) / deltas.Count;
            sb.AppendLine(string.Create(ci, $"| {label} | {deltas.Count} | {p.P5:F0} | {p.P25:F0} | {p.P50:F0} | {p.P75:F0} | {p.P95:F0} | {within:P0} |"));
        }

        private static void AppendPct(StringBuilder sb, CultureInfo ci, string label, DatasetPsfNoiseReport.Percentiles p, string format = "F2") =>
            sb.AppendLine(string.Create(ci,
                $"| {label} | {p.P5.ToString(format, ci)} | {p.P25.ToString(format, ci)} | {p.P50.ToString(format, ci)} | {p.P75.ToString(format, ci)} | {p.P95.ToString(format, ci)} |"));

        private static DatasetPsfNoiseReport.Percentiles Pct(IEnumerable<double> values) =>
            DatasetPsfNoiseReport.PercentilesOf(values.Where(double.IsFinite).ToList());

        private static string Fmt(CultureInfo ci, double value, string format) => double.IsFinite(value) ? value.ToString(format, ci) : "n/a";

        // The tail of a master's name carries the object and filter, the head the session folder; keep both ends.
        private static string Shorten(string name) => name.Length <= 72 ? name : name[..34] + "..." + name[^35..];

        private static float Ratio(float numerator, float denominator) => denominator > 0f ? numerator / denominator : float.NaN;

        private static double MeanFinite(IEnumerable<double> values)
        {
            var sum = 0.0;
            var n = 0;
            foreach (var v in values)
            {
                if (double.IsFinite(v))
                {
                    sum += v;
                    n++;
                }
            }
            return n == 0 ? double.NaN : sum / n;
        }

        /// <summary>Circular mean of angles in degrees, [0, 360); NaN with no finite input.</summary>
        internal static double CircularMeanDeg(IEnumerable<double> anglesDeg)
        {
            var x = 0.0;
            var y = 0.0;
            var n = 0;
            foreach (var a in anglesDeg)
            {
                if (!double.IsFinite(a))
                {
                    continue;
                }
                var (s, c) = Math.SinCos(double.DegreesToRadians(a));
                x += c;
                y += s;
                n++;
            }
            return n == 0 || (x == 0.0 && y == 0.0) ? double.NaN : CoordinateUtils.ConditionDegrees(double.RadiansToDegrees(Math.Atan2(y, x)));
        }

        private static double SignedDelta(double angleDeg, double referenceDeg) =>
            double.IsFinite(angleDeg) && double.IsFinite(referenceDeg) ? CoordinateUtils.ConditionDegreesSigned(angleDeg - referenceDeg) : double.NaN;
    }
}
