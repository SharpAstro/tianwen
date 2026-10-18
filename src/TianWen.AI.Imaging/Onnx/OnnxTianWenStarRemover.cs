using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.AI.Inference;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// TianWen's own star remover over a whole frame: one or more of the R2 line's ONNX models
/// (<c>docs/plans/star-remover-training.md</c>), each shown every tile in up to eight flips and quarter turns, their outputs
/// turned back and averaged, in overlapping windows blended by a triangle. Nothing of the weights ships with it: a caller
/// names the files, and each loads only against the contract beside it (<see cref="ModelContract"/>), fed exactly as the
/// shipped denoiser is (<see cref="N2nDenoiser.Feed"/>: the same graph shape, an image and its per-pixel noise plane).
/// </summary>
/// <remarks>
/// <para><b>Why not <see cref="N2nLinearRunner"/>.</b> The denoiser's runner puts each chunk's median back after the net
/// (<c>RestoreLevel</c>), which corrects a denoiser's level prior and would put every removed star's light back as a haze
/// here. It also blends its chunks over a 32 px feather; the averaging read (E2, E3, E6) showed a star remover gains from
/// every extra view it is given, and a window predicts worst near its edge, where a star is cut and the padding invents
/// what lies past it.</para>
///
/// <para><b>The windows (<see cref="StarRemoverRunner"/>).</b> The tile's 16 px rim
/// (<see cref="AiNafnetInputs.StitchBorderPx"/>) is never used, as the scorer never reads it. Its interior is weighted by a
/// triangle rising from zero at the rim to one at the centre, and the windows step by half the interior, so two triangles
/// sum to one everywhere in the frame and each pixel is decided mostly by the window that sees it centrally: StarNet2's
/// tiling (reproduced exactly against its exe, 2026-10-18), on our tile. The frame is mirrored out by the rim plus a step
/// so its own edges get the same overlap.</para>
///
/// <para><b>The domain is the exporter's, as for the denoiser.</b> One whole-frame stretch
/// (<see cref="ChunkedNafnetRunner.ApplyInputStretch"/>), one noise calibration per channel
/// (<see cref="N2nLinearRunner.FrameNoise"/>), each window's plane from the very tile the net is fed
/// (<see cref="N2nLinearRunner.ChunkPlane"/>), averaging in the stretched domain where the models were averaged when E6 was
/// scored, then <see cref="Image.MtfUnstretch"/> back to the input's units. The canvas ring leaves as it came.</para>
///
/// <para><b>Mono.</b> Every training session was one-shot colour, so a mono frame is refused unless the caller asks for it
/// to be put in all three channels (<c>monoAsRgb</c>), the mean of the three outputs coming back: a distribution no
/// model here was measured on, said so in the log.</para>
/// </remarks>
public sealed class OnnxTianWenStarRemover : IStarRemover, IDisposable
{
    private readonly ImmutableArray<StarRemoverRunner.Model> _models;
    private readonly int _views;
    private readonly bool _monoAsRgb;
    private readonly ILogger? _logger;

    private OnnxTianWenStarRemover(ImmutableArray<StarRemoverRunner.Model> models, int views, bool monoAsRgb, ILogger? logger)
    {
        _models = models;
        _views = views;
        _monoAsRgb = monoAsRgb;
        _logger = logger;
    }

    public string Name => $"Star remover (TianWen R2, {_models.Length} model{(_models.Length == 1 ? "" : "s")} x {_views} view{(_views == 1 ? "" : "s")})";

