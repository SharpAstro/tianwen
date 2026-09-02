using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using TianWen.AI.Inference;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// The in-house Noise2Noise denoiser for one-shot-colour stacked masters: a 0.81 M-parameter
/// noise-conditioned UNet trained on pairs of subs from the same cell, so it never saw a clean
/// target and cannot have learned to reproduce one.
/// </summary>
/// <remarks>
/// <para><b>Provenance, stated because it changes how to treat this model.</b> The shipped weights
/// are <c>n2n_v19d</c> seed 2, trained on 8 sessions x 45 cells. Six experiments went looking for
/// why a small training set beat a large one and the answer turned out to be that it does not
/// reliably: three disjoint 8-session draws scored 0.825 / 0.726 / 0.739 on the same held-out
/// session, and one arm's three seeds spanned a wider range than the effect being ranked. So this
/// is <b>the best checkpoint measured, not the output of a repeatable method</b>. If it is ever
/// retrained, re-measure on the held-out sessions rather than assuming a like-for-like recipe
/// reproduces it.</para>
///
/// <para><b>Domain semantics: linear in, linear out, the exporter's stretch in between.</b> The
/// contract at this boundary is a linear <c>[0, 1]</c> frame, the same one the AI4 enhancers take,
/// and like them the net itself works in the MTF-stretched domain: every training tile was stored
/// after <see cref="ChunkedNafnetRunner.ApplyInputStretch"/>, so <see cref="N2nLinearRunner"/>
/// applies that call to the whole frame, runs, and inverts it before blending. Until 2026-09-02 the
/// runner fed the frame verbatim on the belief that the tiles were linear, which put a real master
/// about 100x below its training band; the measurement that settled it is in the runner's remarks.
/// The output is linear, so it chains with other linear-domain processing.</para>
///
/// <para><b>One-shot-colour only.</b> Every training session was OSC, so a mono input is rejected
/// rather than tiled across the three input slots: that would feed it a distribution nobody has
/// measured it on, and unlike the AI4 pairs there is no mono weight bundle to fall back to.</para>
///
/// <para><b>Not the default <see cref="IDenoiseEnhancer"/>.</b> Registering it is opt-in
/// (<c>AddTianWenN2nDenoiser</c>) because it has never been compared against
/// <see cref="OnnxDenoiser"/> on the enhance pipeline's own job -- only against its own ablations
/// on held-out astro masters. Silently replacing the SAS tier's denoiser on the strength of a
/// different measurement would be asserting something nobody checked.</para>
///
/// <para>Session lifecycle: one lazily-created <see cref="InferenceSession"/>, cached for the
/// lifetime of the instance and released on <see cref="Dispose"/>.</para>
/// </remarks>
public sealed class N2nDenoiser(
    IModelResolver modelResolver,
    ILogger<N2nDenoiser>? logger = null,
    float defaultStrength = 1.0f,
    int overlap = 64)
    : IDenoiseEnhancer, IDisposable
{
    /// <summary>
    /// The shipped weights. The <c>v19d</c> segment is deliberate: the checkpoint identity is part
    /// of what this model is (see the provenance note on the class), so a future retrain gets a new
    /// file name rather than silently replacing this one under the same one.
    /// </summary>
    public const string ModelFileName = "tianwen_denoise_osc_v19d.onnx";

    private readonly System.Threading.Lock _gate = new();
    private InferenceSession? _session;
    private bool _disposed;

    public string Name => "Denoiser (TianWen N2N, OSC)";

    public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, defaultStrength, cancellationToken);

    /// <summary>
    /// The variant axis is an AI4 concept (Default / Lite / Walking weight bundles) and there is
    /// exactly one bundle here, so anything but <see cref="DenoiseVariant.Default"/> is refused
    /// instead of being quietly ignored -- a caller asking for Lite should learn it is not on offer.
    /// </summary>
    public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, CancellationToken cancellationToken = default)
        => variant is DenoiseVariant.Default
            ? EnhanceAsync(input, defaultStrength, cancellationToken)
            : throw RefuseVariant(variant);

    /// <summary>
    /// The pipeline path (<c>SharpenPipeline</c>'s denoise step calls this overload). The one
    /// tuning knob this model reads is <see cref="EnhanceTuning.DenoiseStrength"/>, which maps
    /// onto the blend dial -- the same "how much denoising" the user meant when they set it for
    /// RC's <c>nxt --dn</c>. <paramref name="progress"/> is dropped: chunked ORT inference has no
    /// sub-step stream, so the pipeline's own step-boundary ticks are the progress, exactly as
    /// for the SAS enhancers.
    /// </summary>
    public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => variant is DenoiseVariant.Default
            ? EnhanceAsync(input, options.Tuning?.DenoiseStrength ?? defaultStrength, cancellationToken)
            : throw RefuseVariant(variant);

    private static ArgumentOutOfRangeException RefuseVariant(DenoiseVariant variant) => new(
        nameof(variant), variant,
        $"{nameof(N2nDenoiser)} ships a single weight bundle; only {nameof(DenoiseVariant.Default)} is available.");

    /// <summary>
    /// Denoise and blend the result back toward the input.
    /// </summary>
    /// <param name="strength">In <c>(0, 1]</c>. The output is
    /// <c>input + strength * (denoised - input)</c>: 1.0 is the model's full opinion, and lower
    /// values walk back toward the untouched input along a straight line.
    /// <para>This is deliberately the blend and not the model's own conditioning dial, which was
    /// measured and rejected: see the remarks on <see cref="N2nLinearRunner"/> for the three
    /// reasons, of which the disqualifying one is that fabricated point sources RISE as that dial
    /// is turned down.</para>
    /// </param>
    public async Task<Image> EnhanceAsync(Image input, float strength, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (!float.IsFinite(strength) || strength <= 0f || strength > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strength), strength, "strength must lie in (0, 1]; it is a blend fraction toward the denoised result.");
        }
        if (input.ChannelCount != 3)
        {
            throw new NotSupportedException(
                $"{nameof(N2nDenoiser)} is a one-shot-colour model and requires 3 channels, got {input.ChannelCount}. " +
                "It has no mono weight bundle; use OnnxDenoiser for mono.");
        }
        // The trainer fed tiles normalised by Image.UnitScaleDivisor, so anything far outside
        // [0, 1] is a miscalibrated input (raw camera ADU being the usual case) rather than a
        // frame this model can be expected to handle. The same 1.5 tolerance as OnnxDenoiser,
        // for the same reason: enhanced masters can overshoot slightly above 1.
        if (input.MaxValue > 1.5f)
        {
            throw new ArgumentException(
                $"{nameof(N2nDenoiser)} requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        return await Task.Run(() => RunPipeline(input, strength, cancellationToken), cancellationToken);
    }

    private Image RunPipeline(Image input, float strength, CancellationToken ct)
    {
        var (channels, srcW, srcH) = input.Shape;
        var session = AcquireSession();
        var (imageInput, strengthInput, output) = OnnxIoNames.ImagePlusScalar(session);

        var result = N2nLinearRunner.Run(
            input, session, imageInput, strengthInput, output,
            blend: strength, overlap: overlap, ct: ct);

        var megapixels = (channels * srcW * (double)srcH) / 1_000_000.0;
        var throughputMpps = result.TotalMs > 0 ? megapixels * 1000.0 / result.TotalMs : 0.0;
        logger?.LogInformation(
            "N2nDenoiser.EnhanceAsync: {Model} {W}x{H}x{C} strength={Strength} tile={Tile} overlap={Overlap} chunks={Chunks} " +
            "stretch={Stretched} ({StretchMs}ms) prep={Prep}ms infer={Infer}ms stitch={Stitch}ms unstretch+blend={Unstretch}ms " +
            "throughput={Mpps:F2} Mp/s total={Total}ms level-restore |offset| median={OffsetMedian:E2} max={OffsetMax:E2}",
            ModelFileName, srcW, srcH, channels, strength, result.TileSize, overlap, result.ChunkCount,
            result.StretchApplied, result.StretchMs, result.PrepMs, result.InferMs, result.StitchMs, result.UnstretchMs,
            throughputMpps, result.TotalMs, result.LevelOffsetMedianAbs, result.LevelOffsetMaxAbs);

        return result.Output;
    }

    private InferenceSession AcquireSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null)
            {
                var modelPath = modelResolver.Resolve(ModelFileName);
                logger?.LogInformation("N2nDenoiser: loading {Model} from {Path}", ModelFileName, modelPath);
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
