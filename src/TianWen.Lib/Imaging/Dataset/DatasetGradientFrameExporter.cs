using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// G2 of docs/plans/gradient-remover-training.md: every retained master reduced to the gradient model's
/// square, LINEAR and on the WHOLE canvas, beside its presence plane, the classical surface G1 fitted to
/// it, its coverage depth, and the covariates G1 measured (section 3, "Edges").
/// </summary>
/// <remarks>
/// <para><b>What a frame file holds.</b> Little-endian float32 planes of <c>Size x Size</c>, in
/// <see cref="PlaneLayout"/> order: the master's channels (the mean over present pixels,
/// <see cref="WholeFrameSampler"/>), the classical surface's channels reduced the same way, the presence
/// plane, and the coverage depth (the sidecar's frame count over its maximum; 1 wherever present when a
/// master has no sidecar). NaN wherever a sample has no present pixel, the pad around the frame included.</para>
/// <para><b>The surface is stored, not subtracted.</b> A training pair is the flattened scene plus an
/// injected gradient, and the flattened scene is <c>source - surface + level</c>; keeping both halves lets
/// the trainer also re-inject a master's OWN fitted surface as one of the gradients, a real shape no
/// synthetic family has to imitate.</para>
/// <para><b>Nothing here is re-derived.</b> The surface is G1's own fit (<c>DatasetGradientReport.FitMasterAsync</c>),
/// the covariates are G1's store joined by master, and the split is the bake's own pinned
/// <c>test-sessions.txt</c>, a flip side going with its night (<see cref="DatasetSplitWriter.GroupIdOf"/>).
/// A master with no session in the bake's ledger is exported with the split <c>unknown</c>, which a
/// trainer must never train on: it could be held-out sky.</para>
/// <para><b>Whether a master was flat-fielded is the bake's record, not a guess.</b> The row carries the
/// calibration the PSF store recorded for its session (the resolver's slugs) and <c>HasFlat</c> from it.
/// A session with no flat keeps its vignetting, a multiplicative falloff the model would otherwise learn
/// as sky, so it is not a training scene (the plan's section 3). A record written before the store
/// captured calibration says nothing, so <c>HasFlat</c> is then null, never false: "unrecorded" is not
/// "no flat", and a trainer that wants only flat-fielded scenes reads <c>HasFlat == true</c>.</para>
/// <para><b>The thin band's level steps</b> are measured here, before any model (the plan's G2
/// prediction), two ways, in the plane's own per-pixel background sigma. The band OFFSET is the median of
/// <c>source - surface</c> over the samples the coverage says fewer frames reached against the full-depth
/// samples within three of them; it also reads any gradient the degree-2 surface misses at the edge,
/// where the band sits and the surface is extrapolated. The STEP is the median difference across the
/// boundary, thin sample minus the full-depth sample two away (past the sample straddling it), which a
/// smooth gradient barely moves and a step moves by all of itself: the cleaner number for "is there a
/// step". Neither can tell a step from an OPTICALLY dark edge (vignetting, an image circle's rim) that
/// happens to coincide with the band; both are present pixels at full coverage depth.</para>
/// </remarks>
public static class DatasetGradientFrameExporter
{
    /// <summary>The manifest under the output root, one row per exported master.</summary>
    public const string ManifestFileName = "gradient-frames.jsonl";

    /// <summary>The frame files' directory under the output root.</summary>
    public const string FramesDirectoryName = "frames";

    /// <summary>GraXpert's input size, and the plan's.</summary>
    public const int DefaultSize = 256;

    /// <summary>The order of the planes in every frame file; C is the master's channel count.</summary>
    public const string PlaneLayout = "source[C],surface[C],presence,depth";

    /// <summary>The split a master with no session in the ledger gets; never trained on.</summary>
    public const string UnknownSplit = "unknown";

    /// <summary>Depth below which a sample is in the thin band.</summary>
    internal const float ThinBandDepth = 0.98f;

    /// <summary>How far from the thin band a full-depth sample may be and still be its reference, samples.</summary>
    internal const int ThinBandReach = 3;

