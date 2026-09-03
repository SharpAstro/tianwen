using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Degradation;

namespace TianWen.AI.Imaging
{
    /// <summary>
    /// The shared degradation exporter (docs/plans/model-training-roadmap.md section 1 item 3): it
    /// takes a bake's RETAINED LINEAR masters, degrades each one in linear units, and exports the
    /// result through the identical path <see cref="DatasetTileExporter"/> uses, so a degraded tile and
    /// a P0 tile of the same cell are bytes in one convention. One exporter serves three trainings
    /// because they need the same thing and differ only in what "degrade" means: noise injection for
    /// the denoiser (E1), blur plus noise for the deconvolver (E2), star injection for the star remover
    /// (R1, not implemented here because its classical starless plate builder does not exist yet).
    ///
    /// <para><b>Why the retained masters and not the P0 tiles.</b> The stored tiles are MTF-stretched,
    /// and every degradation worth injecting is linear in flux: convolution is, photon noise is. A
    /// stretch is not invertible per tile either, since its parameters are whole-frame and per channel
    /// and are not in the manifest. So the source has to be the linear master, and the export path
    /// afterwards has to be the P0 one, or the model meets a domain it was not trained on (which is the
    /// defect H0 found in the runner: two weeks of training in a stretched domain fed with linear
    /// pixels).</para>
    ///
    /// <para><b>Both sides of a pair share the TARGET's parameters.</b> The unit divisor and the MTF
    /// parameters are measured ONCE on the clean master and applied to every degraded draw
    /// (<see cref="Image.MtfStretchWith"/>). Taking each side's own would encode the stretch difference
    /// as signal: injected noise lifts a frame's maximum, so its unit divisor differs, and a pair would
    /// then differ by a global rescale the net can learn instead of the degradation. It also makes the
    /// transform pointwise, which is what lets a draw be applied to one CELL rather than to the whole
    /// canvas: 300 cells against 24 megapixels, per draw.</para>
    ///
    /// <para><b>Cells come from the P0 manifest, not from a fresh selection.</b> The structure-biased
    /// sampler needs the session's all-frames intersection, which a retained master does not carry, and
    /// re-deriving it would drift. Reading the cells back also gives the per-cell sub noise the level
    /// range is drawn against, and guarantees a degraded tile covers exactly the pixels its P0
    /// counterpart does.</para>
    ///
    /// <para><b>The manifest is P0-shaped on purpose.</b> Rows go into a
    /// <see cref="DatasetTileExporter.ManifestFileName"/> with <c>Frame</c> = <c>master</c> for the clean
    /// target and <c>deg000..</c> for the draws, which the trainer's <c>--prepare</c> already reads
    /// without a line of Python changing: anything that is not one of the three known frame names lands
    /// in its sub slots, so slot 0 is the clean target and slots 1..8 are independent degradations of
    /// it. The degradation PARAMETERS live in their own store beside it rather than as extra columns,
    /// keeping one authority per fact.</para>
    /// </summary>
    public static class DatasetDegradationExporter
    {
        /// <summary>Per-tile degradation parameters, one row per degraded tile.</summary>
        public const string DegradationManifestFileName = "degradations.jsonl";

        /// <summary>Frame name of the clean target: the same string P0 uses, so it lands in slot 0.</summary>
        public const string FrameClean = DatasetTileExporter.FrameMaster;

        /// <summary>Frame name of the n-th degraded draw.</summary>
        public static string FrameForDraw(int draw) => "deg" + draw.ToString("D3", CultureInfo.InvariantCulture);

        /// <summary>What the exporter does to a frame.</summary>
        public enum DegradationMode
        {
            /// <summary>Add noise only: the denoiser's supervised arm (denoiser-training.md E1).</summary>
            Noise = 0,

            /// <summary>Convolve with a drawn PSF, then add noise: the deconvolver's pairs (deconvolver-training.md E2).</summary>
            Blur = 1,
        }

        /// <summary>The spatial shape of the injected noise; the one thing the H2 arms differ in.</summary>
        public enum NoiseShape
        {
            /// <summary>Uncorrelated. The S-white arm.</summary>
            White = 0,

            /// <summary>Correlated by a bilinear resample, as a registered stack's noise is. The S-warped arm.</summary>
            Warped = 1,
        }