    /// <summary>
    /// Opens every model against the contract beside it and refuses the set when one does not match, so a run never
    /// starts on a model it cannot finish with. <paramref name="views"/> is how many of a tile's eight flips and quarter
    /// turns each model sees: 1, 2 (a mirror), 4 (the turns) or 8.
    /// </summary>
    public static OnnxTianWenStarRemover Open(IReadOnlyList<string> modelPaths, int views = 8, bool monoAsRgb = false, ILogger? logger = null)
    {
        if (modelPaths.Count == 0)
        {
            throw new ArgumentException("at least one model is needed", nameof(modelPaths));
        }
        if (views is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(views), views, "views must be 1, 2, 4 or 8");
        }
        var models = ImmutableArray.CreateBuilder<StarRemoverRunner.Model>(modelPaths.Count);
        try
        {
            foreach (var path in modelPaths)
            {
                var file = Path.GetFileName(path);
                var contract = ModelContract.LoadBeside(path);
                using var options = ExecutionProviderResolver.CreateSessionOptions(deviceId: 0, logger: logger);
                var session = new InferenceSession(path, options);
                if (!OnnxIoNames.IsImagePlusPlane(session))
                {
                    session.Dispose();
                    throw new NotSupportedException($"{file}: a star remover here takes an image and its noise plane; this graph does not.");
                }
                var problems = contract.Check(path, N2nDenoiser.Feed(planeConditioned: true), ModelGraph.From(session));
                if (problems.Length > 0)
                {
                    session.Dispose();
                    throw ModelContractException.Refused(file, ModelContract.PathBeside(path), problems);
                }
                var (imageInput, planeInput, output) = OnnxIoNames.ImagePlusPlane(session);
                var (height, width) = OnnxIoNames.ImageInputTileSize(session, imageInput);
                if (height is not { } tile || width != tile)
                {
                    session.Dispose();
                    throw new NotSupportedException($"{file}: the graph must declare a fixed square tile, got {height?.ToString() ?? "dynamic"} x {width?.ToString() ?? "dynamic"}.");
                }
                models.Add(new StarRemoverRunner.Model(file, session, imageInput, planeInput, output, tile));
                logger?.LogInformation("OnnxTianWenStarRemover: {Model} matches its contract (weights {Sha}), tile {Tile}", file, contract.OnnxSha256, tile);
            }
            if (models.Select(static m => m.Tile).Distinct().Count() != 1)
            {
                throw new NotSupportedException("the models must share one tile size: their windows are averaged pixel for pixel.");
            }
        }
        catch
        {
            foreach (var m in models)
            {
                m.Session.Dispose();
            }
            throw;
        }
        return new OnnxTianWenStarRemover(models.MoveToImmutable(), views, monoAsRgb, logger);
    }

    public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        => Task.Run(() => Remove(input, cancellationToken), cancellationToken);

    private Image Remove(Image input, CancellationToken ct)
    {
        // The trainer's tiles were normalised by Image.UnitScaleDivisor; far outside [0, 1] is raw ADU, not a frame
        // these models can be expected to handle (the denoiser's own guard, and its 1.5 tolerance for an enhanced master).
        if (input.MaxValue > 1.5f)
        {
            throw new ArgumentException($"{Name} requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}.", nameof(input));
        }
        switch (input.ChannelCount)
        {
            case 3:
                return StarRemoverRunner.Run(input, _models, _views, _logger, ct);
            case 1 when _monoAsRgb:
                _logger?.LogWarning("{Name}: a mono frame put in all three channels; no model here was trained or measured on one", Name);
                var (_, w, h) = input.Shape;
                var grey = input.GetChannelSpan(0);
                var rgb = new float[3][,];
                for (var c = 0; c < 3; c++)
                {
                    rgb[c] = new float[h, w];
                    grey.CopyTo(MemoryMarshal.CreateSpan(ref rgb[c][0, 0], w * h));
                }
                var colour = new Image(rgb, BitDepth.Float32, input.MaxValue, input.MinValue, input.Pedestal, input.ImageMeta);
                var starless = StarRemoverRunner.Run(colour, _models, _views, _logger, ct);
                var mean = new float[h, w];
                var dst = MemoryMarshal.CreateSpan(ref mean[0, 0], w * h);
                ReadOnlySpan<float> r = starless.GetChannelSpan(0), g = starless.GetChannelSpan(1), b = starless.GetChannelSpan(2);
                for (var k = 0; k < dst.Length; k++)
                {
                    dst[k] = (r[k] + g[k] + b[k]) / 3f;
                }
                return new Image([mean], BitDepth.Float32, input.MaxValue, input.MinValue, input.Pedestal, input.ImageMeta);
            case 1:
                throw new NotSupportedException($"{Name} is a one-shot-colour model; a mono frame goes through only when asked to be put in all three channels.");
            default:
                throw new NotSupportedException($"{Name} takes 3 channels (or 1, asked for), got {input.ChannelCount}.");
        }
    }

    public void Dispose()
    {
        foreach (var m in _models)
        {
            m.Session.Dispose();
        }
    }
}

/// <summary>
/// The whole-frame pass of <see cref="OnnxTianWenStarRemover"/>, with its tiling, views and padding as small pure helpers
/// a test can pin.
/// </summary>
internal static class StarRemoverRunner
{
    internal sealed record Model(string Name, InferenceSession Session, string ImageInput, string PlaneInput, string Output, int Tile);

    /// <summary>One view of a tile: mirrored left to right first when <paramref name="Flip"/>, then turned a quarter
    /// <paramref name="Turns"/> times, as <c>r2_infer.py</c> makes them (<c>torch.rot90</c> over the last two axes).</summary>
    internal readonly record struct View(bool Flip, int Turns);