    /// <summary>The fewest samples on either side for a thin-band offset to be reported.</summary>
    internal const int ThinBandMinSamples = 8;

    /// <summary>Inputs of an export.</summary>
    /// <param name="MasterFiles">The retained masters; coverage, rejection and bad-pixel sidecars among them are ignored.</param>
    /// <param name="SessionLedgerPath">The bake's <c>stats/sessions.jsonl</c> (<see cref="DatasetSessionLedger"/>), mapping a master to its session.</param>
    /// <param name="TestSessionsPath">The bake's pinned <c>test-sessions.txt</c>.</param>
    /// <param name="GradientStorePath">G1's <c>gradient-masters.jsonl</c> for the covariates and scale, or null.</param>
    /// <param name="PsfStorePath">The bake's <c>stats/psf-sessions.jsonl</c> (<see cref="DatasetPsfStore"/>) for each session's optical train and the calibration it was baked with, or null.</param>
    /// <param name="OutputDir">Root of the export: the manifest and <see cref="FramesDirectoryName"/>.</param>
    /// <param name="Size">The model's square input.</param>
    /// <param name="Force">Re-export masters already in the manifest.</param>
    /// <param name="Parallelism">Masters exported at once; each holds its master, a filled copy and the fit (about 2 GB on
    /// a 26 MP colour master), so memory, not cores, is the bound.</param>
    public sealed record ExportOptions(
        ImmutableArray<string> MasterFiles, string SessionLedgerPath, string TestSessionsPath, string? GradientStorePath,
        string? PsfStorePath, string OutputDir, int Size = DefaultSize, bool Force = false, int Parallelism = 1);

    /// <summary>Outcome of an export.</summary>
    /// <param name="NoFlat">Exported masters whose session the bake calibrated with no flat.</param>
    /// <param name="FlatUnknown">Exported masters whose session has no calibration record (none, or one written before the field existed).</param>
    public sealed record ExportResult(
        int Exported, int Skipped, int Failed, int Test, int UnknownSplit, int NoFlat, int FlatUnknown, string ManifestPath);

