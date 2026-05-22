using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// AI background-gradient correction via the GraXpert BGE ONNX model
/// (Steffenhir/GraXpert, MIT-licensed). Downsamples the source to 240x240,
/// edge-pads to 256x256, normalises per-channel via median + MAD, runs a
/// single-pass NHWC inference, denormalises, smooths, and upsamples the
/// estimated background back to source dimensions. The corrected image is
/// <c>source - background + mean(background)</c> -- gradient SHAPE removed,
/// absolute sky level preserved.
/// </summary>
/// <remarks>
/// <para>Domain semantics: linear-units in / linear-units out. The
/// transformation is well-approximated as a linear-domain function
/// (subtracting a smooth offset surface). Slots at the head of the AI
/// sharpen pipeline per Frank Sackenheim's canonical order
/// (gradient -> stars -> detail -> stretch).</para>
///
/// <para>Model: <c>graxpert_bge.onnx</c> resolved via
/// <see cref="IModelResolver"/>. Input <c>gen_input_image</c> is NHWC
/// <c>(1, 256, 256, 3)</c>; mono inputs are channel-duplicated into RGB.
/// The model file ships at <c>%LOCALAPPDATA%/GraXpert/GraXpert/bge-ai-models/
/// &lt;version&gt;/model.onnx</c>; <c>tools/tianwen-ai-models-fetch.ps1</c>
/// hardlinks the highest-versioned copy into TianWen's models tree.</para>
///
/// <para>Single-pass design (not chunked NAFNet-style): the model is
/// trained to predict the LOW-FREQUENCY background gradient, so it
/// deliberately operates on a downsampled plate and the resize-back
/// up-step provides the spatial extent. Chunking would estimate
/// per-chunk backgrounds, defeating the global-gradient intent.</para>
/// </remarks>
public sealed class OnnxBackgroundExtractor(
    IModelResolver modelResolver,
    ILogger<OnnxBackgroundExtractor>? logger = null)
    : IGradientCorrector, IDisposable
{
    private const string ModelName = "graxpert_bge.onnx";
    private const int ModelInputSize = 256;
    private const int Padding = 8;
    private const int ShrinkSize = ModelInputSize - 2 * Padding;  // 240
    private const float NormScale = 0.04f;

    private readonly object _gate = new();
    private InferenceSession? _session;
    private bool _disposed;

    public string Name => "GradientCorrector (GraXpert BGE ONNX)";

    public async Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
    {
        var (corrected, background) = await EnhanceAndEstimateBackgroundAsync(input, cancellationToken);
        // No background-output flag was specified -- discard the surface so
        // we don't leak a 120 MB plate per call. Internally we still allocate
        // it (the inference and subtraction need it), then release here.
        background?.Release();
        return corrected;
    }

    public async Task<(Image Corrected, Image? Background)> EnhanceAndEstimateBackgroundAsync(
        Image input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.ChannelCount is not (1 or 3))
        {
            throw new NotSupportedException(
                $"OnnxBackgroundExtractor requires 1 or 3 channels (mono / RGB), got {input.ChannelCount}.");
        }
        if (input.MaxValue > 1.5f)
        {
            throw new ArgumentException(
                $"OnnxBackgroundExtractor requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }
        return await Task.Run<(Image, Image?)>(() => RunPipeline(input, cancellationToken), cancellationToken);
    }

    private (Image Corrected, Image Background) RunPipeline(Image input, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var (channels, srcW, srcH) = input.Shape;
        logger?.LogDebug("OnnxBackgroundExtractor: input {W}x{H}x{C}", srcW, srcH, channels);

        // 1) Bilinear downscale source to 240x240.
        var prepSw = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        var shrunk = input.BilinearResize(ShrinkSize, ShrinkSize);

        // 2) Edge-pad to 256x256 (so the model never sees a hard zero border).
        ct.ThrowIfCancellationRequested();
        var padded = EdgePad(shrunk, Padding);
        shrunk.Release();

        // 3) Per-channel median + MAD on the (padded) 256x256 plate. Doing
        // this AFTER the pad means corner regions get the edge values' weight
        // -- consistent with GraXpert's reference impl.
        var (medians, mads) = ChannelMedianMad(padded);
        for (var c = 0; c < channels; c++)
        {
            if (mads[c] < 1e-9f) mads[c] = 1e-9f;  // narrow-distribution guard
        }

        // 4) Normalise: (x - median) / mad * 0.04, clip to [-1, 1].
        ct.ThrowIfCancellationRequested();
        var normalised = NormaliseForModel(padded, medians, mads);
        padded.Release();

        // 5) Mono -> RGB by triplication if needed (model is RGB-only).
        var modelInput = channels == 1 ? MonoToRgb(normalised) : normalised;
        if (!ReferenceEquals(modelInput, normalised)) normalised.Release();
        var prepMs = prepSw.ElapsedMilliseconds;

        // 6) Run inference (single shot, NHWC).
        var inferSw = Stopwatch.StartNew();
        var session = AcquireSession();
        var (inputName, outputName) = OnnxIoNames.SingleInput(session);
        var inputTensor = TensorImageConverter.ToNhwcTensor(modelInput);
        modelInput.Release();
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
        using var results = session.Run(inputs);
        DenseTensor<float>? outputTensor = null;
        foreach (var v in results)
        {
            if (v.Name == outputName) { outputTensor = v.AsTensor<float>() as DenseTensor<float>; break; }
        }
        if (outputTensor is null)
        {
            throw new InvalidOperationException(
                $"OnnxBackgroundExtractor: model did not produce expected output '{outputName}'.");
        }
        // ORT may return a sliced view of a backing buffer; copy to an owned
        // tensor so the lifetime is decoupled from the using-disposed `results`.
        var ownedOutput = new DenseTensor<float>(outputTensor.Dimensions);
        outputTensor.Buffer.Span.CopyTo(ownedOutput.Buffer.Span);
        var raw = TensorImageConverter.FromNhwcTensor(ownedOutput, input);
        var inferMs = inferSw.ElapsedMilliseconds;

        // 7) Denormalise back to source scale. If mono, average channels back
        // to a single plate (GraXpert picks ch0; an unweighted average is
        // closer to lossless since the model gets a duplicated input).
        var stitchSw = Stopwatch.StartNew();
        var denormed = DenormaliseFromModel(raw, medians, mads);
        raw.Release();
        var backgroundPadded = channels == 1 ? RgbToMonoAverage(denormed) : denormed;
        if (!ReferenceEquals(backgroundPadded, denormed)) denormed.Release();

        // 8) Crop off the 8px padding -> 240x240.
        var backgroundCropped = Crop(backgroundPadded, Padding, Padding, ShrinkSize, ShrinkSize);
        backgroundPadded.Release();

        // 9) Light gaussian smooth (sigma=3, ksize=11 per GraXpert reference).
        var smoothed = GaussianBlurSeparable(backgroundCropped, sigma: 3f, kernelSize: 11);
        backgroundCropped.Release();

        // 10) Bilinear upsample back to source dimensions.
        var background = smoothed.BilinearResize(srcW, srcH);
        smoothed.Release();

        // 11) Subtract background, add back mean(background) to preserve the
        // overall sky level (gradient SHAPE removed, absolute level kept).
        // The background plate is returned alongside the corrected output so
        // callers that asked for diagnostic visibility (e.g. CLI flatten
        // --save-gradient) can render it; callers that didn't (the bare
        // EnhanceAsync entry point) release it via the wrapper.
        var meanBg = MeanScalar(background);
        var corrected = input.Subtract(background, addedPedestal: meanBg);
        var stitchMs = stitchSw.ElapsedMilliseconds;

        logger?.LogInformation(
            "OnnxBackgroundExtractor.EnhanceAsync: {Model} {W}x{H}x{C} prep={Prep}ms infer={Infer}ms stitch={Stitch}ms total={Total}ms mean_bg={MeanBg:F5}",
            ModelName, srcW, srcH, channels, prepMs, inferMs, stitchMs, totalSw.ElapsedMilliseconds, meanBg);

        return (corrected, background);
    }

    private InferenceSession AcquireSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null)
            {
                var modelPath = modelResolver.Resolve(ModelName);
                logger?.LogInformation("OnnxBackgroundExtractor: loading {Model} from {Path}", ModelName, modelPath);
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

    // ---- private helpers below: BGE-specific, kept local to this wrapper ----

    /// <summary>
    /// Edge-replication pad: outputs an Image with (srcW + 2*pad) x (srcH + 2*pad)
    /// where the inner srcW x srcH region copies the source and the border
    /// region replicates the nearest edge pixel.
    /// </summary>
    private static Image EdgePad(Image src, int pad)
    {
        var (channels, srcW, srcH) = src.Shape;
        var newW = srcW + 2 * pad;
        var newH = srcH + 2 * pad;
        var newData = new float[channels][,];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var c = 0; c < channels; c++)
        {
            var srcSpan = src.GetChannelSpan(c);
            var plane = new float[newH, newW];
            for (var y = 0; y < newH; y++)
            {
                var sy = y - pad;
                if (sy < 0) sy = 0;
                else if (sy > srcH - 1) sy = srcH - 1;
                for (var x = 0; x < newW; x++)
                {
                    var sx = x - pad;
                    if (sx < 0) sx = 0;
                    else if (sx > srcW - 1) sx = srcW - 1;
                    var v = srcSpan[sy * srcW + sx];
                    plane[y, x] = v;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            newData[c] = plane;
        }
        return new Image(newData, src.BitDepth, max, min, src.Pedestal, src.ImageMeta);
    }

    /// <summary>
    /// Center crop. Used to slice the 8px BGE padding off the model output.
    /// </summary>
    private static Image Crop(Image src, int x0, int y0, int newWidth, int newHeight)
    {
        var (channels, srcW, _) = src.Shape;
        var newData = new float[channels][,];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var c = 0; c < channels; c++)
        {
            var srcSpan = src.GetChannelSpan(c);
            var plane = new float[newHeight, newWidth];
            for (var y = 0; y < newHeight; y++)
            {
                for (var x = 0; x < newWidth; x++)
                {
                    var v = srcSpan[(y + y0) * srcW + (x + x0)];
                    plane[y, x] = v;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            newData[c] = plane;
        }
        return new Image(newData, src.BitDepth, max, min, src.Pedestal, src.ImageMeta);
    }

    /// <summary>
    /// Direct-sort per-channel median and MAD. Used instead of the
    /// histogram-based <c>Image.GetPedestralMedianAndMADScaledToUnit</c>
    /// because (a) input is only 65k pixels per channel here -- sort is
    /// cheaper than histogram setup, and (b) we need raw values in source
    /// units, not the histogram-bin-quantised version.
    /// </summary>
    private static (float[] medians, float[] mads) ChannelMedianMad(Image src)
    {
        var (channels, w, h) = src.Shape;
        var n = w * h;
        var medians = new float[channels];
        var mads = new float[channels];
        var sortBuf = new float[n];
        for (var c = 0; c < channels; c++)
        {
            var ch = src.GetChannelSpan(c);
            ch.CopyTo(sortBuf);
            Array.Sort(sortBuf, 0, n);
            var med = sortBuf[n / 2];
            medians[c] = med;
            for (var i = 0; i < n; i++) sortBuf[i] = Math.Abs(ch[i] - med);
            Array.Sort(sortBuf, 0, n);
            mads[c] = sortBuf[n / 2];
        }
        return (medians, mads);
    }

    /// <summary>
    /// Pre-inference normalisation: out = (in - median) / mad * 0.04, clipped
    /// to [-1, 1]. The clipping kills bright stars and saturated cores; the
    /// model only ever sees background-scale variation, which is the whole
    /// point -- a star peak would otherwise dominate the convolutional input.
    /// </summary>
    private static Image NormaliseForModel(Image src, float[] medians, float[] mads)
    {
        var (channels, w, h) = src.Shape;
        var newData = new float[channels][,];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var c = 0; c < channels; c++)
        {
            var ch = src.GetChannelSpan(c);
            var plane = new float[h, w];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], w * h);
            var med = medians[c];
            var scale = NormScale / mads[c];
            for (var i = 0; i < ch.Length; i++)
            {
                var v = (ch[i] - med) * scale;
                if (v < -1f) v = -1f;
                else if (v > 1f) v = 1f;
                dst[i] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            newData[c] = plane;
        }
        return new Image(newData, src.BitDepth, max, min, src.Pedestal, src.ImageMeta);
    }

    /// <summary>
    /// Post-inference denormalisation, the algebraic inverse of
    /// <see cref="NormaliseForModel"/>. The output is in the source data's
    /// original scale and may exceed [0, 1] when the model extrapolates
    /// (rare for BGE since the input was clipped pre-inference).
    /// </summary>
    private static Image DenormaliseFromModel(Image src, float[] medians, float[] mads)
    {
        var (channels, w, h) = src.Shape;
        var newData = new float[channels][,];
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var c = 0; c < channels; c++)
        {
            var ch = src.GetChannelSpan(c);
            var plane = new float[h, w];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], w * h);
            var med = medians[c];
            var scale = mads[c] / NormScale;
            for (var i = 0; i < ch.Length; i++)
            {
                var v = ch[i] * scale + med;
                dst[i] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            newData[c] = plane;
        }
        return new Image(newData, src.BitDepth, max, min, src.Pedestal, src.ImageMeta);
    }

    /// <summary>
    /// Duplicate a single-channel image into a 3-channel RGB plate. Used
    /// because the GraXpert BGE model is RGB-only -- mono input gets the
    /// same plane in all three channels.
    /// </summary>
    private static Image MonoToRgb(Image src)
    {
        var (channels, w, h) = src.Shape;
        if (channels != 1) throw new InvalidOperationException("MonoToRgb expects single-channel input");
        var ch0 = src.GetChannelSpan(0);
        var newData = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[h, w];
            ch0.CopyTo(MemoryMarshal.CreateSpan(ref plane[0, 0], w * h));
            newData[c] = plane;
        }
        return new Image(newData, src.BitDepth, src.MaxValue, src.MinValue, src.Pedestal, src.ImageMeta);
    }

    /// <summary>
    /// Average a 3-channel RGB plate back into a single mono channel.
    /// Used on the model output when the source was mono -- GraXpert picks
    /// channel 0; averaging is slightly more lossless since the model is
    /// fed an identical-channel input, so all three output channels carry
    /// the same target signal plus uncorrelated inference noise.
    /// </summary>
    private static Image RgbToMonoAverage(Image src)
    {
        var (channels, w, h) = src.Shape;
        if (channels != 3) throw new InvalidOperationException("RgbToMonoAverage expects 3-channel input");
        var r = src.GetChannelSpan(0);
        var g = src.GetChannelSpan(1);
        var b = src.GetChannelSpan(2);
        var plane = new float[h, w];
        var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], w * h);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var i = 0; i < r.Length; i++)
        {
            var v = (r[i] + g[i] + b[i]) / 3f;
            dst[i] = v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return new Image([plane], src.BitDepth, max, min, src.Pedestal, src.ImageMeta);
    }

    /// <summary>
    /// Separable Gaussian blur with reflect-mode boundaries. Matches
    /// GraXpert's <c>cv2.GaussianBlur</c> with explicit ksize/sigma. The
    /// BGE pipeline runs a sigma=3 smooth on the model output before
    /// resize-back-to-full -- it's a small detail-suppression that makes
    /// the upsampled gradient look smooth rather than blocky.
    /// </summary>
    private static Image GaussianBlurSeparable(Image src, float sigma, int kernelSize)
    {
        var (channels, w, h) = src.Shape;
        // Build 1D kernel.
        var center = kernelSize / 2;
        var kernel = new float[kernelSize];
        var sum = 0f;
        var twoSigma2 = 2f * sigma * sigma;
        for (var i = 0; i < kernelSize; i++)
        {
            var d = i - center;
            kernel[i] = MathF.Exp(-(d * d) / twoSigma2);
            sum += kernel[i];
        }
        for (var i = 0; i < kernelSize; i++) kernel[i] /= sum;

        var newData = new float[channels][,];
        var temp = new float[w * h];
        float globalMin = float.PositiveInfinity, globalMax = float.NegativeInfinity;
        for (var c = 0; c < channels; c++)
        {
            var srcSpan = src.GetChannelSpan(c);
            // Horizontal pass into temp.
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var acc = 0f;
                    for (var k = 0; k < kernelSize; k++)
                    {
                        var sx = x + k - center;
                        // Reflect.
                        if (sx < 0) sx = -sx;
                        else if (sx >= w) sx = 2 * w - 2 - sx;
                        if (sx < 0) sx = 0;
                        if (sx > w - 1) sx = w - 1;
                        acc += srcSpan[y * w + sx] * kernel[k];
                    }
                    temp[y * w + x] = acc;
                }
            }
            // Vertical pass into output plane.
            var plane = new float[h, w];
            var dst = MemoryMarshal.CreateSpan(ref plane[0, 0], w * h);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var acc = 0f;
                    for (var k = 0; k < kernelSize; k++)
                    {
                        var sy = y + k - center;
                        if (sy < 0) sy = -sy;
                        else if (sy >= h) sy = 2 * h - 2 - sy;
                        if (sy < 0) sy = 0;
                        if (sy > h - 1) sy = h - 1;
                        acc += temp[sy * w + x] * kernel[k];
                    }
                    dst[y * w + x] = acc;
                    if (acc < globalMin) globalMin = acc;
                    if (acc > globalMax) globalMax = acc;
                }
            }
            newData[c] = plane;
        }
        return new Image(newData, src.BitDepth, globalMax, globalMin, src.Pedestal, src.ImageMeta);
    }

    /// <summary>Mean of all non-NaN pixels across all channels. Used as the
    /// brightness offset added back after gradient subtraction.</summary>
    private static float MeanScalar(Image src)
    {
        var (channels, _, _) = src.Shape;
        var total = 0.0;
        var count = 0L;
        for (var c = 0; c < channels; c++)
        {
            var ch = src.GetChannelSpan(c);
            for (var i = 0; i < ch.Length; i++)
            {
                var v = ch[i];
                if (!float.IsNaN(v))
                {
                    total += v;
                    count++;
                }
            }
        }
        return count > 0 ? (float)(total / count) : 0f;
    }
}
