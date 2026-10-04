using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.AI.Imaging;

/// <summary>
/// The Stars mode (docs/plans/star-remover-training.md, R1): the clean tile is a session's starless plate, each draw the
/// plate plus injected stars and their own noise, and every injected star is recorded in <see cref="InjectionManifestFileName"/>.
/// </summary>
public static partial class DatasetDegradationExporter
{
    /// <summary>Every injected star, one row per draw.</summary>
    public const string InjectionManifestFileName = "injections.jsonl";

    /// <summary>Stars are placed this far past a cell's edge too, so a tile's edge cuts stars as a real tile's does.</summary>
    public const int InjectionMarginPx = 8;

    /// <summary>A star is rendered out to where its light falls below this fraction of the sky's noise at the master's depth.</summary>
    public const double InjectionFloorSigma = 0.02;

    /// <summary>With <see cref="Options.MeasureInjection"/>: the injected stars read back, one row per session.</summary>
    public const string InjectionMeasureFileName = "injection-measures.jsonl";

    /// <summary>An injected star is fitted back only at this significance in a channel (its peak over the sky's noise at the
    /// master's depth) and up to <see cref="MeasureMaxSignificance"/>, R1's prediction's band.</summary>
    public const double MeasureMinSignificance = 30.0;

    /// <summary>The upper end of the band <see cref="MeasureMinSignificance"/> opens.</summary>
    public const double MeasureMaxSignificance = 1000.0;

    /// <summary>
    /// One session's injected stars read back (docs/plans/star-remover-training.md, R1's predictions): each channel's fitted
    /// FWHM and beta over the drawn ones at the median (unsaturated stars in the significance band, isolated from the other
    /// injected stars), the fitted beta itself (the Gaussian arm's check), the injected saturated stars' plateaus and edges
    /// against the master's own (<see cref="InjectionMeasure.SaturatedShape"/>, one definition), the placement, and the
    /// pixels that passed a saturated star's clip in the noise-free render.
    /// </summary>
    public sealed record InjectionMeasureRow(
        string SessionId, string Placement, string Profile, int Draws, int Requested, int Placed,
        int[] Fitted, double[] FwhmRatio, double[] BetaRatio, double[] BetaFitted, double AxisRatioDrawn, double AxisRatioFitted,
        int InjectedSaturated, double InjectedPlateauPx, double InjectedEdgePx, int RealSaturated, double RealPlateauPx, double RealEdgePx,
        int ClipExceeded);

    /// <summary>What one session's draws collect for its <see cref="InjectionMeasureRow"/>.</summary>
    private sealed class InjectionMeasures
    {
        public InjectionMeasures(int channels)
        {
            FwhmRatio = new List<double>[channels];
            BetaRatio = new List<double>[channels];
            BetaFitted = new List<double>[channels];
            for (var c = 0; c < channels; c++)
            {
                FwhmRatio[c] = new List<double>();
                BetaRatio[c] = new List<double>();
                BetaFitted[c] = new List<double>();
            }
        }

        public List<double>[] FwhmRatio { get; }

        public List<double>[] BetaRatio { get; }

        public List<double>[] BetaFitted { get; }

        public List<double> AxisRatioDrawn { get; } = new List<double>();

        public List<double> AxisRatioFitted { get; } = new List<double>();

        public List<SaturatedStarShape> Injected { get; } = new List<SaturatedStarShape>();

        public List<SaturatedStarShape> Real { get; } = new List<SaturatedStarShape>();

        public int Requested { get; set; }

        public int Placed { get; set; }

        public int ClipExceeded { get; set; }

        private readonly Dictionary<int, (double FwhmPx, double Beta)?> _reference = new Dictionary<int, (double FwhmPx, double Beta)?>();

        // What a star of this channel's profile is read back against: its drawn FWHM and beta for a Moffat or a Gaussian, the
        // noise-free fit of the profile alone for the field profile, which is no single Moffat (InjectionMeasure.ReferenceFit).
        public (double FwhmPx, double Beta)? ReferenceFor(int channel, StarProfile drawn)
        {
            if (drawn.Family != StarProfileFamily.Field)
            {
                return (drawn.FwhmPx, drawn.Beta);
            }
            if (!_reference.TryGetValue(channel, out var reference))
            {
                reference = InjectionMeasure.ReferenceFit(drawn);
                _reference[channel] = reference;
            }
            return reference;
        }
    }