    /// <summary>One exported master.</summary>
    /// <param name="Master">The master's file name, the row's key.</param>
    /// <param name="SessionId">Its session id (a flip side's carries <c>|flip=a</c> or <c>b</c>); empty when the ledger has none.</param>
    /// <param name="Split"><c>train</c>, <c>test</c>, or <see cref="UnknownSplit"/>.</param>
    /// <param name="OpticalTrain">The session's optical train as the bake recorded it; empty when the PSF store has no record.</param>
    /// <param name="Calibration">The masters the bake calibrated the session's frames with, by the resolver's slugs; null when unrecorded.</param>
    /// <param name="HasFlat">Whether those frames were flat-fielded: null when <paramref name="Calibration"/> is, never read as false.</param>
    /// <param name="Camera">INSTRUME. A camera is not a field: read <paramref name="FieldWidthDeg"/> for that.</param>
    /// <param name="Filter">The filter's FITS name.</param>
    /// <param name="ObjectName">OBJECT.</param>
    /// <param name="Strategy">The integration strategy the master records.</param>
    /// <param name="StackedFrames">Frames integrated.</param>
    /// <param name="Width">The master's width, pixels.</param>
    /// <param name="Height">The master's height, pixels.</param>
    /// <param name="Channels">C, its channel count.</param>
    /// <param name="Size">The square's side.</param>
    /// <param name="FrameWidth">The frame's width inside the square.</param>
    /// <param name="FrameHeight">The frame's height inside the square.</param>
    /// <param name="OffsetX">The frame's first column in the square.</param>
    /// <param name="OffsetY">The frame's first row in the square.</param>
    /// <param name="SourcePixelsPerSample">Master pixels per sample along the long side.</param>
    /// <param name="File">The frame file, relative to the output root, forward slashes.</param>
    /// <param name="Layout"><see cref="PlaneLayout"/>.</param>
    /// <param name="Median">Per channel, the median of the source samples with any presence.</param>
    /// <param name="Mad">Per channel, the median absolute deviation from that median (unscaled).</param>
    /// <param name="SurfaceLevel">Per channel, the classical fit's level (the median of its model).</param>
    /// <param name="BackgroundSigma">Per channel, the master's own per-pixel background sigma (G1's).</param>
    /// <param name="AbsentFraction">The share of the frame's area absent, from the presence plane.</param>
    /// <param name="ThinBandFraction">The share of the PRESENT area under <see cref="ThinBandDepth"/> of full depth.</param>
    /// <param name="HasCoverage">Whether the master carried a coverage sidecar (else depth is 1 wherever present).</param>
    /// <param name="CropX">The all-frames rectangle in the square: first column (0 with no sidecar).</param>
    /// <param name="CropY">Its first row.</param>
    /// <param name="CropWidth">Its width (0 with no sidecar).</param>
    /// <param name="CropHeight">Its height.</param>
    /// <param name="ThinBandOffsetSigma">Per channel, the thin band's level against the full-depth samples beside it, in background sigma; NaN when either side has too few samples.</param>
    /// <param name="ThinBandSamples">Thin-band samples the offset was taken over.</param>
    /// <param name="ThinBandStepSigma">Per channel, the median difference across the band's boundary (thin sample minus the full-depth sample two away), in background sigma; NaN with too few pairs.</param>
    /// <param name="ThinBandPairs">Boundary pairs the step was taken over.</param>
    /// <param name="PixelScaleArcsec">The master's solved scale (G1's, else its header WCS); NaN when unknown.</param>
    /// <param name="ScaleSource"><c>solve</c>, <c>header</c>, or empty.</param>
    /// <param name="FieldWidthDeg">The long side on the sky, degrees.</param>
    /// <param name="HasCovariates">Whether G1's store held this master.</param>
    /// <param name="Epoch">The master's DATE-OBS (see G1's epoch caveat).</param>
    /// <param name="AltitudeDeg">Target altitude at the epoch.</param>
    /// <param name="AzimuthDeg">Target azimuth at the epoch.</param>
    /// <param name="Airmass">Airmass at the epoch.</param>
    /// <param name="ParallacticAngleDeg">Parallactic angle at the epoch.</param>
    /// <param name="HorizonAngleInFrameDeg">The horizon's direction in the frame (G1's convention); the placement neither rotates nor flips, so it holds in the square too.</param>
    /// <param name="MoonAltitudeDeg">The Moon's altitude.</param>
    /// <param name="MoonIllumination">The Moon's illuminated fraction.</param>
    /// <param name="MoonSeparationDeg">The Moon's separation from the target.</param>
    /// <param name="MoonAngleInFrameDeg">The Moon's direction in the frame.</param>
    public sealed record FrameRow(
        string Master, string SessionId, string Split,
        string OpticalTrain, CalibrationProvenance? Calibration, bool? HasFlat,
        string Camera, string Filter, string ObjectName, string Strategy, int StackedFrames,
        int Width, int Height, int Channels,
        int Size, int FrameWidth, int FrameHeight, int OffsetX, int OffsetY, double SourcePixelsPerSample,
        string File, string Layout,
        float[] Median, float[] Mad, float[] SurfaceLevel, float[] BackgroundSigma,
        float AbsentFraction, float ThinBandFraction, bool HasCoverage,
        int CropX, int CropY, int CropWidth, int CropHeight,
        float[] ThinBandOffsetSigma, int ThinBandSamples,
        float[] ThinBandStepSigma, int ThinBandPairs,
        double PixelScaleArcsec, string ScaleSource, double FieldWidthDeg,
        bool HasCovariates, DateTimeOffset Epoch, double AltitudeDeg, double AzimuthDeg, double Airmass,
        double ParallacticAngleDeg, double HorizonAngleInFrameDeg,
        double MoonAltitudeDeg, double MoonIllumination, double MoonSeparationDeg, double MoonAngleInFrameDeg)
    {
        /// <summary>The master file's last write when it was exported, UTC (<see cref="DatasetGradientReport.MasterGradient.WrittenUtcOf"/>).
        /// A row is reused only while the master is that file: a re-bake rewrites a master under its own name.</summary>
        public DateTimeOffset? MasterWrittenUtc { get; init; }
    }

