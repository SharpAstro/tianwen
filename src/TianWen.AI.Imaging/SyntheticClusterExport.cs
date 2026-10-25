using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.StarRemoval;

namespace TianWen.AI.Imaging;

/// <summary>
/// The synthetic crowded clusters as star-remover training tiles (docs/plans/star-remover-training.md, "Synthetic crowded
/// clusters"): many skies drawn from one master (<see cref="SyntheticClusterScene"/>), each rendered in every optics asked for
/// and cut into tiles laid out as a Stars-mode export's (<see cref="DatasetDegradationExporter"/>), so
/// <c>n2n_smoke.py --prepare</c> packs them into a cache unchanged. Per 256 px cell of a scene:
/// <list type="bullet">
/// <item><c>master</c>, the target: the scene's NOISY starless truth (<see cref="SyntheticClusterRender.StarlessNoisy"/>), the
/// truth with the master's noise at its own level, as a Stars-mode draw's target is its plate, noise and all; a noise-free
/// target would teach denoising on these tiles alone.</item>
/// <item><c>deg000</c>, the input: the composite, which is that noisy truth plus the stars plus the stars' own shot noise
/// (<c>SyntheticClusterScene.AddNoise</c>), so input less target is the stars and their own noise only.</item>
/// <item>Each tile's conditioning plane (<see cref="DatasetDegradationExporter.SigmaPathFor"/>) from its noise-free level and the
/// scene's own noise model (<see cref="SyntheticClusterSource.NoiseCalibration"/>), as an injected draw's is, and the
/// draw's loss mask (<see cref="StarRemovalMasks.KeepPathFor"/>), all ones: the truth holds no star a plate kept, and its
/// knots must count, since the net is to learn to keep them.</item>
/// </list>
/// Both sides are stretched with one set of parameters, chosen as a Stars-mode export chooses them
/// (<see cref="DatasetDegradationExporter.StarsStretch"/>): the master's own by default, as inference stretches it (#1776), or
/// the plate's (H6, every cluster export before), the plate put on the master's scale first. One output holds one stretch
/// (<see cref="StretchFileName"/>), so a resumed run cannot mix them. A sky is held out WHOLE, by its seed (<see cref="HeldOut"/>): every optics and every cell of
/// it, never a tile of a training sky. Each sky's rows land in a fragment of their own once all its tiles are written, so a
/// run resumes where it stopped and several runs over disjoint seeds share one output; the manifest, the split lists and the
/// scene table are rebuilt from the fragments at the end of every run.
/// </summary>
public static class SyntheticClusterExport
{
    /// <summary>A cell's side, the trainer's tile.</summary>
    public const int TileSize = 256;

    /// <summary>The scene table, one row per rendered scene.</summary>
    public const string SceneTableFileName = "clusters.tsv";

    /// <summary>The training scenes' session names, for <c>--train-from-list</c>.</summary>
    public const string TrainListFileName = "clusters-train.txt";

    /// <summary>The held-out scenes' session names, for <c>--val-from-list</c>.</summary>
    public const string ValListFileName = "clusters-val.txt";

    /// <summary>Where each sky's manifest rows and scene rows wait until the manifest is rebuilt.</summary>
    public const string FragmentDir = "skies";

    /// <summary>Whose stretch the output's tiles are in, <c>master</c> or <c>plate</c>, written by its first run.</summary>
    public const string StretchFileName = "stretch.txt";

    /// <summary>Every <see cref="ValEvery"/>-th seed is a held-out sky (seeds 10, 20, ...): one in ten, by seed alone, so a
    /// split never depends on which runs drew which seeds.</summary>
    public const int ValEvery = 10;

    /// <summary>Whether sky <paramref name="seed"/> is held out.</summary>
    public static bool HeldOut(int seed) => seed % ValEvery == 0;

