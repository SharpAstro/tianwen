using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.Imaging.Sources;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.AI.Imaging;

/// <summary>
/// Real pairs (docs/plans/star-remover-training.md, "Real pairs"): the Stars mode with nothing drawn. A cell's one draw is
/// the master's own cell, its real stars in, and its clean tile the starless plate's, so the plate builder's subtraction is
/// the truth. That truth holds only where the builder took a star out, so every draw carries its loss mask
/// (<see cref="RealPairMask"/>) and a trainer reads the export as a second cache with its masks.
/// </summary>
public static partial class DatasetDegradationExporter
{
    /// <summary>What a real pair's rows say where a drawn export names its placement and its profile.</summary>
    public const string RealPairsLabel = "Real";

    /// <summary><see cref="Options.SaturatedFraction"/>'s default, the one number for the option, the CLI and the check: a
    /// real-pairs export refuses any other value, which it would ignore.</summary>
    public const double DefaultSaturatedFraction = 0.25;

    /// <summary>
    /// Why <paramref name="options"/> cannot export real pairs, or null when it can or does not ask to
    /// (<see cref="Options.RealPairs"/>). A real pair has one draw and draws nothing, so an option that shapes a drawn star
    /// or a background, that names where a drawn star's profile comes from, or that sets the noise's level, shape or
    /// correlation has nothing to act on, and is refused by name before it is silently ignored. An option with a default
    /// that is no "off" (<see cref="Options.SaturatedFraction"/>) is refused when it differs from it. The warped shape's
    /// own parameters are read only under <see cref="NoiseShape.Warped"/>, which is itself refused.
    /// </summary>
    public static string? RealPairsRefusal(Options options)
    {
        if (!options.RealPairs)
        {
            return null;
        }
        if (options.Mode != DegradationMode.Stars)
        {
            return "real pairs are a Stars-mode export: the target is a starless plate";
        }
        if (options.Draws != 1)
        {
            return $"a real pair has one draw, the master's own cell; {options.Draws} draws were asked for";
        }
        (string Name, bool Given)[] drawn =
        [
            (nameof(Options.SyntheticBackground), options.SyntheticBackground),
            (nameof(Options.BrightKnotsWide), options.BrightKnotsWide),
            (nameof(Options.NebulaKnots), options.NebulaKnots),
            (nameof(Options.Steering), options.Steering is not null),
            (nameof(Options.NoiseScale), options.NoiseScale is not null),
            (nameof(Options.Placement), options.Placement != InjectionPlacement.Random),
            (nameof(Options.Profile), options.Profile != StarProfileFamily.Field),
            (nameof(Options.SaturatedFraction), options.SaturatedFraction != DefaultSaturatedFraction),
            (nameof(Options.PsfStorePath), options.PsfStorePath is not null),
            (nameof(Options.MeasureInjection), options.MeasureInjection),
            (nameof(Options.StarLobes), options.StarLobes),
            (nameof(Options.StarSubs), options.StarSubs),
            (nameof(Options.ChannelRelationSessions), options.ChannelRelationSessions is not null),
            (nameof(Options.StarShapesPath), options.StarShapesPath is not null),
            (nameof(Options.ChannelWingCut), options.ChannelWingCut is not null),
            (nameof(Options.ClampWingRelations), options.ClampWingRelations),
            (nameof(Options.OuterHaloPath), options.OuterHaloPath is not null),
            (nameof(Options.ChromaticWidthRatio), options.ChromaticWidthRatio is not null),
            (nameof(Options.SaturatedAtOwnShare), options.SaturatedAtOwnShare),
            (nameof(Options.SaturatedMeasured), options.SaturatedMeasured),
            (nameof(Options.BrightMeasured), options.BrightMeasured),
            (nameof(Options.HaloOverPlate), options.HaloOverPlate),
            (nameof(Options.StarBeta), options.StarBeta is not null),
            (nameof(Options.StarBetaDraw), options.StarBetaDraw is not null),
            (nameof(Options.KeptCores), options.KeptCores),
            (nameof(Options.FaintStarFloor), options.FaintStarFloor is not null),
            (nameof(Options.ChannelNoiseCorrelation), options.ChannelNoiseCorrelation),
            (nameof(Options.ChannelNoiseStructure), options.ChannelNoiseStructure),
            (nameof(Options.NoiseAsRecorded), options.NoiseAsRecorded),
            (nameof(Options.Shape), options.Shape != NoiseShape.White),
        ];
        foreach (var (name, given) in drawn)
        {
            if (given)
            {
                return $"real pairs draw no star, no background and no noise, so {name} has nothing to act on";
            }
        }
        return null;
    }

