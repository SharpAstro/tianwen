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
/// The in-house denoiser for one-shot-colour stacked masters: a 0.81 M-parameter UNet conditioned on a
/// per-pixel noise plane, trained on injected pairs (a master plus noise drawn at a known depth as the
/// input, the master itself as the target).
/// </summary>
/// <remarks>
/// <para><b>Provenance, stated because it changes how to treat this model.</b> The shipped weights are
/// <c>convmapb_s2</c>, seed 2 of E16b's arm (<c>docs/plans/denoiser-training.md</c>, "E16b's result"):
/// E16a's recipe on the recipe-3 store, the noise injected per channel on each master's own recorded
/// calibration and shaped by its integration, plus 45 bright cells. They replaced <c>e2_wide_s2</c> on
/// 2026-10-01, which took the tile's scalar sigma and was never shown a bright level being cleaned.
/// The seed is the one nearest its arm's mean on the two registered primary measures, not the best of
/// four, so the arm's published numbers describe what ships; the arm effect cleared its seed spread on
/// every field that carries it.</para>
///
/// <para><b>What it does that the model it replaced did not</b>, at full strength against half B on the
/// E16b eval (14 fields): it CLEANS a bright level, where every earlier model left one as it came or
/// worse. The finest-band error left at level 0.45-0.60 is 0.743 against the E16b control's 0.907 and
/// E16a's 0.947, and 0.862 at 0.60 and up, while <c>e2_wide_s2</c> left 1.36 and 2.05 (it added error
/// there); bright detail is kept at 0.991 and 1.000 against 0.849 and 0.844. On the densest cells of
/// the Sgr Star Cloud it keeps 0.999 of the unresolved stars' amplitude and 0.992 of their grain, where
/// <c>e2_wide_s2</c> kept 0.790 and 0.875, and in the NGC 362 core it moves no channel's level (that
/// one put red at 1.012 of half B's level against its input's 0.998, and more pixels toward clipping).
/// It removes somewhat less noise at full strength (22.6
/// percent on the eval's mean against 24.5), because its planes are calibrated to the noise it is
/// actually fed.</para>
///
/// <para><b>The plane is the runner's to compute</b> (<see cref="N2nLinearRunner"/>'s remarks): one
/// calibration per channel from the frame itself, then each chunk's plane from the tile the net is fed,
/// exactly as the eval's planes were made. A frame whose noise cannot be estimated (no 32 px block free
/// of the canvas ring) is refused rather than guessed at.</para>
///
/// <para><b>Domain semantics: linear in, linear out, the exporter's stretch in between.</b> The
/// contract at this boundary is a linear <c>[0, 1]</c> frame, the one every enhancer here takes, and
/// the net itself works in the MTF-stretched domain: every training tile was stored
/// after <see cref="ChunkedNafnetRunner.ApplyInputStretch"/>, so <see cref="N2nLinearRunner"/>
/// applies that call to the whole frame, runs, and inverts it before blending. Until 2026-09-02 the
/// runner fed the frame verbatim on the belief that the tiles were linear, which put a real master
/// about 100x below its training band; the measurement that settled it is in the runner's remarks.
/// The output is linear, so it chains with other linear-domain processing.</para>
///
/// <para><b>One-shot-colour only.</b> Every training session was OSC, so a mono input is rejected
/// rather than tiled across the three input slots: that would feed it a distribution nobody has
/// measured it on, and there is no mono weight bundle to fall back to.</para>
///
/// <para><b>The default local <see cref="IDenoiseEnhancer"/> since 2026-09-26</b>, when the SETI Astro
/// tier it was once opt-in beside was removed (its licence allows use only within SASpro).
/// <c>AddTianWenAi()</c> registers it, and <c>AddRcAstroAi()</c> makes it the fallback behind
/// NoiseXTerminator. It declines mono through <see cref="IEnhancerAvailability"/>, so the canonical
/// program leaves the denoise out there instead of failing.</para>
///
/// <para>Session lifecycle: one lazily-created <see cref="InferenceSession"/>, cached for the
/// lifetime of the instance and released on <see cref="Dispose"/>.</para>
/// </remarks>
public sealed class N2nDenoiser(
    IModelResolver modelResolver,
    ILogger<N2nDenoiser>? logger = null,
    float defaultStrength = 1.0f,
    int overlap = 64)
    : IDenoiseEnhancer, IEnhancerAvailability, IDisposable
{
    /// <summary>
    /// The shipped weights. The <c>convmapb_s2</c> segment is deliberate: the checkpoint identity is
    /// part of what this model is (see the provenance note on the class), so a retrain gets a new
    /// file name rather than silently replacing this one under the same one. That rule was honoured
    /// on 2026-09-06, when this stopped being <c>tianwen_denoise_osc_v19d.onnx</c>, and on 2026-10-01,
    /// when it stopped being <c>tianwen_denoise_osc_e2wide_s2.onnx</c>; the old weights are one
    /// <c>git show</c> away, and the seam probe's model-directory override exists to compare two
    /// checkpoints on one frame without swapping the file back.
    /// </summary>
    public const string ModelFileName = "tianwen_denoise_osc_convmapb_s2.onnx";

    private readonly System.Threading.Lock _gate = new(); // serializes lazy InferenceSession creation and Dispose; session build is a one-time cold path, not a hot-path hand-off
    private InferenceSession? _session;
    private bool _disposed;

    public string Name => "Denoiser (TianWen N2N, OSC)";

    /// <summary>
    /// Serves 3-channel input whose weights resolve, and nothing else: a mono frame is refused by
    /// design (one-shot-colour model, no mono bundle), and a checkout without the weights (or with an
    /// LFS pointer stub) has nothing to run. Answered by a file probe, never a session build, so the
    /// canonical program can ask it before any inference.
    /// </summary>
    public bool CanServe(int channelCount, EnhanceOptions options)
        => channelCount == 3 && modelResolver.TryResolve(ModelFileName, out _);

    public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, defaultStrength, cancellationToken);

    /// <summary>
    /// The variant axis names weight bundles (Default / Lite / Walking) and there is exactly one
    /// bundle here, so anything but <see cref="DenoiseVariant.Default"/> is refused
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
    /// sub-step stream, so the pipeline's own step-boundary ticks are the progress.
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
                "It has no mono weight bundle; a mono frame is denoised by RC-Astro NoiseXTerminator or not at all.");
        }
        // The trainer fed tiles normalised by Image.UnitScaleDivisor, so anything far outside
        // [0, 1] is a miscalibrated input (raw camera ADU being the usual case) rather than a
        // frame this model can be expected to handle. A 1.5 tolerance, because enhanced masters can
        // overshoot slightly above 1.
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
        // The graph's inputs say which conditioning it takes: a per-pixel plane (E16's, the shipped weights since
        // 2026-10) or the tile's scalar sigma computed in-graph (every model before it).
        N2nRunResult result;
        if (OnnxIoNames.IsImagePlusPlane(session))
        {
            var (imageInput, planeInput, output) = OnnxIoNames.ImagePlusPlane(session);
            result = N2nLinearRunner.Run(input, session, imageInput, null, planeInput, output, blend: strength, overlap: overlap, ct: ct);
        }
        else
        {
            var (imageInput, strengthInput, output) = OnnxIoNames.ImagePlusScalar(session);
            result = N2nLinearRunner.Run(input, session, imageInput, strengthInput, null, output, blend: strength, overlap: overlap, ct: ct);
        }

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