        /// <summary>
        /// One degraded tile's parameters. Joined to the tile manifest on
        /// (<see cref="SessionId"/>, <see cref="CellX"/>, <see cref="CellY"/>, <see cref="Frame"/>).
        /// </summary>
        /// <param name="Tile">Blob path relative to the output directory.</param>
        /// <param name="SessionId">Portable session id.</param>
        /// <param name="Frame">The draw's frame name (<see cref="FrameForDraw"/>).</param>
        /// <param name="CellX">Cell origin X on the session canvas.</param>
        /// <param name="CellY">Cell origin Y.</param>
        /// <param name="Draw">Draw index within the cell.</param>
        /// <param name="Mode">Degradation mode.</param>
        /// <param name="Shape">Noise shape.</param>
        /// <param name="StackedFrames">Frames integrated into the source master (its <c>STACK_N</c>).</param>
        /// <param name="DepthScale">Injected noise level as a multiple of ONE sub's noise, drawn log-uniform.
        /// <see cref="MasterDepth"/> is interior to that range by construction, because the bottom of the
        /// range is derived from it per session rather than fixed.</param>
        /// <param name="OneSubSigma">Measured noise of one sub at the cell's background, unit-scaled linear.</param>
        /// <param name="BackgroundLevel">The cell's robust background, unit-scaled linear.</param>
        /// <param name="AdjacentDiffSigma">The structure-insensitive noise estimate of the same cell, as a
        /// diagnostic: it reads lower than the MAD wherever the cell carries nebulosity, and lower again
        /// because a stacked frame's noise is correlated.</param>
        /// <param name="ExtraFwhmPx">Blur mode: the ADDED FWHM in pixels (composes in quadrature with the
        /// frame's own). Zero in noise mode.</param>
        /// <param name="MoffatBeta">Blur mode: the drawn Moffat exponent.</param>
        /// <param name="Elongation">Blur mode: major/minor axis ratio of the kernel.</param>
        /// <param name="PositionAngleDeg">Blur mode: kernel major-axis angle in the pixel frame.</param>
        /// <param name="FieldRadius">Cell centre distance from the frame centre over the half-diagonal, so
        /// 0 is the centre and 1 a corner. The covariate H7 (position-varying PSF) needs.</param>
        /// <param name="NoiseAnchor">Which measurement set the level: <c>sub-noisemad</c> (a real sub's
        /// measured noise, converted to linear through the MTF slope) or <c>master-mad</c> (the fallback
        /// when the cell has no sub rows, which reads a cell's structure as noise and overstates it).</param>
        /// <param name="MasterDepth">The session's own master depth, 1/sqrt(StackedFrames): the level the
        /// model meets at inference. It must be INTERIOR to the drawn range, which is what makes the
        /// bottom of that range a per-session value rather than a constant.</param>
        /// <param name="Seed">The draw's RNG seed, so one tile can be re-derived on its own.</param>
        public sealed record DegradationRow(
            string Tile,
            string SessionId,
            string Frame,
            int CellX,
            int CellY,
            int Draw,
            string Mode,
            string Shape,
            int StackedFrames,
            double DepthScale,
            double OneSubSigma,
            double BackgroundLevel,
            double AdjacentDiffSigma,
            double ExtraFwhmPx,
            double MoffatBeta,
            double Elongation,
            double PositionAngleDeg,
            double FieldRadius,
            string NoiseAnchor,
            double MasterDepth,
            int Seed);

        /// <summary>What to export.</summary>
        /// <param name="BakeRoot">A dataset bake: it must hold <c>tiles-manifest.jsonl</c> and
        /// <c>session-masters/</c>.</param>
        /// <param name="OutDir">Where the degraded cache is written. Never the bake root: the manifest
        /// file name is the same and the trainer would then see both sets as one.</param>
        /// <param name="Mode">Noise only, or blur then noise.</param>
        /// <param name="Shape">Noise shape.</param>
        /// <param name="Draws">Degraded draws per cell. Eight fills the trainer's sub slots.</param>
        /// <param name="CellsPerSession">Cap on cells taken from the P0 manifest (the first N in canonical
        /// order), or 0 for all of them.</param>
        /// <param name="MaxSessions">Cap on sessions, or 0 for all.</param>
        /// <param name="Seed">Base seed; each (session, cell, draw) derives its own from it.</param>
        /// <param name="MinDepthScale">Hard floor of the log-uniform level range, in multiples of one sub.
        /// A CLAMP, not the value: the bottom actually used is the smaller of this and
        /// <paramref name="MasterDepthFraction"/> of the session's own master depth.</param>
        /// <param name="MasterDepthFraction">The bottom of the range as a fraction of the session's master
        /// depth (1/sqrt(StackedFrames)), so the level the model is DEPLOYED at is always interior to the
        /// range rather than at or past its edge.</param>
        /// <param name="MaxDepthScale">Top of the level range.</param>
        /// <param name="WarpResampleSigma">Warped shape only: extra smoothing per realisation, in pixels,
        /// standing in for a resampling kernel wider than bilinear. The knob that calibrates the arm
        /// against the shape a real frame has; measure with --measure-shape rather than guessing.</param>
        /// <param name="MinExtraFwhmPx">Blur mode: bottom of the added-FWHM range.</param>
        /// <param name="MaxExtraFwhmPx">Blur mode: top of the added-FWHM range.</param>
        /// <param name="Force">Re-export a session already present in the degradation store.</param>
        public sealed record Options(
            string BakeRoot,
            string OutDir,
            DegradationMode Mode = DegradationMode.Noise,
            NoiseShape Shape = NoiseShape.White,
            int Draws = 8,
            int CellsPerSession = 300,
            int MaxSessions = 0,
            int Seed = 1,
            double WarpResampleSigma = 0.0,
            double MinDepthScale = 0.1,
            double MasterDepthFraction = 0.5,
            double MaxDepthScale = 1.5,
            double MinExtraFwhmPx = 0.5,
            double MaxExtraFwhmPx = 4.0,
            bool Force = false);