    /// <summary>
    /// One sky's own draw: a rotation, whether it has a nebula, and its cluster, from ranges that bracket the two skies gated
    /// first (2,500 stars at core radii of 25 and 80 px, tidal radii of 300 and 450, none and six compact groups): 1,500 to
    /// 3,000 resolved cluster stars, a core radius log-uniform from 20 to 90 px, a tidal radius from 250 to 500 px and at
    /// least three cores, and up to eight groups.
    /// </summary>
    public sealed record Sky(int Seed, double RotationDeg, bool Nebula, int ClusterStars, double CoreRadiusPx, double TidalRadiusPx, int SubClusters)
    {
        /// <summary>Whether this sky is held out.</summary>
        public bool IsHeldOut => HeldOut(Seed);

        /// <summary>The sky drawn for <paramref name="seed"/>, a nebula with probability <paramref name="nebulaShare"/>.</summary>
        public static Sky Draw(int seed, double nebulaShare)
        {
            var rng = new Random(unchecked((seed * 7919) + 0x3c6ef372));
            var rotation = rng.NextDouble() * 360.0;
            var nebula = rng.NextDouble() < nebulaShare;
            var stars = 1500 + rng.Next(1501);
            var core = 20.0 * Math.Pow(90.0 / 20.0, rng.NextDouble());
            var tidal = Math.Max(3.0 * core, 250.0 + (250.0 * rng.NextDouble()));
            var groups = rng.Next(0, 9);
            return new Sky(seed, rotation, nebula, stars, core, tidal, groups);
        }

        /// <summary>The scene this sky is in <paramref name="optics"/>, from the run's base spec.</summary>
        public SyntheticClusterSpec SpecFrom(SyntheticClusterSpec baseSpec, ClusterOptics optics)
            => baseSpec with
            {
                Seed = Seed, RotationDeg = RotationDeg, Optics = optics, NebulaStrength = Nebula ? 1.0 : 0.0,
                ClusterStars = ClusterStars, CoreRadiusPx = CoreRadiusPx, TidalRadiusPx = TidalRadiusPx, SubClusters = SubClusters,
            };
    }

    /// <summary>One scene's session name: the master, the sky and the optics (a cell is keyed by its session and place).</summary>
    public static string SessionFor(string masterName, int seed, ClusterOptics optics)
        => string.Create(CultureInfo.InvariantCulture, $"synthetic-cluster|{masterName}|s{seed:D4}|{optics.ToString().ToLowerInvariant()}");

    /// <summary>What a run wrote.</summary>
    /// <param name="Skies">Skies rendered by this run.</param>
    /// <param name="Skipped">Skies whose fragment was already there.</param>
    /// <param name="TrainTiles">Input tiles of the training skies in the rebuilt manifest, every run's.</param>
    /// <param name="ValTiles">Input tiles of the held-out skies.</param>
    public sealed record Result(int Skies, int Skipped, int TrainTiles, int ValTiles);