    /// <summary>
    /// Exports every master not already in the manifest, appending each row as it completes, so a stopped
    /// run keeps what it finished. Faults are isolated per master.
    /// </summary>
    public static async Task<ExportResult> RunAsync(
        ExportOptions options, ILogger? logger = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var framesDir = Path.Combine(options.OutputDir, FramesDirectoryName);
        Directory.CreateDirectory(framesDir);
        var manifestPath = Path.Combine(options.OutputDir, ManifestFileName);
        var done = await JsonLinesFile.ReadLastPerKeyAsync(
            manifestPath, DatasetGradientFrameJsonContext.Default.FrameRow, static r => r.Master, "gradient frame manifest", logger, cancellationToken);

        var ledger = await DatasetSessionLedger.ReadAsync(options.SessionLedgerPath, logger, cancellationToken);
        var sessionByStem = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in ledger.Values)
        {
            sessionByStem[Path.GetFileName(entry.TileDirRelative)] = entry.SessionId;
        }
        var heldOut = DatasetSplitWriter.ReadPinned(options.TestSessionsPath).ToHashSet(StringComparer.Ordinal);
        var covariates = options.GradientStorePath is { } storePath && File.Exists(storePath)
            ? await DatasetGradientStore.ReadAsync(storePath, logger, cancellationToken)
            : new Dictionary<string, DatasetGradientReport.MasterGradient>();
        var psfBySession = options.PsfStorePath is { } psfPath && File.Exists(psfPath)
            ? await DatasetPsfStore.ReadAsync(psfPath, logger, cancellationToken)
            : new Dictionary<string, DatasetPsfNoiseReport.SessionPsf>();

        var files = options.MasterFiles
            .Where(static path => !IntegrationFitsWriter.IsMapSidecarPath(path))
            .ToImmutableArray()
            .Sort(StringComparer.OrdinalIgnoreCase);
        int exported = 0, skipped = 0, failed = 0, test = 0, unknown = 0, noFlat = 0, flatUnknown = 0;
        // Masters run Parallelism at a time: one master's work is mostly serial, so a single pass used 0.9 of
        // sixteen cores (G1b, 2026-10-02). A row is one manifest line, so its append takes the gate, or two
        // racing appends could interleave; everything else a master touches is its own.
        using var manifestGate = new SemaphoreSlim(1, 1);
        var work = files.Select(static (path, i) => (Path: path, Index: i + 1));
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(work, parallel, async (item, ct) =>
        {
            var (path, index) = item;
            var key = Path.GetFileName(path);
            if (!options.Force && done.TryGetValue(key, out var existing) && File.Exists(Path.Combine(options.OutputDir, existing.File))
                && existing.MasterWrittenUtc == DatasetGradientReport.MasterGradient.WrittenUtcOf(path))
            {
                Interlocked.Increment(ref skipped);
                progress?.Report($"[gradient-export] {index}/{files.Length} in manifest, skipped: {key}");
                return;
            }

            var sessionId = SessionIdOf(Path.GetFileNameWithoutExtension(path), sessionByStem);
            var split = sessionId.Length == 0 ? UnknownSplit
                : heldOut.Contains(sessionId) || heldOut.Contains(DatasetSplitWriter.GroupIdOf(sessionId)) ? "test"
                : "train";
            try
            {
                covariates.TryGetValue(key, out var record);
                psfBySession.TryGetValue(sessionId, out var psf);
                var row = await ExportMasterAsync(path, sessionId, split, record, psf, options.OutputDir, options.Size, ct)
                    with { MasterWrittenUtc = DatasetGradientReport.MasterGradient.WrittenUtcOf(path) };
                await manifestGate.WaitAsync(ct);
                try
                {
                    await JsonLinesFile.AppendRecordAsync(manifestPath, row, DatasetGradientFrameJsonContext.Default.FrameRow, ct);
                }
                finally
                {
                    manifestGate.Release();
                }
                Interlocked.Increment(ref exported);
                if (split == "test")
                {
                    Interlocked.Increment(ref test);
                }
                if (split == UnknownSplit)
                {
                    Interlocked.Increment(ref unknown);
                }
                if (row.HasFlat == false)
                {
                    Interlocked.Increment(ref noFlat);
                }
                if (row.HasFlat is null)
                {
                    Interlocked.Increment(ref flatUnknown);
                }
                var flat = row.HasFlat switch { true => "flat", false => "NO FLAT", null => "flat unknown" };
                progress?.Report(
                    $"[gradient-export] {index}/{files.Length} {key}: {split}, {flat}, {row.FrameWidth}x{row.FrameHeight} at {row.SourcePixelsPerSample:F1} px/sample, " +
                    $"absent {row.AbsentFraction:P1}, thin band {row.ThinBandFraction:P1} (offset {string.Join("/", row.ThinBandOffsetSigma.Select(v => v.ToString("F2")))}, " +
                    $"step {string.Join("/", row.ThinBandStepSigma.Select(v => v.ToString("F2")))} sigma over {row.ThinBandPairs} pairs), field {row.FieldWidthDeg:F1} deg");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fault-isolated per master, as the report is: one pathological file must not cost the
                // other hundred and eighty their export.
                Interlocked.Increment(ref failed);
                logger?.LogError(ex, "Gradient export: {Master} failed", key);
                progress?.Report($"[gradient-export] {index}/{files.Length} FAILED {key}: {ex.Message}");
            }
        });