    /// <summary>One injected star, in the cell's coordinates (pixel centres at integers), per channel where it varies.</summary>
    public sealed record InjectedStarRow(
        double X, double Y, double[] Amplitude, double[] FwhmPx, double[] Beta, double AxisRatio, double PositionAngleDeg,
        bool Saturated, double[]? ClipLevel);

    /// <summary>One draw's injection: what was asked for, what was placed, and every star.</summary>
    public sealed record InjectionRow(
        string Tile, string SessionId, int CellX, int CellY, int Draw, string Placement, string Profile, int Requested, int Placed,
        bool SaturatedFallback, InjectedStarRow[] Stars);

    /// <summary>What the Stars mode reads once per run: the plates and the PSF store.</summary>
    internal sealed class StarsContext
    {
        internal StarsContext(string platesDir, Dictionary<string, DatasetPsfNoiseReport.SessionPsf> psf)
        {
            PlatesDir = platesDir;
            Psf = psf;
        }

        public string PlatesDir { get; }

        public Dictionary<string, DatasetPsfNoiseReport.SessionPsf> Psf { get; }

        public static async Task<StarsContext> OpenAsync(Options options, ILogger? logger, CancellationToken cancellationToken)
        {
            if (options.PlatesRoot is not { } platesRoot)
            {
                throw new ArgumentException("the Stars mode needs a starless-plates store (PlatesRoot)", nameof(options));
            }
            if (options.NoiseAnchor != NoiseAnchorKind.MasterCalibration)
            {
                // The injected stars' noise is the master's own at its depth, per channel: only the master's recorded
                // calibration says what that is (the sub anchors are on the subs' scales, the half pairs need the master's
                // cells, and the plate has none of its own).
                throw new ArgumentException("the Stars mode takes its noise from the master's recorded calibration (NoiseAnchor MasterCalibration)", nameof(options));
            }
            var platesDir = Path.Combine(platesRoot, "plates");
            if (!Directory.Exists(platesDir))
            {
                throw new DirectoryNotFoundException($"no plates folder in the starless-plates store {platesRoot}");
            }
            var psfPath = options.PsfStorePath ?? Path.Combine(options.BakeRoot, "stats", DatasetPsfStore.FileName);
            var psf = await DatasetPsfStore.ReadAsync(psfPath, logger, cancellationToken);
            if (psf.Count == 0)
            {
                throw new FileNotFoundException("the PSF store is empty or missing; the injected stars take each channel's profile from it", psfPath);
            }
            return new StarsContext(platesDir, psf);
        }

        public string PlatePath(string sessionId) => Path.Combine(PlatesDir, DatasetTileExporter.Sanitize(sessionId) + "_plate.fits");

        public string CataloguePath(string sessionId) => StarlessCatalogue.PathFor(PlatesDir, DatasetTileExporter.Sanitize(sessionId));

        public bool HasPlate(string sessionId) => File.Exists(PlatePath(sessionId)) && File.Exists(CataloguePath(sessionId));

        /// <summary>The plate builder's field profile beside the plate (<see cref="StarlessFieldProfile"/>).</summary>
        public string ProfilePath(string sessionId) => StarlessFieldProfile.PathFor(PlatesDir, DatasetTileExporter.Sanitize(sessionId));