        /// <summary>What one session's export produced.</summary>
        public sealed record SessionResult(string SessionId, int Cells, int CleanTiles, int DegradedTiles, double ParityMaxAbsDiff, long ElapsedMs);

        /// <summary>What a whole run produced.</summary>
        public sealed record RunResult(
            ImmutableArray<SessionResult> Sessions,
            int Skipped,
            int Failed,
            string TileManifestPath,
            string DegradationManifestPath,
            double WorstParity);

        /// <summary>
        /// The band1/band0 of one labelled population of noise, measured scene-free by differencing two
        /// frames of the SAME scene. Real sub pairs and real half-master pairs are measured with the
        /// identical code as the injected draws, because 0.60 and 0.32 came out of another
        /// implementation on another domain and a number is only a target if it is measured the same way.
        /// </summary>
        public sealed record ShapeMeasurement(string Label, int Pairs, double Band0, double Band1, double Band2, double Ratio);

        /// <summary>
        /// Exports one bake. Sessions are processed in canonical order; a session already in the
        /// degradation store is skipped unless <see cref="Options.Force"/>, and a session that throws is
        /// logged and skipped so one bad master cannot lose the run.
        /// </summary>
        public static async Task<RunResult> RunAsync(Options options, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Draws);
            if (Path.GetFullPath(options.BakeRoot).Equals(Path.GetFullPath(options.OutDir), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("the degraded cache must not be written into the bake it reads: both use " + DatasetTileExporter.ManifestFileName, nameof(options));
            }

            var bakeManifest = Path.Combine(options.BakeRoot, DatasetTileExporter.ManifestFileName);
            if (!File.Exists(bakeManifest))
            {
                throw new FileNotFoundException("no P0 tile manifest in the bake; the degradation exporter takes its cells from it", bakeManifest);
            }

            Directory.CreateDirectory(options.OutDir);
            var outTileManifest = Path.Combine(options.OutDir, DatasetTileExporter.ManifestFileName);
            var outDegManifest = Path.Combine(options.OutDir, DegradationManifestFileName);

            var cellsBySession = await ReadCellsAsync(bakeManifest, cancellationToken);
            var alreadyDone = options.Force ? [] : await ReadExportedSessionsAsync(outDegManifest, cancellationToken);

            var sessions = cellsBySession.Keys.OrderBy(static s => s, StringComparer.Ordinal).ToList();
            if (options.MaxSessions > 0 && sessions.Count > options.MaxSessions)
            {
                sessions = sessions.Take(options.MaxSessions).ToList();
            }

            var results = ImmutableArray.CreateBuilder<SessionResult>();
            var skipped = 0;
            var failed = 0;
            var worstParity = 0.0;
            var index = 0;
            foreach (var sessionId in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                if (alreadyDone.Contains(sessionId))
                {
                    skipped++;
                    continue;
                }
                if (!RetainedMasterStore.Exists(options.BakeRoot, sessionId))
                {
                    logger?.LogWarning("[degrade] {Index}/{Total} {Session}: no retained master, skipped", index, sessions.Count, sessionId);
                    skipped++;
                    continue;
                }

                try
                {
                    var result = await ExportSessionAsync(options, sessionId, cellsBySession[sessionId], outTileManifest, outDegManifest, logger, cancellationToken);
                    results.Add(result);
                    worstParity = Math.Max(worstParity, result.ParityMaxAbsDiff);
                    logger?.LogInformation(
                        "[degrade] {Index}/{Total} {Session}: {Cells} cells, {Degraded} degraded tiles, parity {Parity:E1}, {Ms} ms",
                        index, sessions.Count, sessionId, result.Cells, result.DegradedTiles, result.ParityMaxAbsDiff, result.ElapsedMs);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger?.LogError(ex, "[degrade] {Index}/{Total} {Session}: failed", index, sessions.Count, sessionId);
                }
            }

            return new RunResult(results.ToImmutable(), skipped, failed, outTileManifest, outDegManifest, worstParity);
        }

