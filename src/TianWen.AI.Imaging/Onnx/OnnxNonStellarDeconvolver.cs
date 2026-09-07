using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TianWen.AI.Inference;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// AI4 NAFNet PSF-conditional deconvolver for the starless plate. Two ONNX
/// inputs: the image tensor and a scalar <c>psf01</c> in <c>[0, 1]</c> that
/// the network broadcasts internally and concatenates as a 4th input
/// channel. Delegates the chunked-inference pipeline to
/// <see cref="ChunkedNafnetRunner"/>; this class owns PSF estimation,
/// session management, and the per-call log line.
/// </summary>
/// <remarks>
/// <para><b><paramref name="perChunkPsf"/> conditions each tile on its own region's PSF</b>
/// (deconvolver-training.md, D1). Training labels psf01 per 256 px cell, and the optics vary across a
/// field (Rim: 4.03 px at the centre, 3.12 at a corner), so one number per frame tells every tile the
/// wrong width but one. The per-tile estimate comes from <see cref="IPsfEstimator.EstimateChunkAsync(Image, int, int, int, int, float, System.Threading.CancellationToken)"/>
/// on the LINEAR input over the tile's source region, falling back to the whole-image value where a
/// tile has too few stars. OFF by default until it is measured against the whole-image value on Rim:
/// the shipped SAS AI4 graph was trained on whole-image labels, and a change to what it is told is a
/// change to what it does.</para>
/// </remarks>
public sealed class OnnxNonStellarDeconvolver(
    IModelResolver modelResolver,
    IPsfEstimator psfEstimator,
    ILogger<OnnxNonStellarDeconvolver>? logger = null,
    int chunkSize = 256,
    int overlap = 64,
    bool perChunkPsf = false)
    : INonStellarDeconvolver, IDisposable
{
    internal const string Model = "deep_nonstellar_sharp_conditional_psf_AI4.onnx";

    private readonly object _gate = new();
    private InferenceSession? _session;
    private bool _disposed;

    public string Name => "NonStellarDeconvolver (AI4 NAFNet, PSF-conditional)";

    public async Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.ChannelCount is not (1 or 3))
        {
            throw new NotSupportedException(
                $"OnnxNonStellarDeconvolver requires 1 or 3 channels, got {input.ChannelCount}.");
        }
        // Allow up to MaxValue=1.5 to tolerate small AI4 NAFNet overshoot when
        // chained as a pipeline stage (see Image.MtfUnstretch xmldoc -- network
        // excursions above [0, 1] are preserved as empirical max). Still
        // rejects miscalibrated inputs like raw [0, 65535] camera data.
        if (input.MaxValue > 1.5f)
        {
            throw new ArgumentException(
                $"OnnxNonStellarDeconvolver requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        // PSF measurement runs against the LINEAR input (FindStarsAsync
        // expects unstretched data); everything else happens on the thread
        // pool inside Task.Run.
        var psf01 = await psfEstimator.EstimateAsync(input, cancellationToken);

        // Per tile, on the same linear input, over each tile's SOURCE region: the runner splits the
        // bordered plane, so the layout is taken over the bordered size and mapped back by the border.
        float[]? chunkPsf01 = null;
        if (perChunkPsf)
        {
            chunkPsf01 = await EstimatePerChunkAsync(input, psf01, cancellationToken);
        }

        return await Task.Run(() => RunPipeline(input, psf01, chunkPsf01, cancellationToken), cancellationToken);
    }

    /// <summary>The tile grid the runner will split on, and the psf01 each tile's source region measures.</summary>
    internal async Task<float[]> EstimatePerChunkAsync(Image input, float wholeImagePsf01, CancellationToken ct)
    {
        var (_, srcW, srcH) = input.Shape;
        var border = AiNafnetInputs.StitchBorderPx;
        var layout = ChunkedInference.Layout(srcW + (2 * border), srcH + (2 * border), chunkSize, overlap);
        var values = new float[layout.Length];
        var fellBack = 0;
        for (var i = 0; i < layout.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rect = layout[i];
            // Bordered coordinates to source coordinates, clipped to the frame; a tile that is all
            // border (never on a real image, but a 16 px frame could do it) takes the whole-image value.
            var x0 = Math.Clamp(rect.X - border, 0, srcW);
            var y0 = Math.Clamp(rect.Y - border, 0, srcH);
            var x1 = Math.Clamp(rect.X + rect.Width - border, 0, srcW);
            var y1 = Math.Clamp(rect.Y + rect.Height - border, 0, srcH);
            if (x1 <= x0 || y1 <= y0)
            {
                values[i] = wholeImagePsf01;
                fellBack++;
                continue;
            }

            values[i] = await psfEstimator.EstimateChunkAsync(input, x0, y0, x1 - x0, y1 - y0, wholeImagePsf01, ct);
            if (values[i] == wholeImagePsf01)
            {
                fellBack++;
            }
        }

        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        logger?.LogInformation(
            "OnnxNonStellarDeconvolver: per-chunk psf01 over {Chunks} tiles: min {Min:F3} p50 {P50:F3} max {Max:F3} (whole image {Whole:F3}; {FellBack} tiles at the whole-image value)",
            values.Length, sorted[0], sorted[sorted.Length / 2], sorted[^1], wholeImagePsf01, fellBack);
        return values;
    }

    private Image RunPipeline(Image input, float psf01, float[]? chunkPsf01, CancellationToken ct)
    {
        var (sourceChannels, srcW, srcH) = input.Shape;
        logger?.LogDebug("OnnxNonStellarDeconvolver: input {W}x{H}x{C} psf01={Psf01:F3} chunkSize={Chunk} overlap={Overlap} perChunk={PerChunk}",
            srcW, srcH, sourceChannels, psf01, chunkSize, overlap, chunkPsf01 is not null);

        var session = AcquireSession();
        var (imageInputName, scalarInputName, outputName) = OnnxIoNames.ImagePlusScalar(session);

        // PSF scalar tensor shared across all chunks (whole-image psf01), or one per chunk.
        var psfTensor = new DenseTensor<float>([1, 1]);
        psfTensor.Buffer.Span[0] = psf01;
        var extras = new[] { NamedOnnxValue.CreateFromTensor(scalarInputName, psfTensor) };
        Func<int, IReadOnlyList<NamedOnnxValue>?>? perChunk = null;
        if (chunkPsf01 is not null)
        {
            perChunk = i =>
            {
                var t = new DenseTensor<float>([1, 1]);
                t.Buffer.Span[0] = i < chunkPsf01.Length ? chunkPsf01[i] : psf01;
                return [NamedOnnxValue.CreateFromTensor(scalarInputName, t)];
            };
        }

        var result = ChunkedNafnetRunner.Run(
            input, session, imageInputName, outputName,
            chunkSize: chunkSize, overlap: overlap,
            extraInputs: extras,
            ct: ct,
            extraInputsForChunk: perChunk);

        var megapixels = (sourceChannels * srcW * (double)srcH) / 1_000_000.0;
        var throughputMpps = result.TotalMs > 0 ? megapixels * 1000.0 / result.TotalMs : 0.0;
        logger?.LogInformation(
            "OnnxNonStellarDeconvolver.EnhanceAsync: {Model} {W}x{H}x{C} modelCh={ModelChannels} chunks={Chunks} stretchApplied={StretchApplied} psf01={Psf01:F3} " +
            "stretch={Stretch}ms prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms",
            Model, srcW, srcH, sourceChannels, result.ModelChannels, result.ChunkCount, result.StretchApplied, psf01,
            result.StretchMs, result.PrepMs, result.InferMs, result.StitchMs, result.UnstretchMs,
            throughputMpps, result.TotalMs);

        return result.Output;
    }

    private InferenceSession AcquireSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null)
            {
                var modelPath = modelResolver.Resolve(Model);
                logger?.LogInformation("OnnxNonStellarDeconvolver: loading {Model} from {Path}", Model, modelPath);
                using var options = ExecutionProviderResolver.CreateSessionOptions(deviceId: 0, logger: logger);
                _session = new InferenceSession(modelPath, options);
            }
            return _session;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _disposed = true;
        }
    }
}
