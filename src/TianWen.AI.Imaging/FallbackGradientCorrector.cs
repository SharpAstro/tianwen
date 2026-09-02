using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging
{
    /// <summary>
    /// The <see cref="IGradientCorrector"/> a composition root gets from <c>AddTianWenAi()</c>: GraXpert's
    /// BGE model when its weights are installed, the classical robust fit otherwise. Decided per call from
    /// <see cref="IModelResolver.TryResolve"/>, which is a file probe, so installing GraXpert later takes
    /// effect on the next enhance with no restart, and nothing is loaded until an enhance actually runs.
    /// </summary>
    /// <remarks>
    /// <para>Before this, a machine without GraXpert had no gradient correction at all: <c>flatten</c>
    /// and the pipeline's gradient step failed on a missing model file. The classical fit is the AI-free
    /// fallback the product needs anyway (roadmap section 1, item 2), so it is what a missing model now
    /// resolves to. The two produce the same interop surface (a full-resolution background plate in the
    /// image's units, the correction a subtraction with the level added back), so a caller cannot tell
    /// which answered except by <see cref="Name"/>, and the log says so once.</para>
    /// <para>This is a two-way pick on model presence, not a <c>DeferredEnhancer</c>: there is no
    /// RC-Astro product for this role, no licence to probe, and <see cref="EnhanceBackend"/> has no
    /// value for it. If a per-call selection is ever wanted, it goes through that enum like the other
    /// roles.</para>
    /// </remarks>
    public sealed class FallbackGradientCorrector(
        IModelResolver modelResolver,
        OnnxBackgroundExtractor graXpert,
        ClassicalBackgroundExtractor classical,
        ILogger<FallbackGradientCorrector>? logger = null)
        : IGradientCorrector
    {
        private int _fallbackAnnounced;

        /// <summary>The corrector the next call would run.</summary>
        internal IGradientCorrector Pick()
        {
            if (modelResolver.TryResolve(OnnxBackgroundExtractor.ModelName, out _))
            {
                return graXpert;
            }
            if (Interlocked.Exchange(ref _fallbackAnnounced, 1) == 0)
            {
                logger?.LogInformation(
                    "Gradient correction: {Model} is not installed (GraXpert's weights are read from its own install, or from %LOCALAPPDATA%/TianWen/models via tools/tianwen-ai-models-fetch.ps1); using the classical robust fit instead.",
                    OnnxBackgroundExtractor.ModelName);
            }
            return classical;
        }

        public string Name => Pick().Name;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Pick().EnhanceAsync(input, cancellationToken);

        public Task<(Image Corrected, Image? Background)> EnhanceAndEstimateBackgroundAsync(Image input, CancellationToken cancellationToken = default)
            => Pick().EnhanceAndEstimateBackgroundAsync(input, cancellationToken);
    }
}