        return new ExportResult(exported, skipped, failed, test, unknown, noFlat, flatUnknown, manifestPath);
    }

    /// <summary>
    /// Exports one master into <paramref name="outputDir"/>'s frames and returns its manifest row (not
    /// appended). <paramref name="covariates"/> is G1's record for it, <paramref name="psf"/> the bake's
    /// PSF-store record for its session; either may be null.
    /// </summary>
    internal static async Task<FrameRow> ExportMasterAsync(
        string masterPath, string sessionId, string split, DatasetGradientReport.MasterGradient? covariates,
        DatasetPsfNoiseReport.SessionPsf? psf, string outputDir, int size, CancellationToken cancellationToken)
    {
        if (!Image.TryReadFitsFile(masterPath, out var master, out var headerWcs))
        {
            throw new InvalidDataException($"{Path.GetFileName(masterPath)} does not read as an image");
        }

        DatasetGradientReport.MasterFit? fit = null;
        Image? filled = null;
        Image? coverage = null;
        try
        {
            var meta = master.ImageMeta;
            var (channels, width, height) = master.Shape;
            fit = await DatasetGradientReport.FitMasterAsync(master, masterPath, cancellationToken);

            // Interior holes are filled first, the one rule, so only the edge reads as missing.
            filled = master.WithInteriorHolesFilled();
            var absent = filled.AbsentPixels();
            var placement = WholeFramePlacement.For(width, height, size);
            var source = WholeFrameSampler.Sample(filled, absent, placement);
            var surface = WholeFrameSampler.Sample(fit.Baseline.Background, absent, placement);

            var hasCoverage = IntegrationFitsWriter.TryReadCoverageMap(masterPath, out coverage);
            var depth = hasCoverage && coverage is { } cov
                ? DepthOf(WholeFrameSampler.Sample(cov, absent, placement))
                : source.Presence.Select(static p => p > 0 ? 1f : float.NaN).ToArray();

            var sigma = fit.Planes.Select(static p => p.BackgroundSigma).ToArray();
            var (offsets, thinSamples) = ThinBandOffsets(source, surface, depth, sigma);
            var (steps, pairs) = ThinBandSteps(source, surface, depth, sigma);
            var crop = CropInSquare(fit.CoveredRect, placement);
            var stem = Path.GetFileNameWithoutExtension(masterPath);
            var relative = FramesDirectoryName + "/" + stem + ".f32";
            await WriteFrameAsync(Path.Combine(outputDir, FramesDirectoryName, stem + ".f32"), source, surface, depth, cancellationToken);

            var (scale, scaleSource) = covariates is { ScaleSource.Length: > 0 } stored
                ? (stored.PixelScaleArcsec, stored.ScaleSource)
                : DatasetGradientReport.ScaleOf(null, headerWcs);
            var (medians, mads) = MedianAndMad(source);
            var (strategy, stackedFrames) = DatasetGradientReport.ReadMasterCards(masterPath);
            var (absentFraction, thinFraction) = AreaFractions(source.Presence, depth, placement);

            var calibration = psf?.Calibration;
            return new FrameRow(
                Path.GetFileName(masterPath), sessionId, split,
                psf?.OpticalTrain ?? "", calibration, calibration is null ? null : calibration.Flat is { Length: > 0 },
                meta.Instrument ?? "", meta.Filter.FilterNameForFits ?? "", meta.ObjectName ?? "",
                strategy, stackedFrames,
                width, height, channels,
                size, placement.FrameWidth, placement.FrameHeight, placement.OffsetX, placement.OffsetY, placement.SourcePixelsPerSample,
                relative, PlaneLayout,
                medians, mads, fit.Planes.Select(static p => p.Level).ToArray(), sigma,
                absentFraction, thinFraction, hasCoverage,
                crop.X, crop.Y, crop.Width, crop.Height,
                offsets, thinSamples,
                steps, pairs,
                scale, scaleSource, scale * Math.Max(width, height) / 3600.0,
                covariates is not null, covariates?.Epoch ?? meta.ExposureStartTime,
                covariates?.AltitudeDeg ?? double.NaN, covariates?.AzimuthDeg ?? double.NaN, covariates?.Airmass ?? double.NaN,
                covariates?.ParallacticAngleDeg ?? double.NaN, covariates?.HorizonAngleInFrameDeg ?? double.NaN,
                covariates?.MoonAltitudeDeg ?? double.NaN, covariates?.MoonIllumination ?? double.NaN,
                covariates?.MoonSeparationDeg ?? double.NaN, covariates?.MoonAngleInFrameDeg ?? double.NaN);
        }
        finally
        {
            coverage?.Release();
            if (filled is not null && !ReferenceEquals(filled, master))
            {
                // WithInteriorHolesFilled hands the image itself back when there is no hole to fill.
                filled.Release();
            }
            fit?.Release();
            master.Release();
        }
    }

    /// <summary>
    /// The session a retained master belongs to: the ledger's id for its stem, or, for a meridian flip's
    /// side (<c>&lt;stem&gt;_flip=a</c>), its night's id with the side appended. Empty when the ledger has none.
    /// </summary>
    internal static string SessionIdOf(string masterStem, IReadOnlyDictionary<string, string> sessionByStem)
    {
        if (sessionByStem.TryGetValue(masterStem, out var id))
        {
            return id;
        }
        var marker = "_" + ImagingSession.FlipSideKey + "=";
        var at = masterStem.LastIndexOf(marker, StringComparison.Ordinal);
        return at > 0 && sessionByStem.TryGetValue(masterStem[..at], out var night)
            ? night + "|" + ImagingSession.FlipSideKey + "=" + masterStem[(at + marker.Length)..]
            : "";
    }

    /// <summary>The coverage depth per sample: the channels' mean count over its maximum, NaN where absent.</summary>
    private static float[] DepthOf(WholeFrameSample coverage)
    {
        var n = coverage.Presence.Length;
        var depth = new float[n];
        var max = 0f;
        for (var i = 0; i < n; i++)
        {
            var sum = 0f;
            foreach (var plane in coverage.Planes)
            {
                sum += plane[i];
            }
            depth[i] = coverage.Presence[i] > 0 ? sum / coverage.Planes.Length : float.NaN;
            if (depth[i] > max)
            {
                max = depth[i];
            }
        }
        if (max > 0)
        {
            for (var i = 0; i < n; i++)
            {
                depth[i] /= max;
            }
        }
        return depth;
    }

    /// <summary>The thin band's offset per channel, in sigma, and how many thin samples it was taken over.</summary>
    private static (float[] Offsets, int ThinSamples) ThinBandOffsets(WholeFrameSample source, WholeFrameSample surface, float[] depth, float[] sigma)
    {
        var size = source.Placement.Size;
        var thin = new bool[size * size];
        var thinCount = 0;
        for (var i = 0; i < thin.Length; i++)
        {
            thin[i] = source.Presence[i] >= 0.5f && depth[i] < ThinBandDepth;
            thinCount += thin[i] ? 1 : 0;
        }

        var reference = new bool[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                if (source.Presence[i] < 0.999f || !(depth[i] >= 0.999f))
                {
                    continue;
                }
                for (var dy = -ThinBandReach; dy <= ThinBandReach && !reference[i]; dy++)
                {
                    for (var dx = -ThinBandReach; dx <= ThinBandReach; dx++)
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (nx >= 0 && ny >= 0 && nx < size && ny < size && thin[ny * size + nx])
                        {
                            reference[i] = true;
                            break;
                        }
                    }
                }
            }
        }

        var offsets = new float[source.Planes.Length];
        for (var c = 0; c < offsets.Length; c++)
        {
            var thinResidual = new List<float>();
            var referenceResidual = new List<float>();
            for (var i = 0; i < thin.Length; i++)
            {
                var r = source.Planes[c][i] - surface.Planes[c][i];
                if (!float.IsFinite(r))
                {
                    continue;
                }
                if (thin[i])
                {
                    thinResidual.Add(r);
                }
                else if (reference[i])
                {
                    referenceResidual.Add(r);
                }
            }
            offsets[c] = thinResidual.Count >= ThinBandMinSamples && referenceResidual.Count >= ThinBandMinSamples && sigma[c] > 0
                ? (Median(thinResidual) - Median(referenceResidual)) / sigma[c]
                : float.NaN;
        }
        return (offsets, thinCount);
    }

    /// <summary>
    /// The step across the thin band's boundary per channel, in sigma, and how many pairs it was taken over.
    /// Each full-depth sample is paired with the thin sample TWO along in each direction, past the one
    /// straddling the boundary: that sample holds part of each side, so pairing with it reads a fraction of
    /// the step (three quarters of a planted step, measured).
    /// </summary>
    private static (float[] Steps, int Pairs) ThinBandSteps(WholeFrameSample source, WholeFrameSample surface, float[] depth, float[] sigma)
    {
        var size = source.Placement.Size;
        var channels = source.Planes.Length;
        var differences = new List<float>[channels];
        for (var c = 0; c < channels; c++)
        {
            differences[c] = [];
        }
        var pairs = 0;
        ReadOnlySpan<(int Dx, int Dy)> directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var f = y * size + x;
                if (source.Presence[f] < 0.999f || !(depth[f] >= 0.999f))
                {
                    continue;
                }
                foreach (var (dx, dy) in directions)
                {
                    var (mx, my, tx, ty) = (x + dx, y + dy, x + 2 * dx, y + 2 * dy);
                    if (tx < 0 || ty < 0 || tx >= size || ty >= size)
                    {
                        continue;
                    }
                    var m = my * size + mx;
                    var t = ty * size + tx;
                    // Crossing the boundary: the next sample is not at full depth, the one after is thin.
                    if (!(depth[m] < 0.999f) || source.Presence[t] < 0.5f || !(depth[t] < ThinBandDepth))
                    {
                        continue;
                    }
                    var counted = false;
                    for (var c = 0; c < channels; c++)
                    {
                        var d = (source.Planes[c][t] - surface.Planes[c][t]) - (source.Planes[c][f] - surface.Planes[c][f]);
                        if (float.IsFinite(d))
                        {
                            differences[c].Add(d);
                            counted = true;
                        }
                    }
                    pairs += counted ? 1 : 0;
                }
            }
        }

        var steps = new float[channels];
        for (var c = 0; c < channels; c++)
        {
            steps[c] = differences[c].Count >= ThinBandMinSamples && sigma[c] > 0 ? Median(differences[c]) / sigma[c] : float.NaN;
        }
        return (steps, pairs);
    }

    /// <summary>The all-frames rectangle mapped into the square, shrunk to whole samples inside it.</summary>
    private static PixelRect CropInSquare(PixelRect covered, WholeFramePlacement p)
    {
        if (covered.IsEmpty)
        {
            return default;
        }
        var x0 = p.OffsetX + (int)Math.Ceiling((double)covered.Left * p.FrameWidth / p.SourceWidth);
        var x1 = p.OffsetX + (int)Math.Floor((double)covered.Right * p.FrameWidth / p.SourceWidth);
        var y0 = p.OffsetY + (int)Math.Ceiling((double)covered.Top * p.FrameHeight / p.SourceHeight);
        var y1 = p.OffsetY + (int)Math.Floor((double)covered.Bottom * p.FrameHeight / p.SourceHeight);
        return x1 > x0 && y1 > y0 ? new PixelRect(x0, y0, x1 - x0, y1 - y0) : default;
    }

    /// <summary>Per channel, the median and the unscaled MAD of the samples with any presence.</summary>
    private static (float[] Medians, float[] Mads) MedianAndMad(WholeFrameSample sample)
    {
        var medians = new float[sample.Planes.Length];
        var mads = new float[sample.Planes.Length];
        for (var c = 0; c < medians.Length; c++)
        {
            var values = new List<float>();
            for (var i = 0; i < sample.Presence.Length; i++)
            {
                if (sample.Presence[i] > 0 && float.IsFinite(sample.Planes[c][i]))
                {
                    values.Add(sample.Planes[c][i]);
                }
            }
            if (values.Count == 0)
            {
                (medians[c], mads[c]) = (float.NaN, float.NaN);
                continue;
            }
            var median = Median(values);
            medians[c] = median;
            mads[c] = Median(values.Select(v => MathF.Abs(v - median)).ToList());
        }
        return (medians, mads);
    }

    /// <summary>The frame's absent share, and the thin band's share of what is present, from the sample planes.</summary>
    private static (float Absent, float Thin) AreaFractions(float[] presence, float[] depth, WholeFramePlacement p)
    {
        double frame = 0, present = 0, thin = 0;
        for (var y = p.OffsetY; y < p.OffsetY + p.FrameHeight; y++)
        {
            for (var x = p.OffsetX; x < p.OffsetX + p.FrameWidth; x++)
            {
                var i = y * p.Size + x;
                frame++;
                present += presence[i];
                if (depth[i] < ThinBandDepth)
                {
                    thin += presence[i];
                }
            }
        }
        return ((float)(1 - present / frame), present > 0 ? (float)(thin / present) : 0f);
    }

    private static float Median(List<float> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : 0.5f * (values[n / 2 - 1] + values[n / 2]);
    }

    /// <summary>Writes the planes in <see cref="PlaneLayout"/> order, staged and renamed so a reader never sees half a frame.</summary>
    private static async Task WriteFrameAsync(string path, WholeFrameSample source, WholeFrameSample surface, float[] depth, CancellationToken cancellationToken)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("the frame files are little-endian float32");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var staging = path + ".partial";
        await using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            foreach (var plane in source.Planes.Concat(surface.Planes).Append(source.Presence).Append(depth))
            {
                await stream.WriteAsync(MemoryMarshal.AsBytes(plane.AsSpan()).ToArray(), cancellationToken);
            }
        }
        File.Move(staging, path, overwrite: true);
    }
}

// NaN is a legitimate value here (an absent sample, an unsolved master's directions).
[JsonSerializable(typeof(DatasetGradientFrameExporter.FrameRow))]
// Registered explicitly, as the PSF store's context does: an AOT binary fails at RUNTIME on a type the
// generator's walk did not emit a converter for.
[JsonSerializable(typeof(CalibrationProvenance))]
[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal partial class DatasetGradientFrameJsonContext : JsonSerializerContext;