        /// <summary>
        /// Each channel's FWHM and Moffat beta from the master's own profile fits. A channel the store could not measure takes
        /// the nearest measured channel's (the lower one on a tie): a line filter on a colour sensor leaves a channel too few
        /// stars to stack (the Rim Nebula's SII night has no blue profile), and its stars are still there to inject. Each
        /// borrowing is named in <paramref name="borrowed"/>; a session with no measured channel at all is refused.
        /// </summary>
        public (double Fwhm, double Beta)[] ChannelPsf(string sessionId, int channels, out string? borrowed)
        {
            borrowed = null;
            if (!Psf.TryGetValue(sessionId, out var record) || record.MasterProfiles is not { } profiles || profiles.Length != channels)
            {
                throw new InvalidOperationException($"{sessionId}: the PSF store has no per-channel master profile for this session");
            }
            static bool Measured(PsfProfileFit.Result? p) => p is { } r && r.Fwhm > 0 && r.MoffatBeta > 0;
            var result = new (double, double)[channels];
            for (var c = 0; c < channels; c++)
            {
                var source = -1;
                for (var d = 0; d < channels && source < 0; d++)
                {
                    if (c - d >= 0 && Measured(profiles[c - d]))
                    {
                        source = c - d;
                    }
                    else if (c + d < channels && Measured(profiles[c + d]))
                    {
                        source = c + d;
                    }
                }
                if (source < 0 || profiles[source] is not { } p)
                {
                    throw new InvalidOperationException($"{sessionId}: no channel has a measured master profile");
                }
                if (source != c)
                {
                    borrowed = (borrowed is null ? "" : borrowed + ", ") + $"channel {c} from channel {source}";
                }
                result[c] = (p.Fwhm, p.MoffatBeta);
            }
            return result;
        }
    }

