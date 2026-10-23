using System;
using System.Collections.Immutable;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// An immutable snapshot of everything the display pipeline reads that is NOT pixels: the stretch
    /// uniforms, plus the curve / HDR dials that live on <see cref="ViewerState"/> rather than inside
    /// <see cref="StretchUniforms"/>.
    /// </summary>
    /// <remarks>
    /// <para>This exists so the before/after split can render the SAME pixels two ways. A rendition is
    /// the whole answer to "how is this frame being displayed", so the comparison half needs one of its
    /// own: handing it a bare <see cref="StretchUniforms"/> would leave it sharing the live curve and
    /// HDR settings, and the pinned half would drift as the user moved them -- a comparison against a
    /// moving target, which looks like the pin not working.</para>
    /// <para>A rendition costs a handful of floats and a shared immutable array, which is what makes
    /// uniform-only A/B free: no second texture, no second copy of anything. Comparing PIXELS is the
    /// half with a memory story -- see docs/plans/before-after-slider.md.</para>
    /// </remarks>
    public readonly record struct DisplayRendition(
        StretchUniforms Stretch,
        float CurvesBoost,
        float HdrAmount,
        float HdrKnee,
        int CurvesMode,
        ImmutableArray<float> CurveData)
    {
        /// <summary>
        /// The curve knots as a span, empty when no spline curve is set. Goes through
        /// <see cref="ImmutableArray{T}.IsDefaultOrEmpty"/> because a DEFAULT
        /// <see cref="ImmutableArray{T}"/> cannot be spanned at all (it has no backing array), and a
        /// rendition built by <c>new()</c> rather than the primary constructor has exactly that.
        /// </summary>
        public ReadOnlySpan<float> CurveSpan => CurveData.IsDefaultOrEmpty ? default : CurveData.AsSpan();

        /// <summary>
        /// The asinh beta a star split's stars are drawn with (<see cref="ViewerState.StarStretch"/>), so the pinned
        /// half of a comparison keeps its own.
        /// </summary>
        /// <remarks>Stored as its distance from <see cref="StarSplit.StarCurve"/>, so a <c>new()</c> rendition, which skips
        /// property initialisers, reads the default and not 0, below the curve's least beta of 1.</remarks>
        public float StarStretch
        {
            get => StarSplit.StarCurve + _starStretchOffset;
            init => _starStretchOffset = value - StarSplit.StarCurve;
        }

        private readonly float _starStretchOffset;

        /// <summary>The star split's star saturation (<see cref="ViewerState.StarSaturation"/>), stored as its distance from
        /// <see cref="StarSplit.StarSaturation"/> for the same reason as <see cref="StarStretch"/>.</summary>
        public float StarSaturation
        {
            get => StarSplit.StarSaturation + _starSaturationOffset;
            init => _starSaturationOffset = value - StarSplit.StarSaturation;
        }

        private readonly float _starSaturationOffset;

        /// <summary>
        /// Snapshots how the viewer is displaying the frame right now. The Tone popover's finish (masked contrast and
        /// saturation) and the Background popover's reduce travel INSIDE the stretch, as the headless render carries it, so every path that renders a
        /// rendition, the GPU, the CPU save and the split's pinned half, applies the same finish.
        /// </summary>
        public static DisplayRendition FromState(StretchUniforms stretch, ViewerState state)
            => new DisplayRendition(stretch with
                {
                    FinishContrast = state.FinishContrast,
                    FinishSaturation = state.FinishSaturation,
                    BackgroundReduce = state.BackgroundReduce,
                },
                state.CurvesBoost, state.HdrAmount, state.HdrKnee, state.CurvesMode, state.CurveData)
            {
                StarStretch = state.StarStretch,
                StarSaturation = state.StarSaturation,
            };
    }

    /// <summary>Which display-parameter slot a draw reads. Two draws share one command buffer, so each
    /// needs its own slot -- see the backend's UBO-slot notes.</summary>
    public enum RenditionSlot
    {
        /// <summary>The live rendition: what the viewer shows with the split off.</summary>
        Live = 0,

        /// <summary>The comparison rendition shown on the split's left half.</summary>
        Comparison = 1,
    }

    /// <summary>What the before/after split's left half shows.</summary>
    public enum SplitCompare
    {
        /// <summary>
        /// The same pixels re-rendered with a pinned snapshot of the display settings. Costs no extra
        /// memory whatsoever, so it needs no policy and is always available once something is pinned.
        /// </summary>
        PinnedSettings,

        /// <summary>
        /// The same pixels and the same settings, with and without the display CROP: the left half
        /// shows the whole frame, the right the crop. Needs a crop to be in force and nothing else --
        /// no retained texture, no pinned snapshot, because neither half is a different rendition.
        /// </summary>
        /// <remarks>
        /// It answers the question a crop actually raises -- what did that take off? -- which no other
        /// mode can: comparing pixels needs an enhance to have retained some, and comparing settings
        /// shows two identically-framed halves. Distinct from the enhance comparison on purpose: once a
        /// crop has been baked into an enhance, the two halves must show the SAME region and differ only
        /// by the enhancement, or the comparison is answering two questions at once.
        /// </remarks>
        CropExtent,

        /// <summary>
        /// The retained pre-enhance pixels, rendered with the CURRENT display settings (so moving a
        /// slider moves both halves together and only the pixels differ). Needs the backend to be
        /// holding a before texture set; unavailable otherwise.
        /// </summary>
        BeforePixels,
    }
}