    /// <summary>
    /// Why an export of any mode cannot go into <paramref name="outDir"/>, or null when it can: a folder holds real pairs
    /// or another export's pairs (drawn stars, noise, blur: every mode writes the same tile names and manifests), never
    /// both, since one folder is one training set and a trainer reads its masks by one rule. A folder is
    /// a real-pairs export's from the moment its mask record (<see cref="StarRemovalMasks.RuleFileName"/>) says so, which a
    /// real-pairs run writes once its plates store has opened and before its first session (a run that cannot start
    /// leaves none). From then on, with rows or without, no other export goes beside that record. A run that failed or
    /// was stopped inside a session has no row but may have left that session's tiles and masks: a drawn export let in
    /// there would write its own tiles over them and keep their masks, which <c>plate-masks</c> skips as present. So
    /// the record is never dropped for another export; the real-pairs export runs there again, or the other one takes
    /// an empty folder. A folder that <paramref name="holdsRows"/> without that record holds another export's, and
    /// takes no real pair. What this does not hold apart is two exports that are neither a real pair's (a noise export
    /// into a folder of drawn stars): that is #2510.
    /// </summary>
    internal static string? RealPairsFolderRefusal(string outDir, bool realPairs, bool holdsRows)
    {
        var record = Path.Combine(outDir, StarRemovalMasks.RuleFileName);
        var startedAsRealPairs = File.Exists(record) && File.ReadAllText(record).Trim() == nameof(StarRemovalMasks.Rule.RealStars);
        if (startedAsRealPairs)
        {
            return realPairs
                ? null
                : $"the folder is a real-pairs export's (its {StarRemovalMasks.RuleFileName} says {nameof(StarRemovalMasks.Rule.RealStars)}) and this export is not one; one folder holds one rule, so export into an empty folder";
        }
        return realPairs && holdsRows
            ? "the folder already holds another export's rows and this export writes real pairs; one folder holds one rule, so export into an empty folder"
            : null;
    }