        private static async Task<SessionResult> ExportSessionAsync(
            Options options,
            string sessionId,
            IReadOnlyList<CellSpec> cells,
            string outTileManifest,
            string outDegManifest,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            if (!RetainedMasterStore.TryRead(options.BakeRoot, sessionId, out var master, logger))
            {
                throw new IOException($"retained master for {sessionId} could not be read");
            }

            // Subsetting is a seeded SAMPLE, not a prefix. The P0 cells arrive sorted row-major, so
            // taking the first N would take the top of the canvas: a training set drawn only from the
            // top of every frame, with the field-radius covariate the deconvolver's H7 needs collapsed
            // onto one edge. Seeded on the session id so a re-run picks the same cells, and re-sorted
            // afterwards so the manifest order stays canonical.
            var selected = cells.ToList();
            if (options.CellsPerSession > 0 && selected.Count > options.CellsPerSession)
            {
                var rng = new Random(DrawSeed(options.Seed, sessionId, -1, -1, -1));
                for (var i = selected.Count - 1; i > 0; i--)
                {
                    var j = rng.Next(i + 1);
                    (selected[i], selected[j]) = (selected[j], selected[i]);
                }
                selected = selected.Take(options.CellsPerSession).ToList();
                selected.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            }

            var slug = DatasetTileExporter.Sanitize(sessionId);
            var tilesDir = Path.Combine(options.OutDir, "tiles", slug);
            Directory.CreateDirectory(tilesDir);

            var stackedFrames = ReadStackCount(RetainedMasterStore.PathFor(options.BakeRoot, sessionId));
            Image unitMaster = null!;
            Image cleanStretched = null!;
            try
            {
                // The clean side, once: the unit divisor and the MTF parameters measured here are the
                // domain BOTH sides of every pair live in.
                unitMaster = DatasetTileExporter.ToUnitRange(master);
                var (stretched, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(unitMaster);
                cleanStretched = stretched;
                if (!applied || origMin is null || balances is null)
                {
                    // A linear master must take the stretch branch. If it did not, the source is already
                    // stretched (or empty), and every pair built from it would carry a domain error that
                    // no parity check can see.
                    throw new InvalidOperationException(
                        $"{sessionId}: the retained master did not read as linear (the SAS auto-detect skipped the stretch), so it is not a valid degradation source");
                }

                var tileRows = ImmutableArray.CreateBuilder<DatasetTileExporter.TileManifestRow>();
                var degRows = ImmutableArray.CreateBuilder<DegradationRow>();
                var channels = master.ChannelCount;
                var cleanTiles = 0;
                var degradedTiles = 0;
                var parity = 0.0;

                var halfDiagonal = Math.Sqrt(((double)master.Width * master.Width) + ((double)master.Height * master.Height)) / 2.0;
                var centreX = master.Width / 2.0;
                var centreY = master.Height / 2.0;

                foreach (var cell in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var origin = new Point(cell.X, cell.Y);

                    var cleanFile = $"x{cell.X}_y{cell.Y}_{FrameClean}{DatasetTileExporter.TileExtension}";
                    var cleanMad = DatasetTileExporter.WriteTile(cleanStretched, origin, cell.TileSize, Path.Combine(tilesDir, cleanFile), sessionId);
                    tileRows.Add(new DatasetTileExporter.TileManifestRow(
                        Tile: $"tiles/{slug}/{cleanFile}", SessionId: sessionId, Camera: cell.Camera, Frame: FrameClean,
                        SourceFile: "", CellX: cell.X, CellY: cell.Y, TileSize: cell.TileSize, Channels: channels,
                        Gain: cell.Gain, ExposureSeconds: cell.ExposureSeconds, NoiseMad: cleanMad));
                    cleanTiles++;

                    if (cleanTiles == 1)
                    {
                        parity = ParityAgainstP0(options.BakeRoot, cell, cleanStretched, origin, sessionId);
                    }

                    var fieldRadius = halfDiagonal > 0
                        ? Math.Sqrt(Math.Pow(cell.X + (cell.TileSize / 2.0) - centreX, 2) + Math.Pow(cell.Y + (cell.TileSize / 2.0) - centreY, 2)) / halfDiagonal
                        : 0.0;

                    for (var draw = 0; draw < options.Draws; draw++)
                    {
                        var seed = DrawSeed(options.Seed, sessionId, cell.X, cell.Y, draw);
                        var row = DegradeCell(options, unitMaster, cell, origin, draw, seed, stackedFrames, fieldRadius, origMin, balances, tilesDir, slug, sessionId);
                        degRows.Add(row);
                        tileRows.Add(new DatasetTileExporter.TileManifestRow(
                            Tile: row.Tile, SessionId: sessionId, Camera: cell.Camera, Frame: row.Frame,
                            SourceFile: "", CellX: cell.X, CellY: cell.Y, TileSize: cell.TileSize, Channels: channels,
                            Gain: cell.Gain, ExposureSeconds: cell.ExposureSeconds, NoiseMad: row.OneSubSigma * row.DepthScale));
                        degradedTiles++;
                    }
                }

                await DatasetTileExporter.AppendManifestAsync(outTileManifest, tileRows.ToImmutable(), cancellationToken);
                await DatasetDegradationStore.AppendAsync(outDegManifest, degRows.ToImmutable(), cancellationToken);

                return new SessionResult(sessionId, selected.Count, cleanTiles, degradedTiles, parity, sw.ElapsedMilliseconds);
            }
            finally
            {
                if (!ReferenceEquals(cleanStretched, unitMaster))
                {
                    cleanStretched?.Release();
                }
                if (!ReferenceEquals(unitMaster, master))
                {
                    unitMaster?.Release();
                }
                master.Release();
            }
        }

        /// <summary>
        /// Degrades ONE cell and writes its tile. The margin is what makes a per-cell blur exact: the
        /// region is cut <see cref="PsfKernel.Radius"/> pixels wider on every side, convolved, and then
        /// cropped back, so the kernel never reaches for a pixel that is not there.
        /// </summary>
        private static DegradationRow DegradeCell(
            Options options,
            Image unitMaster,
            CellSpec cell,
            Point origin,
            int draw,
            int seed,
            int stackedFrames,
            double fieldRadius,
            float[] origMin,
            double[] balances,
            string tilesDir,
            string slug,
            string sessionId)
        {
            var rng = new Random(seed);
            var size = cell.TileSize;
            var channels = unitMaster.ChannelCount;

            PsfKernel? kernel = null;
            var extraFwhm = 0.0;
            var beta = 0.0;
            var elongation = 1.0;
            var positionAngle = 0.0;
            if (options.Mode == DegradationMode.Blur)
            {
                extraFwhm = LogUniform(rng, options.MinExtraFwhmPx, options.MaxExtraFwhmPx);
                // The archive's measured betas run roughly 1.5 to 8 with the heavy-winged end the common
                // one; a jittered draw over that span keeps the pairs inside what the estimator has seen.
                beta = LogUniform(rng, 1.5, 8.0);
                elongation = 1.0 + (rng.NextDouble() * 0.25);
                positionAngle = rng.NextDouble() * 180.0;
                kernel = PsfKernel.Moffat(extraFwhm, beta, elongation, positionAngle);
            }

            var margin = kernel?.Radius ?? 0;
            var cut = size + (2 * margin);
            var planes = new float[channels][,];
            var calibration = default(LinearDegradation.NoiseCalibration);
            var adjacent = double.NaN;
            var anchor = "";
            // The bottom of the range must sit BELOW the depth the model is deployed at, or the
            // conditioning plane at inference reads a level the model never saw in training, which is
            // the H0 domain skew wearing different clothes. Deployment depth is the master's own,
            // 1/sqrt(StackedFrames), and it varies by a factor of four across one bake (0.26 for a
            // 15-frame session, 0.062 for a 257-frame one), so no fixed bottom serves both ends: the
            // plan's 0.1 was reasoned on a 43-frame master and sits ABOVE the master depth of 34 of
            // this pool's 51 sessions. Deriving it per session removes the constant.
            var masterDepth = 1.0 / Math.Sqrt(Math.Max(1, stackedFrames));
            var minDepth = Math.Min(options.MinDepthScale, options.MasterDepthFraction * masterDepth);
            var depthScale = LogUniform(rng, minDepth, options.MaxDepthScale);

            for (var c = 0; c < channels; c++)
            {
                var region = CutClamped(unitMaster, c, origin.X - margin, origin.Y - margin, cut, cut);

                if (c == 0)
                {
                    // Calibrate ONCE, on the KEPT part of channel 0 (so a margin hanging over the canvas
                    // edge cannot drag the background down), and use it for every channel: the anchor is
                    // a sub's measured noise at a background level, and SigmaAt then follows each
                    // channel's own signal from there, which is what shot noise does when the channels
                    // share a gain.
                    var inner = CutClamped(unitMaster, 0, origin.X, origin.Y, size, size);
                    anchor = cell.SubNoiseMads.Count > 0 ? "sub-noisemad" : "master-mad";
                    calibration = cell.SubNoiseMads.Count > 0
                        ? LinearDegradation.NoiseCalibration.FromStretchedSubNoise(
                            inner, unitMaster.Pedestal, MedianOf(cell.SubNoiseMads), balances[0], origMin[0], stackedFrames)
                        : LinearDegradation.NoiseCalibration.Measure(inner, unitMaster.Pedestal, stackedFrames);
                    adjacent = LinearDegradation.NoiseCalibration.AdjacentDifferenceSigma(inner, size, size);
                }

                var shape = options.Shape == NoiseShape.White
                    ? NoiseField.White(cut, cut, rng)
                    : NoiseField.Warped(cut, cut, Math.Max(2, Math.Min(stackedFrames, 16)), rng, options.WarpResampleSigma);

                var degraded = kernel is null
                    ? region
                    : kernel.Convolve(region, cut, cut);
                LinearDegradation.AddNoiseInPlace(degraded, shape, calibration, depthScale);

                // Crop the margin off and lay the cell out as a plane.
                var plane = new float[size, size];
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        plane[y, x] = degraded[((y + margin) * cut) + x + margin];
                    }
                }
                planes[c] = plane;
            }

