using System;
using System.Runtime.InteropServices;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// A lightweight <see cref="IPreviewSource"/> over a live camera frame, for previewing a raw stream in the
    /// full <see cref="ImageRendererBase{TSurface}"/> viewer (Live Session preview / guide cam / polar-align)
    /// without paying the heavy <see cref="AstroImageDocument.AdoptImageAsync"/> path on every frame.
    /// <para>
    /// It keeps the mini viewer's two performance tricks that a per-frame document would lose:
    /// </para>
    /// <list type="number">
    ///   <item><b>Subsampled statistics.</b> Everything is derived from a strided ~1M-sample scan, not
    ///   from the full-resolution passes <see cref="AstroImageDocument"/> runs, and the luma, background-
    ///   region and star passes are skipped entirely. A full 61 MP scan per frame on the render thread is
    ///   what previously dragged the live session to ~1 fps. The per-channel medians, MADs and histograms
    ///   themselves come from the SAME collectors a document uses
    ///   (<see cref="StretchSolver.CollectPerChannelStats"/> / <see cref="StretchSolver.CollectChannelHistograms"/>),
    ///   so what a channel IS -- three colours for a Bayer mosaic, one per plane otherwise -- is decided
    ///   in one place for both.</item>
    ///   <item><b>Freeze.</b> <see cref="AcceptFrame"/> takes a <c>freezeStats</c> flag; while set, the cached
    ///   stats are reused (no rescan) so the display stretch does not re-fire on every exposure -- a
    ///   polar-align correctness requirement (the field must stay visually stable across the slow refine
    ///   exposures). A one-shot recompute fires on the freeze-off -> on edge so the frozen stats reflect the
    ///   current exposure regime.</item>
    /// </list>
    /// Channel data is normalised to <c>[0, 1]</c> at accept time (so every stretch mode -- including the
    /// linear <see cref="StretchMode.None"/> path -- displays correctly), and <see cref="ComputeStretchUniforms"/>
    /// delegates to the shared <see cref="StretchSolver.ComputeStretchUniforms(StretchMode, StretchParameters, ChannelStretchStats[], ChannelStretchStats?, float, ValueTuple{float, float, float}?, ValueTuple{float, float, float}?, ValueTuple{float, float, float}?)"/>
    /// producer so the stretch math is identical to the document/SER path. There is no document, so still-only
    /// features (plate solve / stars / colour cal / info-panel metadata) are inactive for a live source.
    /// </summary>
    public sealed class LiveFramePreviewSource : IPreviewSource
    {
        // Owned, [0,1]-normalised per-channel planes ([height, width], row-major). Reallocated only when the
        // frame geometry changes. Read on the render thread in GetChannelData; written in AcceptFrame on the
        // same (render) thread the consumer feeds frames from, so no cross-thread guard. PLANES rather than
        // flat arrays so the display histograms can be taken over them after the frame itself has gone
        // back to the camera (see ChannelStatistics).
        private float[][,] _planes = [];
        private int _width;
        private int _height;
        private int _channelCount;
        private SensorType _sensorType = SensorType.Monochrome;
        private int _bayerOffsetX;
        private int _bayerOffsetY;
        // The frame's metadata, for the view the display histograms are taken over: its sensor type and
        // Bayer offsets decide the channel rule there exactly as they did on the frame.
        private ImageMeta _meta;
        private int _statsStride = 1;

        private ChannelStretchStats[] _stats = [];
        // The display histograms of the latest statistics, taken on FIRST READ; null until then.
        private ImageHistogram[]? _histograms;

        // Background level (= the subsampled pedestal) the post-stretch background math reads. Sized to the
        // channel count (min 1) so the renderer's per-channel ComputePostStretchBackground never indexes past
        // the end -- it reads channels 0..2 unconditionally and falls back to [0], so an empty array crashes.
        private float[] _perChannelBg = [0f];
        private float _backgroundLevel;

        private bool _hasStats;
        private bool _previousFreeze;
        private long _frameCount;

        // ~1M-sample target for the subsampled median/MAD scan (matches the mini viewer's heuristic).
        private const long StatsSampleTarget = 1_000_000L;

        /// <inheritdoc/>
        public int Width => _width;

        /// <inheritdoc/>
        public int Height => _height;

        /// <inheritdoc/>
        public int ChannelCount => _channelCount;

        /// <inheritdoc/>
        public SensorType SensorType => _sensorType;

        /// <inheritdoc/>
        public int BayerOffsetX => _bayerOffsetX;

        /// <inheritdoc/>
        public int BayerOffsetY => _bayerOffsetY;

        /// <inheritdoc/>
        public ReadOnlySpan<float> GetChannelData(int channel)
            => (uint)channel < (uint)_planes.Length ? PlaneSpan(_planes[channel]) : default;

        private static Span<float> PlaneSpan(float[,] plane)
            => plane.Length > 0 ? MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length) : default;

        /// <inheritdoc/>
        /// <remarks>
        /// <para>Three for a Bayer mosaic, one per plane otherwise -- the channel rule lives in
        /// <see cref="StretchSolver.CollectChannelHistograms"/> and nowhere else. These used to be empty,
        /// which made <c>UploadHistogramData</c> a no-op and left the histogram key (<c>H</c>) dead in the GUI's live session
        /// and guider previews: a chromeless host draws no toolbar, but the histogram overlay is gated on
        /// <see cref="ViewerState.ShowHistogram"/> alone, so there was nothing to draw rather than
        /// nowhere to draw it.</para>
        /// <para><b>Taken on first read, not per exposure.</b> Both live hosts start with the overlay
        /// hidden and the renderer asks only once it is drawn, so an exposure nobody looks at the
        /// histogram of costs no histogram pass and no bins: 256 KB a channel for a 16-bit sensor, which
        /// was every guide frame. They are taken over the normalised planes, the frame's own pixels over
        /// [0, 1], so they draw the same picture a histogram of the raw frame does, at a fixed 65536 bins
        /// that also keep the renderer's display one size from exposure to exposure. A new array per
        /// statistics refresh, never one filled again: an <see cref="ImageHistogram"/> is immutable.</para>
        /// </remarks>
        public ImageHistogram[] ChannelStatistics => _histograms ??= CollectDisplayHistograms();

        private ImageHistogram[] CollectDisplayHistograms()
        {
            if (_planes.Length == 0 || _width == 0 || _height == 0)
            {
                return [];
            }

            // A view over the planes, which it neither copies nor owns: [0, 1] Float32, so the histogram
            // bins it at the unit scale.
            var view = new Image(_planes, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: _meta);
            return StretchSolver.CollectChannelHistograms(view, _statsStride);
        }

        /// <inheritdoc/>
        public float[] PerChannelBackground => _perChannelBg;

        /// <inheritdoc/>
        public float LumaBackground => _backgroundLevel;

        /// <inheritdoc/>
        public int FrameCount => (int)Math.Min(_frameCount, int.MaxValue);

        /// <inheritdoc/>
        public int FrameIndex => _frameCount > 0 ? (int)Math.Min(_frameCount - 1, int.MaxValue) : 0;

        /// <summary>A live stream is not seekable; always returns false.</summary>
        public bool SelectFrame(int index) => false;

        /// <inheritdoc/>
        public bool HasTimestamps => false;

        /// <inheritdoc/>
        public DateTimeOffset TimestampOf(int index) => DateTimeOffset.MinValue;

        /// <inheritdoc/>
        public StretchUniforms ComputeStretchUniforms(
            StretchMode mode,
            StretchParameters parameters,
            LumaWeighting weighting = LumaWeighting.Rec709,
            float lumaBlend = 1f,
            bool normalize = false,
            int curvesMode = 0,
            ReadOnlySpan<float> curveLut = default,
            float curvesBoost = 0f,
            float curvesMidpoint = 0.25f,
            float hdrAmount = 0f,
            float hdrKnee = 0.8f,
            float bgNeutralizationStrength = 1f,
            (float R, float G, float B)? manualWhiteBalance = null,
            bool applyColorCalibration = true)
        {
            // applyColorCalibration is a document concern: a live frame carries no measured calibration
            // to switch off, so the flag has nothing to act on here.
            if (_stats.Length == 0)
            {
                return new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default);
            }

            // Manual WB on a live source is purely the shader multiply (there is no auto calibration to scale
            // the stats against), mirroring the document's manual-WB semantics. Stats are already unit-scaled,
            // so imageMaxValue is 1 (channel data is normalised to [0,1] in AcceptFrame). Luma/normalize/curves/
            // HDR/background-neutralization are document features with no live-raw analogue, so they are not
            // applied here. The static producer is the single source of the stretch math (shared with the
            // document + SER paths).
            var shaderWb = StretchSolver.ComposeWhiteBalance(null, manualWhiteBalance);
            // A live frame carries no measured calibration, so Auto resolves on channel count alone:
            // colour -> Unlinked (neutralise per channel), mono -> Linked.
            var isColour = _channelCount >= 3 || _sensorType is SensorType.RGGB;
            mode = mode.ResolveAuto(isColour, calibrationActive: false);
            return StretchSolver.ComputeStretchUniforms(
                mode, parameters, _stats, lumaStats: null, imageMaxValue: 1f,
                whiteBalance: null, lumaWeights: null, shaderWhiteBalance: shaderWb);
        }

        /// <summary>
        /// Accepts a new live frame: normalises each channel to <c>[0, 1]</c> into the owned buffers and (unless
        /// frozen) refreshes the subsampled stretch stats. Call on the render thread the consumer feeds from.
        /// </summary>
        /// <remarks>
        /// <b>Frame ownership: a BORROW.</b> The frame is its publisher's (a session's
        /// <c>LastCapturedImages</c> slot, a guider's <c>LastGuideFrame</c>), and the publisher releases it
        /// when IT is done, from its own thread: an autofocus rung straight after star detection, a guide
        /// frame as its successor lands. So the frame is leased for the copy and given back the moment the
        /// copy exists, and one released before the lease is skipped, leaving the exposure already shown.
        /// The copy is the whole reason no reference is kept: the owner's release still recycles the frame.
        /// Reading through the bare reference threw on the render thread in the live check of 2026-09-25
        /// and took the display down mid-autofocus.
        /// </remarks>
        /// <param name="image">The raw camera frame (mono, raw RGGB mosaic, or pre-debayered multi-channel).</param>
        /// <param name="freezeStats">When true, reuse the cached stats instead of rescanning -- except on the
        /// freeze-off -> on edge (a one-shot recompute) and on a geometry change (forced recompute).</param>
        /// <returns><see langword="true"/> when the frame was copied in; <see langword="false"/> when its owner
        /// had already given it back, which leaves everything here as it was.</returns>
        public bool AcceptFrame(Image image, bool freezeStats)
        {
            if (!image.TryLease(out var lease))
            {
                return false;
            }

            using (lease)
            {
                CopyIn(lease.Image, freezeStats);
            }

            return true;
        }

        private void CopyIn(Image image, bool freezeStats)
        {
            var w = image.Width;
            var h = image.Height;
            var meta = image.ImageMeta;

            // Layout from the ACTUAL frame, not a nominal SensorType: a raw RGGB mosaic arrives as 1 channel
            // (GPU debayers); a pre-debayered colour frame arrives as 3. Mono is 1. (Mirrors the mini viewer +
            // UploadDocumentTextures.)
            int channelCount;
            SensorType sensorType;
            int bayerX = 0, bayerY = 0;
            if (meta.SensorType is SensorType.RGGB && image.ChannelCount == 1)
            {
                channelCount = 1;
                sensorType = SensorType.RGGB;
                bayerX = meta.BayerOffsetX;
                bayerY = meta.BayerOffsetY;
            }
            else
            {
                channelCount = image.ChannelCount;
                sensorType = meta.SensorType;
            }

            var geometryChanged = _width != w || _height != h || _channelCount != channelCount || _planes.Length != channelCount;
            if (geometryChanged)
            {
                _planes = new float[channelCount][,];
                for (var c = 0; c < channelCount; c++)
                {
                    _planes[c] = new float[h, w];
                }
                _hasStats = false; // dims changed -> stats are stale
            }

            _width = w;
            _height = h;
            _channelCount = channelCount;
            _sensorType = sensorType;
            _bayerOffsetX = bayerX;
            _bayerOffsetY = bayerY;
            _meta = meta;

            // Normalise raw [0, MaxValue] samples to [0, 1] so the display path (and the linear None mode) is
            // correct regardless of stretch mode -- the [0,1] convention every other IPreviewSource follows.
            var maxValue = image.MaxValue > 0f ? image.MaxValue : 1f;
            var inv = 1f / maxValue;
            for (var c = 0; c < channelCount; c++)
            {
                var src = image.GetChannelSpan(c);
                var dst = PlaneSpan(_planes[c]);
                var count = Math.Min(src.Length, dst.Length);
                for (var i = 0; i < count; i++)
                {
                    dst[i] = src[i] * inv;
                }
            }

            // Stats: recompute on (a) geometry change / first frame, (b) the normal (unfrozen) per-frame path,
            // (c) the freeze-off -> on edge (a one-shot refresh so the frozen stats reflect the current
            // exposure). After (c), subsequent frozen frames reuse the cache.
            var freezeEdgeOn = freezeStats && !_previousFreeze;
            if (!_hasStats || !freezeStats || freezeEdgeOn)
            {
                // The SAME collectors a document uses, so the GUI's live preview and the file on disk
                // cannot disagree about what a channel is. A Bayer mosaic is three colours in one plane
                // and gets three of each: one pooled statistic broadcast three ways is what made Linked
                // and Unlinked render identically on every OSC frame and left background neutralisation
                // nothing to level -- Auto resolves a mosaic to Unlinked just above, so without this that
                // resolution bought nothing at all.
                //
                // The stride bounds the scan, and costs no more samples than the single full-frame scan
                // this replaced: a CFA walk doubles an odd stride to stay on its phase, so red, green
                // (both its phases) and blue together visit what one scan at this stride visits.
                var pixels = (long)w * h;
                var stride = pixels > StatsSampleTarget ? (int)Math.Sqrt((double)pixels / StatsSampleTarget) : 1;
                _statsStride = stride;

                _stats = StretchSolver.CollectPerChannelStats(image, channelCount, stride);

                // The histograms the overlay draws (H) are NOT taken here: they are taken over the planes
                // on first read (ChannelStatistics), at this same stride, and dropping them is what makes
                // the next read describe this exposure. Taken here, every exposure paid a histogram pass
                // and a set of bins that only an open overlay ever looked at.
                _histograms = null;

                // The pedestal is the background estimate the post-stretch background math reads. Sized to
                // the STAT count, which is the channel count except on a mosaic, so the renderer's
                // per-channel reads (0..2) never index an empty array.
                _perChannelBg = new float[Math.Max(1, _stats.Length)];
                for (var c = 0; c < _perChannelBg.Length; c++)
                {
                    _perChannelBg[c] = _stats[c].Pedestal;
                }
                // Green for a mosaic: it is the luma-dominant channel and the one a luminance background
                // would be led by, and unlike slot 0 it is not whichever colour the pattern starts with.
                _backgroundLevel = _stats.Length >= 3 ? _stats[1].Pedestal : _stats[0].Pedestal;
                _hasStats = true;
            }

            _previousFreeze = freezeStats;
            _frameCount++;
        }
    }
}