    private static async Task<SessionResult> ExportRealPairsSessionAsync(
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
        Image? unitMaster = null;
        Image? plateStretched = null;
        Image? masterStretched = null;
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
            var slug = DatasetTileExporter.Sanitize(sessionId);
            var catalogue = await StarlessCatalogue.ReadAsync(stars.CataloguePath(sessionId), cancellationToken);
            // The width the builder fitted its stars with is the one its footprints and its kept sources were sized by.
            var field = await StarlessFieldProfile.ReadAsync(stars.ProfilePath(sessionId), cancellationToken)
                ?? throw new InvalidOperationException(
                    $"{sessionId}: the plate has no field profile ({stars.ProfilePath(sessionId)}); give the store its profiles with tianwen dataset starless-plates --profiles-only");
            var fwhm = field.Luminance.Fwhm;
            var subtractedPath = StarlessMasks.SubtractedPathFor(stars.PlatesDir, slug);
            var subtracted = SourceDetectionWriter.ReadMask(subtractedPath)
                ?? throw new IOException($"{sessionId}: the plate's subtracted footprint ({subtractedPath}) is missing or unreadable; a real pair counts only inside it");
            if (subtracted.GetLength(0) != master.Height || subtracted.GetLength(1) != master.Width)
            {
                throw new InvalidOperationException(
                    $"{sessionId}: the subtracted footprint is {subtracted.GetLength(1)} by {subtracted.GetLength(0)}, its master {master.Width} by {master.Height}");
            }
            var channels = master.ChannelCount;
            var counted = RealPairMask.Counted(subtracted, catalogue, fwhm, EitherAbsent(master.AbsentPixels(), plate.AbsentPixels()));

            var selected = SelectCells(options, sessionId, cells, extraCells);
            var tilesDir = Path.Combine(options.OutDir, "tiles", slug);
            Directory.CreateDirectory(tilesDir);
            var stackedFrames = ReadStackCount(RetainedMasterStore.PathFor(options.BakeRoot, sessionId));
            var masterDepth = 1.0 / Math.Sqrt(Math.Max(1, stackedFrames));

            // Both sides on the MASTER's scale and stretched with one set of parameters: the master's own by default, which
            // is what inference stretches this master with, so the input tile is the tile a runner cuts from it.
            var divisor = DatasetTileExporter.UnitDivisor(master);
            unitPlate = DatasetTileExporter.ToUnitRange(plate, divisor);
            unitMaster = DatasetTileExporter.ToUnitRange(master, divisor);
            var (origMin, balances, skySlopeRatio) = StarsStretch(sessionId, master, divisor, unitPlate, options.StretchFromMaster, options.BlackPointSigma);
            plateStretched = unitPlate.MtfStretchWith(origMin, balances);
            masterStretched = unitMaster.MtfStretchWith(origMin, balances);

            var tileRows = ImmutableArray.CreateBuilder<DatasetTileExporter.TileManifestRow>();
            var degRows = ImmutableArray.CreateBuilder<DegradationRow>();
            var injectionRows = ImmutableArray.CreateBuilder<InjectionRow>();
            var halfDiagonal = Math.Sqrt(((double)master.Width * master.Width) + ((double)master.Height * master.Height)) / 2.0;
            var rim = AiNafnetInputs.StitchBorderPx;
            var frame = FrameForDraw(0);
            var exportedCells = 0;
            var emptyCells = 0;
            long countedInside = 0, inside = 0;
            foreach (var cell in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = cell.TileSize;
                var keep = new float[size * size];
                long cellCounted = 0, cellInside = 0;
                for (var y = 0; y < size; y++)
                {
                    var fy = cell.Y + y;
                    for (var x = 0; x < size; x++)
                    {
                        var fx = cell.X + x;
                        var counts = fx >= 0 && fy >= 0 && fx < master.Width && fy < master.Height && counted[fy, fx];
                        keep[(y * size) + x] = counts ? 1f : 0f;
                        if (x >= rim && y >= rim && x < size - rim && y < size - rim)
                        {
                            cellInside++;
                            if (counts)
                            {
                                cellCounted++;
                            }
                        }
                    }
                }
                if (cellCounted == 0)
                {
                    // Nothing of this cell would reach the loss: a batch row spent on it teaches nothing.
                    emptyCells++;
                    continue;
                }
                // The master's recorded noise, as it is: the input is the master itself, and that record is what a runner
                // reads off it for the conditioning plane.
                var calibrations = CellMasterCalibration(cell, channels, stackedFrames)
                    ?? throw new InvalidOperationException($"{sessionId}: cell x{cell.X} y{cell.Y} has no master row with a recorded calibration");
                var origin = new PixelPoint(cell.X, cell.Y);
                var cleanFile = $"x{cell.X}_y{cell.Y}_{FrameClean}{DatasetTileExporter.TileExtension}";
                var file = $"x{cell.X}_y{cell.Y}_{frame}{DatasetTileExporter.TileExtension}";
                var sigmaFile = SigmaPathFor(file);

                var cleanMad = DatasetTileExporter.WriteTile(plateStretched, origin, size, Path.Combine(tilesDir, cleanFile), sessionId);
                WriteMasterSigmaTile(unitPlate, origin, size, origMin, balances, calibrations, masterDepth, Path.Combine(tilesDir, SigmaPathFor(cleanFile)));
                DatasetTileExporter.WriteTile(masterStretched, origin, size, Path.Combine(tilesDir, file), sessionId);
                WriteMasterSigmaTile(unitMaster, origin, size, origMin, balances, calibrations, masterDepth, Path.Combine(tilesDir, sigmaFile));
                WritePlaneFile(keep, Path.Combine(tilesDir, StarRemovalMasks.KeepPathFor(file)));

                var tile = $"tiles/{slug}/{file}";
                var fieldRadius = halfDiagonal > 0
                    ? Math.Sqrt(Math.Pow(cell.X + (size / 2.0) - (master.Width / 2.0), 2) + Math.Pow(cell.Y + (size / 2.0) - (master.Height / 2.0), 2)) / halfDiagonal
                    : 0.0;
                tileRows.Add(new DatasetTileExporter.TileManifestRow(
                    Tile: $"tiles/{slug}/{cleanFile}", SessionId: sessionId, Camera: cell.Camera, Frame: FrameClean,
                    SourceFile: "", CellX: cell.X, CellY: cell.Y, TileSize: size, Channels: channels,
                    Gain: cell.Gain, ExposureSeconds: cell.ExposureSeconds, NoiseMad: cleanMad));
                tileRows.Add(new DatasetTileExporter.TileManifestRow(
                    Tile: tile, SessionId: sessionId, Camera: cell.Camera, Frame: frame,
                    SourceFile: "", CellX: cell.X, CellY: cell.Y, TileSize: size, Channels: channels,
                    Gain: cell.Gain, ExposureSeconds: cell.ExposureSeconds, NoiseMad: calibrations[0].OneSubSigmaAdu * masterDepth));
                degRows.Add(new DegradationRow(
                    Tile: tile,
                    SessionId: sessionId,
                    Frame: frame,
                    CellX: cell.X,
                    CellY: cell.Y,
                    Draw: 0,
                    Mode: options.Mode.ToString(),
                    Shape: options.Shape.ToString(),
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
                    Seed: DrawSeed(options.Seed, sessionId, cell.X, cell.Y, 0),
                    SigmaTile: $"tiles/{slug}/{sigmaFile}",
                    OneSubSigmaPerChannel: [.. calibrations.Select(static k => k.OneSubSigmaAdu)],
                    BackgroundPerChannel: [.. calibrations.Select(static k => k.BackgroundAdu)],
                    InjectedStars: 0,
                    InjectedSaturated: 0,
                    Placement: RealPairsLabel,
                    Profile: RealPairsLabel,
                    InjectionShortfall: 0,
                    Stretch: options.StretchFromMaster ? "master" : "plate",
                    StretchSkySlopeRatio: skySlopeRatio,
                    StretchBlackPointSigma: options.BlackPointSigma));
                injectionRows.Add(new InjectionRow(
                    Tile: tile, SessionId: sessionId, CellX: cell.X, CellY: cell.Y, Draw: 0,
                    Placement: RealPairsLabel, Profile: RealPairsLabel, Requested: 0, Placed: 0, SaturatedFallback: false, Stars: []));
                exportedCells++;
                countedInside += cellCounted;
                inside += cellInside;
            }

            var kept = catalogue.Count(static s => s.Outcome != StarFitOutcome.Subtracted);
            var saturated = catalogue.Count(static s => s.Outcome == StarFitOutcome.Subtracted && s.Saturated);
            logger?.LogInformation(
                "[degrade] {Session}: real pairs, {Cells} cells ({Empty} with no counted pixel left out); the mask counts {Share} of their pixels inside the rim; PSF {Fwhm} px, {Kept} kept sources and {Saturated} saturated stars set aside; stretched with the {Source}'s parameters",
                sessionId, exportedCells, emptyCells,
                inside > 0 ? ((double)countedInside / inside).ToString("P1", CultureInfo.InvariantCulture) : "none",
                fwhm.ToString("F2", CultureInfo.InvariantCulture), kept, saturated, options.StretchFromMaster ? "master" : "plate");

            await DatasetTileExporter.AppendManifestAsync(outTileManifest, tileRows.ToImmutable(), cancellationToken);
            await AppendInjectionsAsync(Path.Combine(options.OutDir, InjectionManifestFileName), injectionRows.ToImmutable(), cancellationToken);
            // The degradation row last: it is what marks a session done for a resume.
            await DatasetDegradationStore.AppendAsync(outDegManifest, degRows.ToImmutable(), cancellationToken);
            return new SessionResult(sessionId, exportedCells, exportedCells, exportedCells, 0.0, sw.ElapsedMilliseconds);
        }
        finally
        {
            // Always stretches of their own (MtfStretchWith), never the frames they were made from.
            masterStretched?.Release();
            plateStretched?.Release();
            if (!ReferenceEquals(unitMaster, master))
            {
                unitMaster?.Release();
            }
            if (!ReferenceEquals(unitPlate, plate))
            {
                unitPlate?.Release();
            }
            plate?.Release();
            master.Release();
        }
    }

    // The pixels absent from either frame, or null when neither has any.
    private static BitMatrix? EitherAbsent(BitMatrix? a, BitMatrix? b)
    {
        if (a is not { } first)
        {
            return b;
        }
        if (b is not { } second)
        {
            return a;
        }
        var height = first.GetLength(0);
        var width = first.GetLength(1);
        var either = new BitMatrix(height, width);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                either[y, x] = first[y, x] || second[y, x];
            }
        }
        return either;
    }
}