    private static async Task<SessionResult> ExportStarsSessionAsync(
        Options options,
        StarsContext stars,
        string sessionId,
        IReadOnlyList<CellSpec> cells,
        HashSet<(int X, int Y)>? extraCells,
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
        Image? plate = null;
        Image? unitPlate = null;
        Image? plateStretched = null;
        try
        {
            if (!Image.TryReadFitsFile(stars.PlatePath(sessionId), out plate))
            {
                throw new IOException($"the starless plate of {sessionId} could not be read");
            }
            if (plate.Shape != master.Shape)
            {
                throw new InvalidOperationException($"{sessionId}: plate {plate.Shape} is not its master's shape {master.Shape}");
            }
            var catalogue = await StarlessCatalogue.ReadAsync(stars.CataloguePath(sessionId), cancellationToken);
            var channels = master.ChannelCount;
            var psf = stars.ChannelPsf(sessionId, channels, out var borrowed);
            if (borrowed is not null)
            {
                logger?.LogWarning("[degrade] {Session}: the PSF store measured no profile for {Borrowed}", sessionId, borrowed);
            }

            var selected = SelectCells(options, sessionId, cells, extraCells);
            var slug = DatasetTileExporter.Sanitize(sessionId);
            var tilesDir = Path.Combine(options.OutDir, "tiles", slug);
            Directory.CreateDirectory(tilesDir);

            var masterPath = RetainedMasterStore.PathFor(options.BakeRoot, sessionId);
            var stackedFrames = ReadStackCount(masterPath);
            var strategy = DatasetGradientReport.ReadMasterCards(masterPath).Strategy;
            var sessionOptions = options.DrizzleWarpResampleSigma is { } drizzleSigma && strategy == DrizzleStrategy
                ? options with { WarpResampleSigma = drizzleSigma }
                : options;

            // The plate on its MASTER's scale, and both sides stretched with the plate's (the target's) parameters, H6.
            var divisor = DatasetTileExporter.UnitDivisor(master);
            unitPlate = DatasetTileExporter.ToUnitRange(plate, divisor);
            var (stretched, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(unitPlate);
            plateStretched = stretched;
            if (!applied || origMin is null || balances is null)
            {
                throw new InvalidOperationException($"{sessionId}: the starless plate did not read as linear, so it is not a valid injection target");
            }

            var absent = plate.AbsentPixels();
            // The field profile is the one every catalogued amplitude was fitted with; a plate store from before profiles were
            // kept has none, and drawing those amplitudes with another profile is what made saturated tops too wide.
            var field = await StarlessFieldProfile.ReadAsync(stars.ProfilePath(sessionId), cancellationToken);
            if (options.Profile == StarProfileFamily.Field && field is null)
            {
                throw new InvalidOperationException(
                    $"{sessionId}: the plate has no field profile ({stars.ProfilePath(sessionId)}); give the store its profiles with tianwen dataset starless-plates --profiles-only");
            }
            var population = InjectionPopulation.Build(catalogue, master, plate, 1.0 / divisor, psf, absent, field);
            var drawnWith = options.Profile == StarProfileFamily.Field && field is { } f
                ? f.Channels.Select(static p => string.Create(CultureInfo.InvariantCulture, $"{p.Fwhm:F2} px b{p.Beta:F2} + table {p.Table.Length}"))
                : psf.Select(static p => string.Create(CultureInfo.InvariantCulture, $"{p.Fwhm:F2} px b{p.Beta:F2}"));
            logger?.LogInformation(
                "[degrade] {Session}: {Sites} subtracted sites, {Pool} stars to draw brightness from, {Saturated} saturated, {Moments} measured for elongation; {Family} profile {Psf}",
                sessionId, population.Sites, population.AmplitudePool, population.SaturatedPool, population.MomentPool, options.Profile,
                string.Join(" / ", drawnWith));

            var measures = options.MeasureInjection ? new InjectionMeasures(channels) : null;
            if (measures is not null)
            {
                // The master's own saturated stars, read as the injected ones will be, on its luminance.
                var lum = Luminance(master);
                foreach (var star in catalogue)
                {
                    if (InjectionPopulation.InSaturatedPool(star, channels)
                        && InjectionMeasure.SaturatedShape(lum, master.Width, master.Height, star.X, star.Y) is { } shape)
                    {
                        measures.Real.Add(shape);
                    }
                }
            }

            var tileRows = ImmutableArray.CreateBuilder<DatasetTileExporter.TileManifestRow>();
            var degRows = ImmutableArray.CreateBuilder<DegradationRow>();
            var injectionRows = ImmutableArray.CreateBuilder<InjectionRow>();
            var halfDiagonal = Math.Sqrt(((double)master.Width * master.Width) + ((double)master.Height * master.Height)) / 2.0;
            var cleanTiles = 0;
            var degradedTiles = 0;
            foreach (var cell in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var origin = new PixelPoint(cell.X, cell.Y);
                var cleanFile = $"x{cell.X}_y{cell.Y}_{FrameClean}{DatasetTileExporter.TileExtension}";
                var cleanMad = DatasetTileExporter.WriteTile(plateStretched, origin, cell.TileSize, Path.Combine(tilesDir, cleanFile), sessionId);
                tileRows.Add(new DatasetTileExporter.TileManifestRow(
                    Tile: $"tiles/{slug}/{cleanFile}", SessionId: sessionId, Camera: cell.Camera, Frame: FrameClean,
                    SourceFile: "", CellX: cell.X, CellY: cell.Y, TileSize: cell.TileSize, Channels: channels,
                    Gain: cell.Gain, ExposureSeconds: cell.ExposureSeconds, NoiseMad: cleanMad));
                cleanTiles++;

                var calibrations = CellMasterCalibration(cell, channels, stackedFrames)
                    ?? throw new InvalidOperationException($"{sessionId}: cell x{cell.X} y{cell.Y} has no master row with a recorded calibration");
                var masterDepth = 1.0 / Math.Sqrt(Math.Max(1, stackedFrames));
                WriteMasterSigmaTile(unitPlate, origin, cell.TileSize, origMin, balances, calibrations, masterDepth,
                    Path.Combine(tilesDir, SigmaPathFor(cleanFile)));

                var fieldRadius = halfDiagonal > 0
                    ? Math.Sqrt(Math.Pow(cell.X + (cell.TileSize / 2.0) - (master.Width / 2.0), 2) + Math.Pow(cell.Y + (cell.TileSize / 2.0) - (master.Height / 2.0), 2)) / halfDiagonal
                    : 0.0;
                for (var draw = 0; draw < options.Draws; draw++)
                {
                    var seed = DrawSeed(options.Seed, sessionId, cell.X, cell.Y, draw);
                    var (row, injection) = InjectCell(sessionOptions, unitPlate, absent, population, cell, draw, seed, stackedFrames, fieldRadius,
                        origMin, balances, calibrations, tilesDir, slug, sessionId, measures);
                    degRows.Add(row);
                    injectionRows.Add(injection);
                    tileRows.Add(new DatasetTileExporter.TileManifestRow(
                        Tile: row.Tile, SessionId: sessionId, Camera: cell.Camera, Frame: row.Frame,
                        SourceFile: "", CellX: cell.X, CellY: cell.Y, TileSize: cell.TileSize, Channels: channels,
                        Gain: cell.Gain, ExposureSeconds: cell.ExposureSeconds, NoiseMad: row.OneSubSigma * row.DepthScale));
                    degradedTiles++;
                }
            }

            await DatasetTileExporter.AppendManifestAsync(outTileManifest, tileRows.ToImmutable(), cancellationToken);
            await AppendInjectionsAsync(Path.Combine(options.OutDir, InjectionManifestFileName), injectionRows.ToImmutable(), cancellationToken);
            if (measures is not null)
            {
                var measureRow = Summarise(measures, sessionId, options, selected.Count * options.Draws);
                await File.AppendAllTextAsync(Path.Combine(options.OutDir, InjectionMeasureFileName),
                    JsonSerializer.Serialize(measureRow, DatasetDegradationJsonContext.Default.InjectionMeasureRow) + "\n", cancellationToken);
                logger?.LogInformation(
                    "[degrade] {Session}: placed {Placed}/{Requested}; FWHM fitted/drawn {Fwhm}, beta {Beta} over {Fitted} stars; saturated plateau {InjectedPlateau} px against the master's {RealPlateau}, edge {InjectedEdge} against {RealEdge}; {Clip} px past a clip",
                    sessionId, measureRow.Placed, measureRow.Requested,
                    string.Join("/", measureRow.FwhmRatio.Select(static v => v.ToString("F3", CultureInfo.InvariantCulture))),
                    string.Join("/", measureRow.BetaRatio.Select(static v => v.ToString("F3", CultureInfo.InvariantCulture))),
                    string.Join("/", measureRow.Fitted),
                    measureRow.InjectedPlateauPx, measureRow.RealPlateauPx, measureRow.InjectedEdgePx, measureRow.RealEdgePx, measureRow.ClipExceeded);
            }
            // The degradation row last: it is what marks a session done for a resume.
            await DatasetDegradationStore.AppendAsync(outDegManifest, degRows.ToImmutable(), cancellationToken);

            // No P0 parity: the clean tile is the plate, which P0 never exported.
            return new SessionResult(sessionId, selected.Count, cleanTiles, degradedTiles, 0.0, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (!ReferenceEquals(plateStretched, unitPlate))
            {
                plateStretched?.Release();
            }
            if (!ReferenceEquals(unitPlate, plate))
            {
                unitPlate?.Release();
            }
            plate?.Release();
            master.Release();
        }
    }

    /// <summary>
    /// One draw of one cell: the plan, the render, the stars' own noise, the stretch, the tile and its plane, and the rows.
    /// The placement and brightness, the saturated stars' virtual subs and the noise each take a stream of their own from
    /// the draw's seed, so changing one never moves the others.
    /// </summary>
    private static (DegradationRow Row, InjectionRow Injection) InjectCell(
        Options options,
        Image unitPlate,
        BitMatrix? absent,
        InjectionPopulation population,
        CellSpec cell,
        int draw,
        int seed,
        int stackedFrames,
        double fieldRadius,
        float[] origMin,
        double[] balances,
        LinearDegradation.NoiseCalibration[] calibrations,
        string tilesDir,
        string slug,
        string sessionId,
        InjectionMeasures? measures)
    {
        var size = cell.TileSize;
        var channels = unitPlate.ChannelCount;
        var plan = population.Plan(cell.X, cell.Y, size, InjectionMarginPx, options.Placement, options.Profile, options.SaturatedFraction, new Random(seed));

        var basePlanes = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            basePlanes[c] = CutClamped(unitPlate, c, cell.X, cell.Y, size, size);
        }
        BitMatrix? cellAbsent = null;
        if (absent is { } frameAbsent)
        {
            var mask = new BitMatrix(size, size);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var fx = cell.X + x;
                    var fy = cell.Y + y;
                    mask[y, x] = fx < 0 || fy < 0 || fx >= unitPlate.Width || fy >= unitPlate.Height || frameAbsent[fy, fx];
                }
            }
            cellAbsent = mask;
        }

