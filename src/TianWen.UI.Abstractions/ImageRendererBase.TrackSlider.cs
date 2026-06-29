using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // -----------------------------------------------------------------------
        // TrackSlider -- the one horizontal press/drag/release track
        //
        // The manual white-balance sliders, the 6 wavelet-layer sliders, and the SER transport scrub are all
        // the same widget: a horizontal track (unfilled bar + played/value fill + a draggable handle) plus a
        // cursor-X -> fraction mapping against a captured hit-band rect. Both the render and the drag math used
        // to be copy-pasted three times; they live here once. The track background + handle colours are the
        // shared Transport* chrome (identical at every old call site); only the fill colour and the per-frame
        // fraction vary, so the caller passes those plus the band/handle geometry and the hit payload.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Draws one horizontal track slider and registers its drag hit-band. <paramref name="frac"/> is the
        /// normalised fill/handle position in [0, 1]. <paramref name="barCenterY"/> is the vertical centre of
        /// the thin track bar; the draggable handle is drawn as a <paramref name="handleH"/>-tall marker at
        /// <paramref name="handleY"/>. <paramref name="hitBand"/> is the full press/drag region (its X/Width
        /// drive the cursor-X -> value mapping in the matching Update*; the caller also stores it in the
        /// slider's track-rect field for that drag). The track + handle colours are the shared Transport
        /// chrome; <paramref name="fillColor"/> is the per-slider accent.
        /// </summary>
        private void DrawTrackSlider(float trackX, float trackW, float barCenterY,
            float handleY, float handleH, float frac, RGBAColor32 fillColor, RectF32 hitBand, HitResult hit)
        {
            var barH = MathF.Max(4f, 6f * DpiScale);
            var handleW = MathF.Max(4f, 6f * DpiScale);

            var barY = barCenterY - barH / 2f;
            FillRect(trackX, barY, trackW, barH, TransportTrackBg);
            FillRect(trackX, barY, trackW * frac, barH, fillColor);

            // Handle marker; guard the clamp's upper bound for a sliver-thin track (trackW < handleW would
            // make Math.Clamp's max < min and throw -- the minimize-to-sliver crash).
            var handleMax = MathF.Max(trackX, trackX + trackW - handleW);
            var handleX = Math.Clamp(trackX + trackW * frac - handleW / 2f, trackX, handleMax);
            FillRect(handleX, handleY, handleW, handleH, TransportHandle);

            RegisterClickable(hitBand.X, hitBand.Y, hitBand.Width, hitBand.Height, hit);
        }

        /// <summary>
        /// Maps a cursor X onto a fraction in [0, 1] across <paramref name="track"/> (the captured hit-band).
        /// The single drag-math primitive behind the WB / wavelet / scrub Update* handlers.
        /// </summary>
        private static float TrackFrac(RectF32 track, float px)
            => track.Width <= 0f ? 0f : Math.Clamp((px - track.X) / track.Width, 0f, 1f);
    }
}
