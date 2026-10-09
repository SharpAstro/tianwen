using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using TianWen.AI.Inference;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.Onnx;

/// <summary>
/// TianWen's own deconvolver (E3.4d, #844): Richardson-Lucy with a learned prior after every update, one ONNX graph that
/// takes the KERNEL as an input (<c>docs/plans/deconvolver-training.md</c>, section 6). Served as the whole-frame
/// <see cref="IImageDeblurrer"/>, before any star removal, because that is what it was measured on: every readout
/// deconvolved a master with its stars in and judged it by them (width, skirt, ring, stars kept).
/// </summary>
/// <remarks>
/// <para><b>It serves only a run that names it AND states its kernel</b> (<see cref="EnhanceBackend.TianWen"/> and
/// <see cref="EnhanceTuning.Deconvolution"/>; the owner's call, 2026-10-09). Nothing yet finds the blur in a single
/// frame or declines one with nothing to remove, and on a window with no blur the operator does not decline, it
/// harms (E7.5: twice the truth's width on an unblurred corner). <see cref="EnhanceBackend.Auto"/> therefore passes it
/// over until #741 gives it both; the rule is <see cref="CanServe"/> and nowhere else.</para>
///
/// <para><b>It loads only against its contract</b> (#824): <c>tianwen_deconv_operator_e34d_s0.contract.json</c> beside
/// the weights states their SHA-256, the four inputs by role (the image, the kernel, the stretch's minimum and balance)
/// and the domain (MTF-stretched to a median of 0.25), and the first use refuses on any difference from what
/// <see cref="Feed"/> says the runner feeds. A model with no contract does not serve at all.</para>
///
/// <para>One-shot-colour only, like the training data: the graph takes three channels and its prior mixes them, so a
/// mono frame is declined rather than tiled.</para>
/// </remarks>
public sealed class OnnxTianWenDeconvolver(
    IModelResolver modelResolver,
    ILogger<OnnxTianWenDeconvolver>? logger = null,
    int? tile = null)
    : IImageDeblurrer, IEnhancerAvailability, IDisposable
{
    /// <summary>E3.4d seed 0's final checkpoint, exported by <c>training/denoise/n2n_operator_export.py</c>. A retrain gets
    /// a new file name and a new contract.</summary>
    public const string ModelFileName = "tianwen_deconv_operator_e34d_s0.onnx";

    private const int ColourChannels = 3;

    private readonly string _modelFileName = ModelFileName;
    private readonly System.Threading.Lock _gate = new(); // serializes lazy InferenceSession creation and Dispose; session build is a one-time cold path, not a hot-path hand-off
    private (InferenceSession Session, OperatorGraphNames Names)? _loaded;
    private bool _disposed;

    /// <summary>A deconvolver over another graph with the same interface (the C# tests' fixture graph).</summary>
    internal OnnxTianWenDeconvolver(string modelFileName, IModelResolver modelResolver, int? tile)
        : this(modelResolver, null, tile)
    {
        _modelFileName = modelFileName;
    }

    public string Name => "Deblur (TianWen E3.4d operator)";

    /// <summary>
    /// Serves a three-channel frame under <see cref="EnhanceBackend.TianWen"/> with a kernel stated, when the weights and
    /// their contract resolve; nothing else. Answered by file probes, never a session build or a hash.
    /// </summary>
    public bool CanServe(int channelCount, EnhanceOptions options)
        => channelCount == ColourChannels
            && options.Backend == EnhanceBackend.TianWen
            && options.Tuning?.Deconvolution is not null
            && modelResolver.TryResolve(_modelFileName, out var modelPath)
            && modelPath is { } path
            && File.Exists(ModelContract.PathBeside(path));

    /// <summary>Declines (returns <paramref name="input"/> itself): with no options there is no kernel and no request.</summary>
    public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        => EnhanceAsync(input, EnhanceOptions.Default, null, cancellationToken);

    /// <summary>
    /// Deconvolves <paramref name="input"/> with the kernel <paramref name="options"/> states, under
    /// <see cref="EnhanceBackend.TianWen"/>; otherwise DECLINES by returning <paramref name="input"/> itself, which the
    /// pipeline reads as a skipped deblur, exactly as the RC passthrough declines an unlicensed BlurX.
    /// </summary>
    public Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (options.Backend != EnhanceBackend.TianWen || options.Tuning?.Deconvolution is not { } kernel)
        {
            logger?.LogInformation(
                "OnnxTianWenDeconvolver: declined, it serves only --ai-backend tianwen with the blur stated (--deconv-kernel); nothing finds it in a single frame yet (#741)");
            return Task.FromResult(input);
        }
        if (input.ChannelCount != ColourChannels)
        {
            throw new NotSupportedException(
                $"{nameof(OnnxTianWenDeconvolver)} is a one-shot-colour model and requires 3 channels, got {input.ChannelCount}.");
        }
        if (input.MaxValue > 1.5f)
        {
            throw new ArgumentException(
                $"{nameof(OnnxTianWenDeconvolver)} requires input normalised to ~[0, 1], got MaxValue={input.MaxValue}. " +
                "Use AstroImageDocument.AdoptImageAsync or Image.ScaleFloatValuesToUnitInPlace first.",
                nameof(input));
        }

        return Task.Run(() => Run(input, kernel, progress, cancellationToken), cancellationToken);
    }

    private Image Run(Image input, DeconvolutionKernel kernel, IProgress<float>? progress, CancellationToken ct)
    {
        var (session, names) = Acquire();
        var result = OperatorDeconvolutionRunner.Run(input, session, names, kernel, tile, progress, ct);
        var (_, width, height) = input.Shape;
        logger?.LogInformation(
            "OnnxTianWenDeconvolver: {Model} {W}x{H} kernel FWHM {Red}/{Green}/{Blue} px beta {Beta} at {Resample}x ({ZoomW}x{ZoomH}, kernel {KernelSize} px), " +
            "{Tiles} tiles of {Tile} px; stretch min {Min} balance {Balance}; resample up {Up} ms, infer {Infer} ms, down + unstretch {Down} ms, total {Total} ms",
            _modelFileName, width, height, kernel.FwhmRed, kernel.FwhmGreen, kernel.FwhmBlue, kernel.Beta, kernel.Resample,
            result.ZoomedWidth, result.ZoomedHeight, result.KernelSize, result.Tiles, result.TileEdge,
            string.Join("/", result.StretchMin), string.Join("/", result.Balances),
            result.ResampleUpMs, result.InferMs, result.ResampleDownMs, result.TotalMs);
        return result.Output;
    }

    private (InferenceSession Session, OperatorGraphNames Names) Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loaded ??= Open();
            return _loaded.Value;
        }
    }

    /// <summary>
    /// Builds the session against the contract beside the resolved weights, and only then: the contract is read first,
    /// checked once the graph is open, and a session that fails it is disposed and never kept. The graph's tensors are
    /// bound by the names the contract gives each role, since two inputs share a rank.
    /// </summary>
    private (InferenceSession, OperatorGraphNames) Open()
    {
        var modelPath = modelResolver.Resolve(_modelFileName);
        logger?.LogInformation("OnnxTianWenDeconvolver: loading {Model} from {Path}", _modelFileName, modelPath);
        try
        {
            var contract = ModelContract.LoadBeside(modelPath);
            // DirectML where the resolver prefers it: about 6.5 times the CPU on a 1312 px tile, agreeing with it to 4.8e-7.
            // It runs this graph only because the exporter clears allowzero on every Reshape (n2n_operator_export.py's
            // clear_reshape_allowzero); with torch.export's allowzero = 1 the session BUILD failed.
            using var options = ExecutionProviderResolver.CreateSessionOptions(deviceId: 0, logger: logger);
            var session = new InferenceSession(modelPath, options);
            var problems = contract.Check(modelPath, Feed, ModelGraph.From(session));
            if (problems.Length > 0)
            {
                session.Dispose();
                throw ModelContractException.Refused(_modelFileName, ModelContract.PathBeside(modelPath), problems);
            }
            var names = new OperatorGraphNames(
                NameOf(contract, ModelRoles.Image),
                NameOf(contract, ModelRoles.Kernel),
                NameOf(contract, ModelRoles.StretchMin),
                NameOf(contract, ModelRoles.StretchBalance),
                contract.Output);
            logger?.LogInformation(
                "OnnxTianWenDeconvolver: {Model} matches its contract (v{Version}, domain {Domain}, weights {Sha})",
                _modelFileName, contract.ContractVersion, contract.Domain, contract.OnnxSha256);
            return (session, names);
        }
        catch (ModelContractException e)
        {
            logger?.LogError(e, "OnnxTianWenDeconvolver: {Model} refused by its contract", _modelFileName);
            throw;
        }
    }

    // A checked contract describes every role the feed names, so a missing one is a broken invariant, not a refusal.
    private static string NameOf(ModelContract contract, string role)
        => contract.InputFor(role)?.Name ?? throw new InvalidOperationException($"a checked contract describes no '{role}' input");

    /// <summary>
    /// What the runner feeds the graph, from its own constants and never from the contract it is checked against: the
    /// frame MTF-stretched to <see cref="AiNafnetInputs.TargetMedian"/> as three channels, a kernel per channel, and the
    /// stretch's per-channel minimum and balance.
    /// </summary>
    internal static ModelFeed Feed { get; } = new ModelFeed(
        ModelDomain.MtfStretched,
        AiNafnetInputs.TargetMedian,
        [
            new ModelFeedInput(ModelRoles.Image, ColourChannels),
            new ModelFeedInput(ModelRoles.Kernel, ColourChannels),
            new ModelFeedInput(ModelRoles.StretchMin, ColourChannels),
            new ModelFeedInput(ModelRoles.StretchBalance, ColourChannels),
        ]);

    public void Dispose()
    {
        lock (_gate)
        {
            _loaded?.Session.Dispose();
            _loaded = null;
            _disposed = true;
        }
    }
}