    /// <summary>
    /// Renders the skies <paramref name="firstSeed"/> to <paramref name="firstSeed"/> + <paramref name="skies"/> - 1 of
    /// <paramref name="run"/>'s master in each of <paramref name="optics"/>, <paramref name="concurrency"/> at a time over one
    /// shared source, and writes their tiles under <paramref name="run"/>'s output, stretched with the master's parameters or,
    /// with <paramref name="stretchFromMaster"/> false, the plate's; then rebuilds the manifest, the lists and the scene table
    /// from every fragment there.
    /// </summary>
    public static async Task<Result> RunAsync(SyntheticClusterRun run, int firstSeed, int skies, ImmutableArray<ClusterOptics> optics,
        double nebulaShare, int concurrency, bool stretchFromMaster, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var stretchName = stretchFromMaster ? "master" : "plate";
        Directory.CreateDirectory(run.OutDir);
        var stretchFile = Path.Combine(run.OutDir, StretchFileName);
        if (File.Exists(stretchFile) && (await File.ReadAllTextAsync(stretchFile, cancellationToken)).Trim() is var written && written != stretchName)
        {
            throw new InvalidOperationException($"{run.OutDir} holds tiles in the {written}'s stretch; a run in the {stretchName}'s needs an output of its own");
        }
        var (master, source, line) = await run.OpenSourceAsync(cancellationToken);
        progress?.Report(line);
        if (!Image.TryReadFitsFile(run.PlatePath, out var plate))
        {
            throw new InvalidDataException($"not a readable FITS image: {run.PlatePath}");
        }
        // The plate on its master's scale, and both sides stretched with the parameters a Stars-mode export would take.
        var divisor = DatasetTileExporter.UnitDivisor(master);
        var unitPlate = DatasetTileExporter.ToUnitRange(plate, divisor);
        float[] origMin;
        double[] balances;
        double[] skySlopeRatio;
        try
        {
            (origMin, balances, skySlopeRatio) = DatasetDegradationExporter.StarsStretch(Path.GetFileName(run.MasterPath), master, divisor, unitPlate, stretchFromMaster);
        }
        finally
        {
            if (!ReferenceEquals(unitPlate, plate))
            {
                unitPlate.Release();
            }
            plate.Release();
        }
        progress?.Report(string.Create(CultureInfo.InvariantCulture,
            $"stretched with the {stretchName}'s parameters; the master's slope at its sky over the plate's {string.Join(" / ", skySlopeRatio.Select(static r => r.ToString("F2", CultureInfo.InvariantCulture)))}"));
        await WriteAtomicAsync(stretchFile, stretchName + "\n", cancellationToken);
        // The scene's own noise model, on the unit scale: its conditioning planes are then the noise the tiles hold.
        var calibrations = new LinearDegradation.NoiseCalibration[master.ChannelCount];
        for (var c = 0; c < calibrations.Length; c++)
        {
            var k = source.NoiseCalibration(c);
            calibrations[c] = k with { BackgroundAdu = k.BackgroundAdu / divisor, OneSubSigmaAdu = k.OneSubSigmaAdu / divisor };
        }
        var masterName = Path.GetFileNameWithoutExtension(run.MasterPath);
        var camera = master.ImageMeta.Instrument;
        Directory.CreateDirectory(Path.Combine(run.OutDir, FragmentDir));

        var done = 0;
        var skipped = 0;
        await Parallel.ForEachAsync(Enumerable.Range(firstSeed, skies), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = cancellationToken },
            async (seed, ct) =>
            {
                var fragment = Path.Combine(run.OutDir, FragmentDir, string.Create(CultureInfo.InvariantCulture, $"s{seed:D4}.jsonl"));
                if (File.Exists(fragment))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }
                var sky = Sky.Draw(seed, nebulaShare);
                var rows = new StringBuilder();
                var scenes = new StringBuilder();
                foreach (var o in optics)
                {
                    ct.ThrowIfCancellationRequested();
                    var spec = sky.SpecFrom(run.Base, o);
                    var render = SyntheticClusterScene.Render(source, spec);
                    var session = SessionFor(masterName, seed, o);
                    foreach (var row in WriteScene(run.OutDir, session, camera, render, spec.Size, divisor, origMin, balances, calibrations))
                    {
                        rows.Append(JsonSerializer.Serialize(row, DatasetManifestJsonContext.Default.TileManifestRow)).Append('\n');
                    }
                    scenes.Append(SceneRow(sky, o, render)).Append('\n');
                    progress?.Report($"{session}: {SyntheticClusterRun.Describe(render, spec)}");
                }
                // The scene rows first, then the tile rows through a rename: a fragment is there only once every tile is.
                await File.WriteAllTextAsync(Path.ChangeExtension(fragment, ".tsv"), scenes.ToString(), ct);
                await File.WriteAllTextAsync(fragment + ".tmp", rows.ToString(), ct);
                File.Move(fragment + ".tmp", fragment, overwrite: true);
                Interlocked.Increment(ref done);
            });

        var (train, val) = await RebuildAsync(run.OutDir, cancellationToken);
        return new Result(done, skipped, train, val);
    }

    /// <summary>
    /// Writes one rendered scene's cells and returns their manifest rows: per cell the noisy truth as the target
    /// (<c>master</c>) and the composite as the one draw (<c>deg000</c>), each with its conditioning plane from its own
    /// noise-free level, and the draw's all-ones loss mask.
    /// </summary>
    internal static ImmutableArray<DatasetTileExporter.TileManifestRow> WriteScene(string outDir, string session, string camera, SyntheticClusterRender render,
        int size, float divisor, float[] origMin, double[] balances, LinearDegradation.NoiseCalibration[] calibrations)
    {
        var channels = render.Composite.Length;
        var levelComposite = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            // The composite's noise-free level: the starless truth plus the stars (the stars truth is the noise-free composite
            // less the starless truth).
            var level = new float[size * size];
            for (var i = 0; i < level.Length; i++)
            {
                level[i] = render.Starless[c][i] + render.Stars[c][i];
            }
            levelComposite[c] = level;
        }
        var slug = DatasetTileExporter.Sanitize(session);
        var tilesDir = Path.Combine(outDir, "tiles", slug);
        Directory.CreateDirectory(tilesDir);
        var target = Stretched(render.StarlessNoisy, size, divisor, origMin, balances);
        var input = Stretched(render.Composite, size, divisor, origMin, balances);
        var targetLevel = Stretched(render.Starless, size, divisor, origMin, balances);
        var inputLevel = Stretched(levelComposite, size, divisor, origMin, balances);
        var ones = new float[TileSize * TileSize];
        Array.Fill(ones, 1f);
        var rows = ImmutableArray.CreateBuilder<DatasetTileExporter.TileManifestRow>();
        try
        {
            for (var y0 = 0; y0 + TileSize <= size; y0 += TileSize)
            {
                for (var x0 = 0; x0 + TileSize <= size; x0 += TileSize)
                {
                    var origin = new PixelPoint(x0, y0);
                    foreach (var (frame, tile, level) in new[] { (DatasetDegradationExporter.FrameClean, target, targetLevel), (DatasetDegradationExporter.FrameForDraw(0), input, inputLevel) })
                    {
                        var file = string.Create(CultureInfo.InvariantCulture, $"x{x0}_y{y0}_{frame}{DatasetTileExporter.TileExtension}");
                        var path = Path.Combine(tilesDir, file);
                        var mad = DatasetTileExporter.WriteTile(tile, origin, TileSize, path, session);
                        var cell = Cut(level, origin, TileSize);
                        try
                        {
                            DatasetDegradationExporter.WriteSigmaTile(cell, origMin, balances, calibrations, 1.0, DatasetDegradationExporter.SigmaPathFor(path));
                        }
                        finally
                        {
                            cell.Release();
                        }
                        if (frame != DatasetDegradationExporter.FrameClean)
                        {
                            DatasetDegradationExporter.WritePlaneFile(ones, StarRemovalMasks.KeepPathFor(path));
                        }
                        rows.Add(new DatasetTileExporter.TileManifestRow(
                            Tile: $"tiles/{slug}/{file}", SessionId: session, Camera: camera, Frame: frame, SourceFile: "",
                            CellX: x0, CellY: y0, TileSize: TileSize, Channels: channels, Gain: 0, ExposureSeconds: 0.0, NoiseMad: mad,
                            SigmaTile: $"tiles/{slug}/{DatasetDegradationExporter.SigmaPathFor(file)}"));
                    }
                }
            }
        }
        finally
        {
            target.Release();
            input.Release();
            targetLevel.Release();
            inputLevel.Release();
        }
        return rows.ToImmutable();
    }

    /// <summary>
    /// Rebuilds the manifest (<see cref="DatasetTileExporter.ManifestFileName"/>), the train and val session lists and the scene
    /// table from every sky's fragment in <paramref name="outDir"/>, in seed order. Returns the input tiles of each split.
    /// </summary>
    public static async Task<(int TrainTiles, int ValTiles)> RebuildAsync(string outDir, CancellationToken cancellationToken)
    {
        var fragments = Directory.GetFiles(Path.Combine(outDir, FragmentDir), "s*.jsonl").Order(StringComparer.Ordinal).ToArray();
        var manifest = new StringBuilder();
        var table = new StringBuilder(SceneTableHeader + "\n");
        var train = new SortedSet<string>(StringComparer.Ordinal);
        var val = new SortedSet<string>(StringComparer.Ordinal);
        var (trainTiles, valTiles) = (0, 0);
        foreach (var fragment in fragments)
        {
            var seed = int.Parse(Path.GetFileNameWithoutExtension(fragment)[1..], CultureInfo.InvariantCulture);
            foreach (var line in await File.ReadAllLinesAsync(fragment, cancellationToken))
            {
                if (line.Length == 0 || JsonSerializer.Deserialize(line, DatasetManifestJsonContext.Default.TileManifestRow) is not { } row)
                {
                    continue;
                }
                manifest.Append(line).Append('\n');
                (HeldOut(seed) ? val : train).Add(row.SessionId);
                if (row.Frame != DatasetDegradationExporter.FrameClean)
                {
                    if (HeldOut(seed))
                    {
                        valTiles++;
                    }
                    else
                    {
                        trainTiles++;
                    }
                }
            }
            table.Append(await File.ReadAllTextAsync(Path.ChangeExtension(fragment, ".tsv"), cancellationToken));
        }
        await WriteAtomicAsync(Path.Combine(outDir, DatasetTileExporter.ManifestFileName), manifest.ToString(), cancellationToken);
        await WriteAtomicAsync(Path.Combine(outDir, TrainListFileName), string.Concat(train.Select(static s => s + "\n")), cancellationToken);
        await WriteAtomicAsync(Path.Combine(outDir, ValListFileName), string.Concat(val.Select(static s => s + "\n")), cancellationToken);
        await WriteAtomicAsync(Path.Combine(outDir, SceneTableFileName), table.ToString(), cancellationToken);
        return (trainTiles, valTiles);
    }

    /// <summary>The scene table's columns.</summary>
    public const string SceneTableHeader =
        "seed\toptics\theld_out\trotation_deg\tnebula\tcluster_stars\tcore_radius_px\ttidal_radius_px\tsub_clusters\tresolved_stars\tunresolved_stars\tknots";

    private static string SceneRow(Sky sky, ClusterOptics optics, SyntheticClusterRender render)
        => string.Create(CultureInfo.InvariantCulture,
            $"{sky.Seed}\t{optics.ToString().ToLowerInvariant()}\t{(sky.IsHeldOut ? 1 : 0)}\t{sky.RotationDeg:F2}\t{(sky.Nebula ? 1 : 0)}\t{sky.ClusterStars}\t" +
            $"{sky.CoreRadiusPx:F1}\t{sky.TidalRadiusPx:F1}\t{sky.SubClusters}\t{render.Truth.Length}\t{render.GlowStars}\t{render.Knots.Length}");

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path + ".tmp", text, cancellationToken);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // A scene's planes (the master's units) on the master's unit scale, stretched with the plate's parameters.
    private static Image Stretched(float[][] planes, int size, float divisor, float[] origMin, double[] balances)
    {
        var data = new float[planes.Length][,];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = new float[size, size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    plane[y, x] = planes[c][(y * size) + x] / divisor;
                }
            }
            data[c] = plane;
        }
        var unit = new Image(data, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        try
        {
            return unit.MtfStretchWith(origMin, balances);
        }
        finally
        {
            unit.Release();
        }
    }

    // One cell of a stretched scene, as an image of its own (the conditioning plane is read per cell, as an export's is).
    private static Image Cut(Image stretched, PixelPoint origin, int tile)
    {
        var data = new float[stretched.ChannelCount][,];
        for (var c = 0; c < data.Length; c++)
        {
            var span = stretched.GetChannelSpan(c);
            var plane = new float[tile, tile];
            for (var y = 0; y < tile; y++)
            {
                for (var x = 0; x < tile; x++)
                {
                    plane[y, x] = span[((origin.Y + y) * stretched.Width) + origin.X + x];
                }
            }
            data[c] = plane;
        }
        return new Image(data, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }
}