            var cellImage = new Image(planes, BitDepth.Float32, 1f, 0f, unitMaster.Pedestal, unitMaster.ImageMeta);
            Image stretchedCell = null!;
            try
            {
                stretchedCell = cellImage.MtfStretchWith(origMin, balances);
                var frame = FrameForDraw(draw);
                var file = $"x{cell.X}_y{cell.Y}_{frame}{DatasetTileExporter.TileExtension}";
                DatasetTileExporter.WriteTile(stretchedCell, Point.Empty, size, Path.Combine(tilesDir, file), sessionId);

                return new DegradationRow(
                    Tile: $"tiles/{slug}/{file}",
                    SessionId: sessionId,
                    Frame: frame,
                    CellX: cell.X,
                    CellY: cell.Y,
                    Draw: draw,
                    Mode: options.Mode.ToString(),
                    Shape: options.Shape.ToString(),
                    StackedFrames: stackedFrames,
                    DepthScale: depthScale,
                    OneSubSigma: calibration.OneSubSigmaAdu,
                    BackgroundLevel: calibration.BackgroundAdu,
                    AdjacentDiffSigma: adjacent,
                    ExtraFwhmPx: extraFwhm,
                    MoffatBeta: beta,
                    Elongation: elongation,
                    PositionAngleDeg: positionAngle,
                    FieldRadius: fieldRadius,
                    NoiseAnchor: anchor,
                    MasterDepth: masterDepth,
                    Seed: seed);
            }
            finally
            {
                stretchedCell?.Release();
                cellImage.Release();
            }
        }