        var masterDepth = 1.0 / Math.Sqrt(Math.Max(1, stackedFrames));
        var floors = calibrations.Select(k => InjectionFloorSigma * k.SigmaAt(k.BackgroundAdu, masterDepth)).ToArray();
        var local = plan.Stars.Select(s => s with { X = s.X - cell.X, Y = s.Y - cell.Y }).ToArray();
        var render = StarInjection.Render(basePlanes, size, size, cellAbsent, local, floors, new Random(seed ^ 0x6d2b79f5));

        var (drawShape, drawSigma) = DrawNoiseShape(options, seed);

        // The stars' own shot noise at the master's depth: what the plate plus a star carries beyond what the plate already
        // does, on a field of the master's shape, and only the share of a saturated star's subs that did not clip.
        var noiseRng = new Random(seed ^ 0x1b873593);
        var planes = new float[channels][,];
        var levelPlanes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var field = NoiseFieldFor(drawShape, drawSigma, size, stackedFrames, noiseRng);
            var plane = new float[size, size];
            var level = new float[size, size];
            var calibration = calibrations[c];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = y * size + x;
                    var withStars = render.Planes[c][i];
                    var without = basePlanes[c][i];
                    level[y, x] = withStars;
                    if (!float.IsFinite(withStars) || withStars == without)
                    {
                        plane[y, x] = withStars;
                        continue;
                    }
                    var sigmaWith = calibration.SigmaAt(withStars, masterDepth);
                    var sigmaWithout = calibration.SigmaAt(without, masterDepth);
                    var extra = Math.Sqrt(Math.Max(0.0, sigmaWith * sigmaWith - sigmaWithout * sigmaWithout)) * render.UnclippedFraction[c][i];
                    plane[y, x] = (float)(withStars + extra * field[i]);
                }
            }
            planes[c] = plane;
            levelPlanes[c] = level;
        }

        if (measures is not null)
        {
            Measure(measures, plan, local, render, basePlanes, planes, floors, size);
        }

        var frame = FrameForDraw(draw);
        var file = $"x{cell.X}_y{cell.Y}_{frame}{DatasetTileExporter.TileExtension}";
        var sigmaFile = SigmaPathFor(file);
        var cellImage = new Image(planes, BitDepth.Float32, 1f, 0f, unitPlate.Pedestal, unitPlate.ImageMeta);
        var levelCell = new Image(levelPlanes, BitDepth.Float32, 1f, 0f, unitPlate.Pedestal, unitPlate.ImageMeta);
        Image? stretchedCell = null;
        Image? levelStretched = null;
        try
        {
            stretchedCell = cellImage.MtfStretchWith(origMin, balances);
            DatasetTileExporter.WriteTile(stretchedCell, PixelPoint.Empty, size, Path.Combine(tilesDir, file), sessionId);
            levelStretched = levelCell.MtfStretchWith(origMin, balances);
            WriteSigmaTile(levelStretched, origMin, balances, calibrations, masterDepth, Path.Combine(tilesDir, sigmaFile));
        }
        finally
        {
            levelStretched?.Release();
            stretchedCell?.Release();
            levelCell.Release();
            cellImage.Release();
        }

        var saturatedCount = plan.Stars.Count(static s => s.Saturated);
        var row = new DegradationRow(
            Tile: $"tiles/{slug}/{file}",
            SessionId: sessionId,
            Frame: frame,
            CellX: cell.X,
            CellY: cell.Y,
            Draw: draw,
            Mode: options.Mode.ToString(),
            Shape: drawShape.ToString(),
            StackedFrames: stackedFrames,
            DepthScale: masterDepth,
            OneSubSigma: calibrations[0].OneSubSigmaAdu,
            BackgroundLevel: calibrations[0].BackgroundAdu,
            AdjacentDiffSigma: double.NaN,
            ExtraFwhmPx: 0.0,
            MoffatBeta: 0.0,
            Elongation: 1.0,
            PositionAngleDeg: 0.0,
            FieldRadius: fieldRadius,
            NoiseAnchor: "master-calibration",
            MasterDepth: masterDepth,
            Seed: seed,
            WarpSigma: drawShape == NoiseShape.Warped ? drawSigma : null,
            SigmaTile: $"tiles/{slug}/{sigmaFile}",
            OneSubSigmaPerChannel: [.. calibrations.Select(static k => k.OneSubSigmaAdu)],
            BackgroundPerChannel: [.. calibrations.Select(static k => k.BackgroundAdu)],
            InjectedStars: plan.Stars.Length,
            InjectedSaturated: saturatedCount,
            Placement: options.Placement.ToString(),
            Profile: options.Profile.ToString(),
            InjectionShortfall: plan.Requested - plan.Placed);

        var injection = new InjectionRow(
            Tile: row.Tile, SessionId: sessionId, CellX: cell.X, CellY: cell.Y, Draw: draw,
            Placement: options.Placement.ToString(), Profile: options.Profile.ToString(), Requested: plan.Requested, Placed: plan.Placed,
            SaturatedFallback: plan.SaturatedFallback,
            Stars: [.. local.Select(static s => new InjectedStarRow(
                s.X, s.Y,
                [.. s.Amplitudes],
                [.. s.Profiles.Select(static p => p.FwhmPx)],
                [.. s.Profiles.Select(static p => p.Beta)],
                s.Profiles.Length > 0 ? s.Profiles[0].AxisRatio : 1.0,
                s.Profiles.Length > 0 ? s.Profiles[0].PositionAngleRad * 180.0 / Math.PI : 0.0,
                s.Saturated,
                s.Saturated ? [.. s.ClipLevels] : null))]);
        return (row, injection);
    }

    /// <summary>
    /// Reads one draw's injected stars back off its linear planes: each unsaturated star in the significance band, clear of
    /// the tile's edge and of every other injected star, fitted per channel on the draw minus the plate (its own noise in
    /// it); each saturated star's plateau and edge on the draw's luminance; and any pixel the noise-free render put past a
    /// saturated star's clip within 3 px of its centre, where the plate was under the clip.
    /// </summary>
    private static void Measure(
        InjectionMeasures measures, InjectionPlan plan, InjectedStar[] stars, InjectionRender render, float[][] basePlanes, float[][,] planes,
        double[] floors, int size)
    {
        measures.Requested += plan.Requested;
        measures.Placed += plan.Placed;
        var channels = basePlanes.Length;
        var lum = new float[size * size];
        for (var c = 0; c < channels; c++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    lum[(y * size) + x] += planes[c][y, x] / channels;
                }
            }
        }
        float[][]? diff = null;
        foreach (var star in stars)
        {
            if (star.Saturated)
            {
                if (InjectionMeasure.SaturatedShape(lum, size, size, star.X, star.Y) is { } shape)
                {
                    measures.Injected.Add(shape);
                }
                for (var c = 0; c < channels; c++)
                {
                    for (var y = Math.Max(0, (int)star.Y - 3); y <= Math.Min(size - 1, (int)star.Y + 3); y++)
                    {
                        for (var x = Math.Max(0, (int)star.X - 3); x <= Math.Min(size - 1, (int)star.X + 3); x++)
                        {
                            if ((x - star.X) * (x - star.X) + (y - star.Y) * (y - star.Y) <= 9.0
                                && render.Planes[c][(y * size) + x] > star.ClipLevels[c] + 1e-6
                                && basePlanes[c][(y * size) + x] < star.ClipLevels[c])
                            {
                                measures.ClipExceeded++;
                            }
                        }
                    }
                }
                continue;
            }
            var widest = star.Profiles.Max(static p => p.FwhmPx);
            var reach = Math.Max(4.0, 2.5 * 1.2 * widest) + 1.0;
            if (star.X < reach || star.Y < reach || star.X > size - 1 - reach || star.Y > size - 1 - reach)
            {
                continue;
            }
            var clear = Math.Max(12.0, 4.0 * widest);
            if (stars.Any(o => !ReferenceEquals(o, star) && ((o.X - star.X) * (o.X - star.X)) + ((o.Y - star.Y) * (o.Y - star.Y)) < clear * clear))
            {
                continue;
            }
            if (diff is null)
            {
                diff = new float[channels][];
                for (var c = 0; c < channels; c++)
                {
                    diff[c] = new float[size * size];
                    for (var y = 0; y < size; y++)
                    {
                        for (var x = 0; x < size; x++)
                        {
                            diff[c][(y * size) + x] = planes[c][y, x] - basePlanes[c][(y * size) + x];
                        }
                    }
                }
            }
            for (var c = 0; c < channels; c++)
            {
                var significance = star.Amplitudes[c] / (floors[c] / InjectionFloorSigma);
                if (!(significance >= MeasureMinSignificance && significance <= MeasureMaxSignificance))
                {
                    continue;
                }
                var drawn = star.Profiles[c];
                if (measures.ReferenceFor(c, drawn) is { } reference
                    && InjectionMeasure.FitMoffat(diff[c], size, size, star.X, star.Y, drawn.FwhmPx * 1.2) is { Converged: true } fit)
                {
                    measures.FwhmRatio[c].Add(fit.FwhmPx / reference.FwhmPx);
                    measures.BetaRatio[c].Add(fit.Beta / reference.Beta);
                    measures.BetaFitted[c].Add(fit.Beta);
                    if (c == 0)
                    {
                        measures.AxisRatioDrawn.Add(drawn.AxisRatio);
                        measures.AxisRatioFitted.Add(fit.AxisRatio);
                    }
                }
            }
        }
    }

    private static InjectionMeasureRow Summarise(InjectionMeasures m, string sessionId, Options options, int draws)
    {
        var channels = m.FwhmRatio.Length;
        return new InjectionMeasureRow(
            sessionId, options.Placement.ToString(), options.Profile.ToString(), draws, m.Requested, m.Placed,
            [.. Enumerable.Range(0, channels).Select(c => m.FwhmRatio[c].Count)],
            [.. Enumerable.Range(0, channels).Select(c => MedianOf(m.FwhmRatio[c]))],
            [.. Enumerable.Range(0, channels).Select(c => MedianOf(m.BetaRatio[c]))],
            [.. Enumerable.Range(0, channels).Select(c => MedianOf(m.BetaFitted[c]))],
            MedianOf(m.AxisRatioDrawn), MedianOf(m.AxisRatioFitted),
            m.Injected.Count, MedianOf([.. m.Injected.Select(static s => (double)s.PlateauPx)]),
            MedianOf([.. m.Injected.Select(static s => s.EdgePx).Where(double.IsFinite)]),
            m.Real.Count, MedianOf([.. m.Real.Select(static s => (double)s.PlateauPx)]),
            MedianOf([.. m.Real.Select(static s => s.EdgePx).Where(double.IsFinite)]),
            m.ClipExceeded);
    }

    /// <summary>The mean of an image's channels, row-major.</summary>
    internal static float[] Luminance(Image image)
    {
        var (channels, width, height) = image.Shape;
        var lum = new float[width * height];
        for (var c = 0; c < channels; c++)
        {
            var plane = image.GetChannelSpan(c);
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] += plane[i] / channels;
            }
        }
        return lum;
    }

    private static async Task AppendInjectionsAsync(string path, ImmutableArray<InjectionRow> rows, CancellationToken cancellationToken)
    {
        if (rows.IsDefaultOrEmpty)
        {
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine(JsonSerializer.Serialize(row, DatasetDegradationJsonContext.Default.InjectionRow));
        }
        await File.AppendAllTextAsync(path, sb.ToString(), cancellationToken);
    }
}