    /// <summary>The views a pass uses, in <c>r2_infer.py</c>'s order: 1 is the tile as it is, 2 adds its mirror, 4 the
    /// quarter turns, 8 all of them.</summary>
    internal static ImmutableArray<View> Views(int count)
    {
        View[] all = [new(false, 0), new(false, 1), new(false, 2), new(false, 3), new(true, 0), new(true, 1), new(true, 2), new(true, 3)];
        return count switch
        {
            1 => [all[0]],
            2 => [all[0], all[4]],
            4 => [all[0], all[1], all[2], all[3]],
            8 => [.. all],
            _ => throw new ArgumentOutOfRangeException(nameof(count), count, "views must be 1, 2, 4 or 8"),
        };
    }

    /// <summary>
    /// Where each pixel of a view comes from in the square tile of side <paramref name="n"/>: view[i] = tile[map[i]]. One
    /// quarter turn takes out[y, x] from in[x, n - 1 - y] (<c>torch.rot90</c>, k = 1); the mirror takes column n - 1 - x.
    /// Turning a model's output back is the same map as a scatter: tile[map[i]] = output[i].
    /// </summary>
    internal static int[] ViewMap(View view, int n)
    {
        var map = new int[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                int a = y, b = x;
                for (var t = 0; t < view.Turns; t++)
                {
                    (a, b) = (b, n - 1 - a);
                }
                if (view.Flip)
                {
                    b = n - 1 - b;
                }
                map[y * n + x] = a * n + b;
            }
        }
        return map;
    }

    /// <summary>
    /// The weight a window gives its pixel at offset <c>k</c> along one axis: zero on the <paramref name="rim"/>, then a
    /// triangle over the interior that peaks at its centre. Windows stepped by half the interior
    /// (<see cref="Step"/>) sum to exactly one wherever two overlap.
    /// </summary>
    internal static float[] Triangle(int tile, int rim)
    {
        var interior = tile - 2 * rim;
        var half = interior / 2f;
        var w = new float[tile];
        for (var k = rim; k < tile - rim; k++)
        {
            var i = k - rim;
            w[k] = MathF.Min(i + 0.5f, interior - i - 0.5f) / half;
        }
        return w;
    }

    /// <summary>The step between windows: half the interior a window is read over.</summary>
    internal static int Step(int tile, int rim) => (tile - 2 * rim) / 2;

    /// <summary>Window origins along an axis of <paramref name="length"/> (already mirrored out): every
    /// <paramref name="step"/>, and one flush with the far end when the steps leave it uncovered.</summary>
    internal static ImmutableArray<int> Origins(int length, int tile, int step)
    {
        var origins = ImmutableArray.CreateBuilder<int>();
        for (var o = 0; o + tile <= length; o += step)
        {
            origins.Add(o);
        }
        if (origins.Count == 0 || origins[^1] + tile < length)
        {
            origins.Add(Math.Max(0, length - tile));
        }
        return origins.ToImmutable();
    }

    /// <summary>A mirror index about the edge sample (reflect-101: ... 2 1 | 0 1 2 ... n-1 | n-2 ...), as StarNet2's tiling
    /// pads and as the demosaic mirrors its taps.</summary>
    internal static int Mirror(int i, int n)
    {
        if (n == 1)
        {
            return 0;
        }
        var period = 2 * (n - 1);
        i %= period;
        if (i < 0)
        {
            i += period;
        }
        return i < n ? i : period - i;
    }

    internal static float[] MirrorPad(ReadOnlySpan<float> plane, int width, int height, int pad)
    {
        var pw = width + 2 * pad;
        var ph = height + 2 * pad;
        var padded = new float[pw * ph];
        for (var y = 0; y < ph; y++)
        {
            var sy = Mirror(y - pad, height) * width;
            for (var x = 0; x < pw; x++)
            {
                padded[y * pw + x] = plane[sy + Mirror(x - pad, width)];
            }
        }
        return padded;
    }

    public static Image Run(Image input, ImmutableArray<Model> models, int viewCount, ILogger? logger, CancellationToken ct)
    {
        var (channels, width, height) = input.Shape;
        var tile = models[0].Tile;
        var rim = AiNafnetInputs.StitchBorderPx;
        var step = Step(tile, rim);
        var pad = rim + step;
        var views = Views(viewCount);
        var maps = views.Select(v => ViewMap(v, tile)).ToArray();
        var weight = Triangle(tile, rim);
        var sw = Stopwatch.StartNew();

        // Into the training domain, and the frame's noise once (the denoiser runner's steps 0 and its plane half).
        var absent = input.AbsentPixels();
        var (stretched, stretchApplied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(input, absent);
        var (stretches, calibrations) = N2nLinearRunner.FrameNoise(input, stretchApplied, origMin, balances, absent);

        var pw = width + 2 * pad;
        var ph = height + 2 * pad;
        var padded = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            padded[c] = MirrorPad(stretched.GetChannelSpan(c), width, height, pad);
        }
        var xs = Origins(pw, tile, step);
        var ys = Origins(ph, tile, step);

        var n = tile * tile;
        var v = views.Length;
        var acc = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            acc[c] = new float[pw * ph];
        }
        var weightSum = new float[pw * ph];
        var tileChw = new float[channels * n];
        var batchImage = new float[v * channels * n];
        var batchPlane = new float[v * n];
        var mean = new float[channels * n];
        var scale = 1f / (models.Length * v);

        foreach (var y0 in ys)
        {
            foreach (var x0 in xs)
            {
                ct.ThrowIfCancellationRequested();
                for (var c = 0; c < channels; c++)
                {
                    for (var y = 0; y < tile; y++)
                    {
                        padded[c].AsSpan((y0 + y) * pw + x0, tile).CopyTo(tileChw.AsSpan(c * n + y * tile, tile));
                    }
                }
                // The plane is made once from the tile as it is, and turned with it, as the eval's stored planes were.
                var plane = N2nLinearRunner.ChunkPlane(tileChw, channels, tile, tile, stretches, calibrations);
                for (var k = 0; k < v; k++)
                {
                    var map = maps[k];
                    for (var c = 0; c < channels; c++)
                    {
                        var src = tileChw.AsSpan(c * n, n);
                        var dst = batchImage.AsSpan((k * channels + c) * n, n);
                        for (var i = 0; i < n; i++)
                        {
                            dst[i] = src[map[i]];
                        }
                    }
                    var pdst = batchPlane.AsSpan(k * n, n);
                    for (var i = 0; i < n; i++)
                    {
                        pdst[i] = plane[map[i]];
                    }
                }

                Array.Clear(mean);
                foreach (var model in models)
                {
                    using var result = model.Session.Run(
                    [
                        NamedOnnxValue.CreateFromTensor(model.ImageInput, new DenseTensor<float>(batchImage.AsMemory(), [v, channels, tile, tile])),
                        NamedOnnxValue.CreateFromTensor(model.PlaneInput, new DenseTensor<float>(batchPlane.AsMemory(), [v, 1, tile, tile])),
                    ]);
                    var output = result.First(r => r.Name == model.Output).AsTensor<float>().ToDenseTensor().Buffer.Span;
                    for (var k = 0; k < v; k++)
                    {
                        var map = maps[k];
                        for (var c = 0; c < channels; c++)
                        {
                            var src = output.Slice((k * channels + c) * n, n);
                            var dst = mean.AsSpan(c * n, n);
                            for (var i = 0; i < n; i++)
                            {
                                dst[map[i]] += src[i];
                            }
                        }
                    }
                }

                for (var y = rim; y < tile - rim; y++)
                {
                    var row = (y0 + y) * pw + x0;
                    for (var x = rim; x < tile - rim; x++)
                    {
                        var wgt = weight[y] * weight[x];
                        weightSum[row + x] += wgt;
                        for (var c = 0; c < channels; c++)
                        {
                            acc[c][row + x] += mean[c * n + y * tile + x] * scale * wgt;
                        }
                    }
                }
            }
        }
        var inferMs = sw.ElapsedMilliseconds;

        var inferred = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[height, width];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], width * height);
            for (var y = 0; y < height; y++)
            {
                var row = (y + pad) * pw + pad;
                for (var x = 0; x < width; x++)
                {
                    dst[y * width + x] = acc[c][row + x] / weightSum[row + x];
                }
            }
            inferred[c] = plane;
        }
        var image = new Image(inferred, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta);
        var restored = stretchApplied && origMin is { } restoreMin && balances is { } restoreBalances
            ? image.MtfUnstretch(restoreMin, restoreBalances)
            : image;

        var outData = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[height, width];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], width * height);
            restored.GetChannelSpan(c).CopyTo(dst);
            if (absent is { } ring)
            {
                Image.CopyAbsent(input.GetChannelSpan(c), dst, width, ring);
            }
            outData[c] = plane;
        }
        logger?.LogInformation(
            "StarRemoverRunner: {W}x{H}x{C}, {Windows} windows of {Tile} px every {Step} px, {Models} model(s) x {Views} view(s), stretch {Stretched}, inference {InferMs} ms, total {TotalMs} ms",
            width, height, channels, xs.Length * ys.Length, tile, step, models.Length, v, stretchApplied, inferMs, sw.ElapsedMilliseconds);
        return new Image(outData, BitDepth.Float32, input.MaxValue, input.MinValue, 0f, input.ImageMeta);
    }
}
