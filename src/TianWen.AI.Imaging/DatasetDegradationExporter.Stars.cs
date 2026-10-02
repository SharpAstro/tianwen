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

    /// <summary>One injected star, in the cell's coordinates (pixel centres at integers), per channel where it varies.</summary>
    public sealed record InjectedStarRow(
        double X, double Y, double[] Amplitude, double[] FwhmPx, double[] Beta, double AxisRatio, double PositionAngleDeg,
        bool Saturated, double[]? ClipLevel);

    /// <summary>One draw's injection: what was asked for, what was placed, and every star.</summary>
    public sealed record InjectionRow(
        string Tile, string SessionId, int CellX, int CellY, int Draw, string Placement, string Profile, int Requested, int Placed,
        bool SaturatedFallback, InjectedStarRow[] Stars);

    /// <summary>What the Stars mode reads once per run: the plates and the PSF store.</summary>
    private sealed class StarsContext
    {
        private StarsContext(string platesDir, Dictionary<string, DatasetPsfNoiseReport.SessionPsf> psf)
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

        /// <summary>Each channel's FWHM and Moffat beta from the master's own profile fits.</summary>
        public (double Fwhm, double Beta)[] ChannelPsf(string sessionId, int channels)
        {
            if (!Psf.TryGetValue(sessionId, out var record) || record.MasterProfiles is not { } profiles || profiles.Length != channels)
            {
                throw new InvalidOperationException($"{sessionId}: the PSF store has no per-channel master profile for this session");
            }
            var result = new (double, double)[channels];
            for (var c = 0; c < channels; c++)
            {
                if (profiles[c] is not { } p || !(p.Fwhm > 0) || !(p.MoffatBeta > 0))
                {
                    throw new InvalidOperationException($"{sessionId}: channel {c} has no measured master profile");
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
            var psf = stars.ChannelPsf(sessionId, channels);

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
            var population = InjectionPopulation.Build(catalogue, master, plate, 1.0 / divisor, psf, absent);
            logger?.LogInformation(
                "[degrade] {Session}: {Sites} subtracted sites, {Pool} stars to draw brightness from, {Saturated} saturated, {Moments} measured for elongation; PSF {Psf}",
                sessionId, population.Sites, population.AmplitudePool, population.SaturatedPool, population.MomentPool,
                string.Join(" / ", psf.Select(static p => string.Create(CultureInfo.InvariantCulture, $"{p.Fwhm:F2} px b{p.Beta:F2}"))));

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
                        origMin, balances, calibrations, tilesDir, slug, sessionId);
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
        string sessionId)
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
