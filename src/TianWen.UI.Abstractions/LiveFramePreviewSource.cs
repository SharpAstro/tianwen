using System;
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
    ///   <item><b>Subsampled stretch stats.</b> Median/MAD are derived from a strided ~1M-sample scan
    ///   (<see cref="Image.GetPedestralMedianAndMADScaledToUnit"/> with a grid stride), not the full
    ///   per-channel histogram + luma + background passes <see cref="AstroImageDocument"/> runs. A full
    ///   61 MP scan per frame on the render thread is what previously dragged the live session to ~1 fps.</item>
    ///   <item><b>Freeze.</b> <see cref="AcceptFrame"/> takes a <c>freezeStats</c> flag; while set, the cached
    ///   stats are reused (no rescan) so the display stretch does not re-fire on every exposure -- a
    ///   polar-align correctness requirement (the field must stay visually stable across the slow refine
    ///   exposures). A one-shot recompute fires on the freeze-off -> on edge so the frozen stats reflect the
    ///   current exposure regime.</item>
    /// </list>
    /// Channel data is normalised to <c>[0, 1]</c> at accept time (so every stretch mode -- including the
    /// linear <see cref="StretchMode.None"/> path -- displays correctly), and <see cref="ComputeStretchUniforms"/>
    /// delegates to the shared static <see cref="AstroImageDocument.ComputeStretchUniforms(StretchMode, StretchParameters, ChannelStretchStats[], ChannelStretchStats?, float, ValueTuple{float, float, float}?, ValueTuple{float, float, float}?, ValueTuple{float, float, float}?)"/>
    /// producer so the stretch math is identical to the document/SER path. There is no document, so still-only
    /// features (plate solve / stars / colour cal / info-panel metadata) are inactive for a live source.
    /// </summary>
    public sealed class LiveFramePreviewSource : IPreviewSource
    {
        // Owned, [0,1]-normalised per-channel buffers (channel-major, each height*width row-major). Reallocated
        // only when the frame geometry changes. Read on the render thread in GetChannelData; written in
        // AcceptFrame on the same (render) thread the consumer feeds frames from, so no cross-thread guard.
        private float[][] _channels = [];
        private int _width;
        private int _height;
        private int _channelCount;
        private SensorType _sensorType = SensorType.Monochrome;
        private int _bayerOffsetX;
        private int _bayerOffsetY;

        private ChannelStretchStats[] _stats = [];

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
            => (uint)channel < (uint)_channels.Length ? _channels[channel] : default;

        // A live raw preview drives no histogram display / info-panel stats; the consumers keep ShowHistogram +
        // ShowInfoPanel off. Returning empty keeps UploadHistogramData a no-op (it gates on ChannelCount > 0).
        /// <inheritdoc/>
        public ImageHistogram[] ChannelStatistics => [];

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
            (float R, float G, float B)? manualWhiteBalance = null)
        {
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
            var shaderWb = AstroImageDocument.ComposeWhiteBalance(null, manualWhiteBalance);
            return AstroImageDocument.ComputeStretchUniforms(
                mode, parameters, _stats, lumaStats: null, imageMaxValue: 1f,
                whiteBalance: null, lumaWeights: null, shaderWhiteBalance: shaderWb);
        }

        /// <summary>
        /// Accepts a new live frame: normalises each channel to <c>[0, 1]</c> into the owned buffers and (unless
        /// frozen) refreshes the subsampled stretch stats. Call on the render thread the consumer feeds from.
        /// </summary>
        /// <param name="image">The raw camera frame (mono, raw RGGB mosaic, or pre-debayered multi-channel).</param>
        /// <param name="freezeStats">When true, reuse the cached stats instead of rescanning -- except on the
        /// freeze-off -> on edge (a one-shot recompute) and on a geometry change (forced recompute).</param>
        public void AcceptFrame(Image image, bool freezeStats)
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

            var n = w * h;
            var geometryChanged = _width != w || _height != h || _channelCount != channelCount || _channels.Length != channelCount;
            if (geometryChanged)
            {
                _channels = new float[channelCount][];
                for (var c = 0; c < channelCount; c++)
                {
                    _channels[c] = new float[n];
                }
                _hasStats = false; // dims changed -> stats are stale
            }

            _width = w;
            _height = h;
            _channelCount = channelCount;
            _sensorType = sensorType;
            _bayerOffsetX = bayerX;
            _bayerOffsetY = bayerY;

            // Normalise raw [0, MaxValue] samples to [0, 1] so the display path (and the linear None mode) is
            // correct regardless of stretch mode -- the [0,1] convention every other IPreviewSource follows.
            var maxValue = image.MaxValue > 0f ? image.MaxValue : 1f;
            var inv = 1f / maxValue;
            for (var c = 0; c < channelCount; c++)
            {
                var src = image.GetChannelSpan(c);
                var dst = _channels[c];
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
                if (_stats.Length != channelCount)
                {
                    _stats = new ChannelStretchStats[channelCount];
                }

                var pixels = (long)w * h;
                var stride = pixels > StatsSampleTarget ? (int)Math.Sqrt((double)pixels / StatsSampleTarget) : 1;
                var (ped, med, mad) = image.GetPedestralMedianAndMADScaledToUnit(0, pixelStride: stride);
                for (var c = 0; c < _stats.Length; c++)
                {
                    _stats[c] = new ChannelStretchStats(ped, med, mad);
                }
                // The pedestal is the background estimate the post-stretch background math reads. Size it to
                // the channel count so the renderer's per-channel reads (0..2) never index an empty array.
                _perChannelBg = new float[Math.Max(1, channelCount)];
                Array.Fill(_perChannelBg, ped);
                _backgroundLevel = ped;
                _hasStats = true;
            }

            _previousFreeze = freezeStats;
            _frameCount++;
        }
    }
}