        /// <summary>
        /// Compares the clean tile this exporter would write against the P0 tile of the same cell in the
        /// bake. A zero here says the retained master reproduces the frame P0 exported from, so a
        /// degraded draw really is the same pixels plus a degradation; anything else says the two paths
        /// have drifted and every pair in the run is suspect.
        /// </summary>
        private static double ParityAgainstP0(string bakeRoot, CellSpec cell, Image cleanStretched, Point origin, string sessionId)
        {
            var p0 = Path.Combine(bakeRoot, cell.MasterTileRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p0))
            {
                return double.NaN;
            }
            var stored = File.ReadAllBytes(p0);
            var mine = DatasetTileExporter.ExtractTileHalfs(cleanStretched, origin, cell.TileSize, out _);
            var mineBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<Half>(mine);
            if (stored.Length != mineBytes.Length)
            {
                return double.PositiveInfinity;
            }
            var worst = 0.0;
            for (var i = 0; i + 1 < stored.Length; i += 2)
            {
                var a = (float)BitConverter.ToHalf(stored, i);
                var b = (float)mine[i / 2];
                if (float.IsNaN(a) && float.IsNaN(b))
                {
                    continue;
                }
                worst = Math.Max(worst, Math.Abs(a - b));
            }
            return worst;
        }

        /// <summary>
        /// Measures band1/band0 for the injected draws and, with the identical code, for the real sub
        /// and half-master pairs of the same bake. Scene-free by differencing two frames of one scene,
        /// which is the only way to see noise shape without the nebulosity in the bands.
        /// </summary>
        /// <param name="degradedRoot">An exported degraded cache.</param>
        /// <param name="bakeRoot">The bake it came from, for the real-pair references. Null to skip them.</param>
        /// <param name="maxCells">Cells to sample per population.</param>
        public static async Task<ImmutableArray<ShapeMeasurement>> MeasureShapeAsync(
            string degradedRoot,
            string? bakeRoot,
            int maxCells = 64,
            CancellationToken cancellationToken = default)
        {
            var measurements = ImmutableArray.CreateBuilder<ShapeMeasurement>();

            var degraded = await ReadCellsAsync(Path.Combine(degradedRoot, DatasetTileExporter.ManifestFileName), cancellationToken);
            var injected = new List<double[]>();
            var pairs = 0;
            foreach (var (_, cells) in degraded)
            {
                foreach (var cell in cells)
                {
                    if (pairs >= maxCells)
                    {
                        break;
                    }
                    if (cell.OtherTiles.Count < 2)
                    {
                        continue;
                    }
                    var bands = DifferenceBands(degradedRoot, cell.OtherTiles[0], cell.OtherTiles[1], cell.TileSize);
                    if (bands is not null)
                    {
                        injected.Add(bands);
                        pairs++;
                    }
                }
                if (pairs >= maxCells)
                {
                    break;
                }
            }
            measurements.Add(Summarise("injected draws", injected));

            if (bakeRoot is not null && File.Exists(Path.Combine(bakeRoot, DatasetTileExporter.ManifestFileName)))
            {
                var real = await ReadCellsAsync(Path.Combine(bakeRoot, DatasetTileExporter.ManifestFileName), cancellationToken);
                var subBands = new List<double[]>();
                var halfBands = new List<double[]>();
                var subPairs = 0;
                var halfPairs = 0;
                foreach (var (_, cells) in real)
                {
                    foreach (var cell in cells)
                    {
                        if (subPairs < maxCells && cell.OtherTiles.Count >= 2)
                        {
                            var b = DifferenceBands(bakeRoot, cell.OtherTiles[0], cell.OtherTiles[1], cell.TileSize);
                            if (b is not null)
                            {
                                subBands.Add(b);
                                subPairs++;
                            }
                        }
                        if (halfPairs < maxCells && cell.HalfATile is { } ha && cell.HalfBTile is { } hb)
                        {
                            var b = DifferenceBands(bakeRoot, ha, hb, cell.TileSize);
                            if (b is not null)
                            {
                                halfBands.Add(b);
                                halfPairs++;
                            }
                        }
                    }
                    if (subPairs >= maxCells && halfPairs >= maxCells)
                    {
                        break;
                    }
                }
                measurements.Add(Summarise("real sub pairs", subBands));
                measurements.Add(Summarise("real half-master pairs", halfBands));
            }

            return measurements.ToImmutable();
        }

        private static ShapeMeasurement Summarise(string label, List<double[]> bands)
        {
            if (bands.Count == 0)
            {
                return new ShapeMeasurement(label, 0, double.NaN, double.NaN, double.NaN, double.NaN);
            }
            var b0 = bands.Select(static b => b[0]).ToArray();
            var b1 = bands.Select(static b => b[1]).ToArray();
            var b2 = bands.Select(static b => b[2]).ToArray();
            Array.Sort(b0);
            Array.Sort(b1);
            Array.Sort(b2);
            var m0 = b0[b0.Length / 2];
            var m1 = b1[b1.Length / 2];
            var m2 = b2[b2.Length / 2];
            return new ShapeMeasurement(label, bands.Count, m0, m1, m2, m0 > 0 ? m1 / m0 : double.NaN);
        }

        /// <summary>Band sigmas of the difference of two stored tiles' channel 0, or null if either is missing.</summary>
        private static double[]? DifferenceBands(string root, string relativeA, string relativeB, int tileSize)
        {
            var a = ReadTileChannel0(root, relativeA, tileSize);
            var b = ReadTileChannel0(root, relativeB, tileSize);
            if (a is null || b is null)
            {
                return null;
            }
            for (var i = 0; i < a.Length; i++)
            {
                a[i] -= b[i];
            }
            return NoiseField.BandSigmasOf(a, tileSize, tileSize);
        }

        private static float[]? ReadTileChannel0(string root, string relative, int tileSize)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var need = tileSize * tileSize * 2;
            if (bytes.Length < need)
            {
                return null;
            }
            var plane = new float[tileSize * tileSize];
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] = (float)BitConverter.ToHalf(bytes, i * 2);
            }
            return plane;
        }

        /// <summary>One cell as the P0 manifest describes it, plus the tiles it carries.</summary>
        /// <param name="SubNoiseMads">The <c>NoiseMad</c> of every sub tile of this cell, stretched-domain
        /// and unscaled, which is what the injected level is anchored on.</param>
        private sealed record CellSpec(
            int X,
            int Y,
            int TileSize,
            string Camera,
            int Gain,
            double ExposureSeconds,
            string MasterTileRelative,
            List<string> OtherTiles,
            List<double> SubNoiseMads,
            string? HalfATile,
            string? HalfBTile);

        private static double MedianOf(List<double> values)
        {
            var copy = values.ToArray();
            Array.Sort(copy);
            return copy.Length % 2 == 1
                ? copy[copy.Length / 2]
                : 0.5 * (copy[(copy.Length / 2) - 1] + copy[copy.Length / 2]);
        }

        private static async Task<Dictionary<string, List<CellSpec>>> ReadCellsAsync(string manifestPath, CancellationToken cancellationToken)
        {
            var bySession = new Dictionary<string, Dictionary<(int X, int Y), CellSpec>>(StringComparer.Ordinal);
            using var reader = new StreamReader(manifestPath);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                var row = JsonSerializer.Deserialize(line, DatasetDegradationJsonContext.Default.TileManifestRow);
                if (row is null)
                {
                    continue;
                }
                if (!bySession.TryGetValue(row.SessionId, out var cells))
                {
                    bySession[row.SessionId] = cells = [];
                }
                var key = (row.CellX, row.CellY);
                if (!cells.TryGetValue(key, out var spec))
                {
                    cells[key] = spec = new CellSpec(row.CellX, row.CellY, row.TileSize, row.Camera, row.Gain, row.ExposureSeconds, "", [], [], null, null);
                }
                cells[key] = row.Frame switch
                {
                    DatasetTileExporter.FrameMaster => spec with { MasterTileRelative = row.Tile },
                    DatasetTileExporter.FrameHalfMasterA => spec with { HalfATile = row.Tile },
                    DatasetTileExporter.FrameHalfMasterB => spec with { HalfBTile = row.Tile },
                    _ => Add(spec, row.Tile, row.Frame, row.NoiseMad),
                };
            }

            static CellSpec Add(CellSpec spec, string tile, string frame, double noiseMad)
            {
                spec.OtherTiles.Add(tile);
                // Only a REAL sub's noise anchors an injected level. A degraded cache read back through
                // this same method (the shape measurement does exactly that) must not feed its own
                // injected levels back in as if they were measurements.
                if (frame == DatasetTileExporter.FrameSub)
                {
                    spec.SubNoiseMads.Add(noiseMad);
                }
                return spec;
            }

            return bySession.ToDictionary(
                static kv => kv.Key,
                static kv => kv.Value.Values
                    .OrderBy(static c => c.Y).ThenBy(static c => c.X)
                    .ToList(),
                StringComparer.Ordinal);
        }

        private static async Task<HashSet<string>> ReadExportedSessionsAsync(string degradationManifest, CancellationToken cancellationToken)
        {
            var done = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(degradationManifest))
            {
                return done;
            }
            using var reader = new StreamReader(degradationManifest);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                var row = JsonSerializer.Deserialize(line, DatasetDegradationJsonContext.Default.DegradationRow);
                if (row is not null)
                {
                    done.Add(row.SessionId);
                }
            }
            return done;
        }

        /// <summary>Reads STACK_N off a retained master; the integration depth one sub's noise is derived from.</summary>
        private static int ReadStackCount(string masterPath)
        {
            if (Image.TryReadFitsHeader(masterPath, out var info) && info.StackedFrameCount > 0)
            {
                return info.StackedFrameCount;
            }
            // A master without the card cannot say how deep it is, and guessing sets every injected level
            // in the session. One is the honest floor: it makes the injected sigma the MASTER's own noise
            // rather than a sub's, which is visible in the row (StackedFrames = 1) rather than silent.
            return 1;
        }

        private static float[] CutClamped(Image img, int channel, int x0, int y0, int width, int height)
        {
            var src = img.GetChannelSpan(channel);
            var w = img.Width;
            var h = img.Height;
            var cut = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                var sy = Math.Clamp(y0 + y, 0, h - 1);
                for (var x = 0; x < width; x++)
                {
                    var sx = Math.Clamp(x0 + x, 0, w - 1);
                    cut[(y * width) + x] = src[(sy * w) + sx];
                }
            }
            return cut;
        }

        private static double LogUniform(Random rng, double lo, double hi)
            => Math.Exp(Math.Log(lo) + (rng.NextDouble() * (Math.Log(hi) - Math.Log(lo))));

        /// <summary>
        /// A per-draw seed that depends on everything identifying the draw, so a tile can be re-derived
        /// from its row alone and two cells never share a noise field.
        /// </summary>
        private static int DrawSeed(int baseSeed, string sessionId, int cellX, int cellY, int draw)
        {
            unchecked
            {
                var h = (uint)baseSeed * 2166136261u;
                foreach (var ch in sessionId)
                {
                    h = (h ^ ch) * 16777619u;
                }
                h = (h ^ (uint)cellX) * 16777619u;
                h = (h ^ (uint)cellY) * 16777619u;
                h = (h ^ (uint)draw) * 16777619u;
                return (int)(h & 0x7FFFFFFF);
            }
        }
    }

    /// <summary>Append-only store of <see cref="DatasetDegradationExporter.DegradationRow"/>.</summary>
    internal static class DatasetDegradationStore
    {
        public static async Task AppendAsync(string path, ImmutableArray<DatasetDegradationExporter.DegradationRow> rows, CancellationToken cancellationToken)
        {
            if (rows.IsDefaultOrEmpty)
            {
                return;
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var sb = new System.Text.StringBuilder();
            foreach (var row in rows)
            {
                sb.AppendLine(JsonSerializer.Serialize(row, DatasetDegradationJsonContext.Default.DegradationRow));
            }
            await File.AppendAllTextAsync(path, sb.ToString(), cancellationToken);
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    [JsonSerializable(typeof(DatasetDegradationExporter.DegradationRow))]
    [JsonSerializable(typeof(DatasetTileExporter.TileManifestRow))]
    internal sealed partial class DatasetDegradationJsonContext : JsonSerializerContext
    {
    }
}
