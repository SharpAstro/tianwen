using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Color.Icc;
using SharpAstro.Jpeg;
using SharpAstro.Png;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>The container an annotated raster is written to.</summary>
    /// <remarks>
    /// Eight bits per channel, both of them, and that is a property of the ANNOTATION rather than a
    /// choice: the overlay rasteriser (<see cref="RgbaImageRenderer"/>) composites into an 8-bit
    /// surface, so a 16-bit container would carry eight bits of real data. The clean raster keeps its
    /// 16-bit option -- see <see cref="DisplayRasterExport"/>, which is what "as displayed" saves.
    /// </remarks>
    public enum AnnotatedRasterFormat
    {
        /// <summary>8-bit-per-channel PNG. Lossless against the annotated surface.</summary>
        Png,

        /// <summary>Baseline JPEG, sRGB-tagged. Lossy; for sharing rather than for keeping.</summary>
        Jpeg,
    }

    /// <summary>
    /// Writes the ANNOTATED view: the display raster <see cref="DisplayRasterExport"/> saves, with
    /// everything the viewer draws OVER it -- the WCS grid and its labels, star markers, object
    /// markers and labels, and any caller-supplied sky annotation -- rendered at the IMAGE's own
    /// resolution rather than the window's.
    /// </summary>
    /// <remarks>
    /// <para><b>There is no second drawing path here, and that was the whole question.</b> P22 sat in
    /// the backlog because the obvious implementation -- a CPU annotator beside the GPU one, in the
    /// shape of <c>PlateSolveAnnotator</c> -- is two implementations of one picture, and every overlay
    /// added to the shader would then owe a CPU twin with nothing failing when it was forgotten. What
    /// this does instead is run <see cref="ImageRendererBase{TSurface}"/> itself, the same class the
    /// viewer's window is drawn by, over a CPU surface: the geometry comes from the same layout pass,
    /// the same <c>OverlayEngine.ComputeOverlays</c> and the same label placement, and only the three
    /// drawing PRIMITIVES are backend-specific. An overlay added to the viewer appears here for free.
    /// </para>
    ///
    /// <para><b>The surface IS the image</b>, so screen coordinates and image coordinates coincide:
    /// zoom is 1, pan is zero, and the chrome is off, which leaves the image pane filling the whole
    /// surface. Nothing has to be projected differently from the way the window projects it -- the
    /// window is simply a different size.</para>
    ///
    /// <para><b>Annotation furniture is scaled, the image is not.</b> A 6-pixel marker and a 14-pixel
    /// label are sized for a window; on a 9576-pixel-wide master they would be specks. The export
    /// therefore renders with a <see cref="PixelWidgetBase{TSurface}.DpiScale"/> derived from the
    /// image width, which is the same lever a HiDPI window pulls, so markers, labels and their
    /// placement grow together and the result reads like the screen at higher resolution. The pixels
    /// underneath are untouched and full-resolution.</para>
    /// </remarks>
    public static class AnnotatedRasterExport
    {
        /// <summary>
        /// The window width the overlay furniture is sized for. The export scales its annotation up by
        /// the ratio of the image to this, so a marker covers the same FRACTION of the picture that it
        /// covers on a typical screen.
        /// </summary>
        private const float ReferenceWindowWidth = 1600f;

        /// <summary>The canonical extension for each format.</summary>
        public static string Extension(this AnnotatedRasterFormat format) => format switch
        {
            AnnotatedRasterFormat.Png => ".png",
            AnnotatedRasterFormat.Jpeg => ".jpg",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        /// <summary>
        /// The format a path's extension asks for, defaulting to <see cref="AnnotatedRasterFormat.Png"/>
        /// for anything this writer does not recognise -- the lossless one, and the one the save dialog
        /// offers first.
        /// </summary>
        public static AnnotatedRasterFormat FromExtension(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => AnnotatedRasterFormat.Jpeg,
                _ => AnnotatedRasterFormat.Png,
            };

        /// <summary>
        /// Renders <paramref name="document"/> as the viewer is displaying it, draws the overlays
        /// <paramref name="state"/> has switched on over it, and writes the result to
        /// <paramref name="path"/>.
        /// </summary>
        /// <param name="document">The document on screen. Its stretch is resolved exactly as the viewer resolves it.</param>
        /// <param name="state">The live viewer state. NOT mutated -- an export snapshot is taken from it.</param>
        /// <param name="path">Output path. Its extension is not consulted; <paramref name="format"/> decides.</param>
        /// <param name="format">Container.</param>
        /// <param name="celestialObjectDB">
        /// Catalog for the object overlay, exactly as the renderer takes it. Null renders every other
        /// overlay and simply omits that one, which is what the viewer does before the DB has loaded.
        /// </param>
        /// <param name="annotation">Caller-supplied sky annotation (plate-solve verification, polar
        /// alignment, mosaic panels) -- the same value the on-screen renderer would be holding.</param>
        /// <param name="jpegQuality">JPEG quality, 1-100. Ignored by PNG.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task WriteAsync(
            AstroImageDocument document,
            ViewerState state,
            string path,
            AnnotatedRasterFormat format,
            DotNext.Threading.AsyncLazy<ICelestialObjectDB>? celestialObjectDB = null,
            WcsAnnotation annotation = default,
            int jpegQuality = 92,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentException.ThrowIfNullOrEmpty(path);

            var image = document.UnstretchedImage;
            var isComposite = state.ChannelView.DisplayedSourceChannel(image.ChannelCount) is null;

            // A raw CFA frame in the composite view is the one case where the SCREEN carries colour the
            // image's own channels do not, because the shader debayers from the mosaic. Mirrors
            // DisplayRasterExport, which mirrors what UploadDocumentTextures decides between.
            var pixels = image.ImageMeta.SensorType is SensorType.RGGB && image.Shape.ChannelCount == 1 && isComposite
                ? await image.DebayerAsync(state.DebayerAlgorithm, cancellationToken: cancellationToken).ConfigureAwait(false)
                : image;

            var (_, width, height) = pixels.Shape;

            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var viewer = new AnnotatedViewer(renderer, pixels)
            {
                CelestialObjectDB = celestialObjectDB,
                Annotation = annotation,
            };

            var exportState = state.ForAnnotatedExport();

            // The curve boost pivots on the POST-stretch background, exactly as the shader's
            // curvesMidpoint does; the default 0.25 would move the curve. Resolved here because the
            // rendition the renderer hands the quad carries the dials but not this, which is derived
            // from the document's own statistics.
            viewer.CurvesMidpoint = document
                .ComputeStretchUniforms(
                    exportState.StretchMode, exportState.StretchParameters,
                    bgNeutralizationStrength: exportState.BackgroundNeutralizationStrength,
                    manualWhiteBalance: exportState.ManualWhiteBalance,
                    applyColorCalibration: exportState.ColorCalibrationEnabled)
                .ComputePostStretchBackground(document.PerChannelBackground, document.LumaBackground);

            viewer.Render(document, exportState);

            cancellationToken.ThrowIfCancellationRequested();

            var rgba = renderer.Surface.Pixels;
            byte[] encoded;
            switch (format)
            {
                case AnnotatedRasterFormat.Png:
                    encoded = PngWriter.Encode(rgba, width, height, IccProfiles.SRgbV4.Span);
                    break;

                case AnnotatedRasterFormat.Jpeg:
                {
                    // JPEG has no alpha. Always three channels here, even for a mono view: an overlay
                    // is coloured whatever the image under it is, so packing to grey would discard the
                    // annotation's own colour, which is the thing it uses to say what a marker means.
                    var packed = new byte[width * height * 3];
                    for (var i = 0; i < width * height; i++)
                    {
                        packed[i * 3 + 0] = rgba[i * 4 + 0];
                        packed[i * 3 + 1] = rgba[i * 4 + 1];
                        packed[i * 3 + 2] = rgba[i * 4 + 2];
                    }

                    var jpeg = JpegEncoder.Encode(packed, width, height, 3,
                        new JpegEncodeOptions { Quality = Math.Clamp(jpegQuality, 1, 100) });
                    encoded = JpegIccInjector.EmbedIccProfile(jpeg, IccProfiles.SRgbV4);
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }

            await File.WriteAllBytesAsync(path, encoded, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The viewer, rendered onto a CPU surface the size of the image. Everything but the three
        /// drawing primitives and the image blit is inherited, which is the point -- see the remarks on
        /// <see cref="AnnotatedRasterExport"/>.
        /// </summary>
        private sealed class AnnotatedViewer : ImageRendererBase<RgbaImage>
        {
            private readonly RgbaImageRenderer _renderer;
            private readonly Image _pixels;
            private readonly float _scale;

            public AnnotatedViewer(RgbaImageRenderer renderer, Image pixels) : base(renderer)
            {
                _renderer = renderer;
                _pixels = pixels;
                Width = renderer.Width;
                Height = renderer.Height;

                // The one lever that scales annotation furniture, taken from the image rather than a
                // display: a HiDPI window scales its overlay exactly this way, so nothing here is a
                // special case for export. Never below 1 -- a thumbnail-sized image gets the same
                // annotation a window would draw, not a shrunken one.
                _scale = MathF.Max(1f, renderer.Width / ReferenceWindowWidth);
                DpiScale = _scale;

                // Bundled first, and all three roles together, because only a bundled face has known
                // coverage -- BundledFonts.Resolve is the single decision for every host (a direct
                // FontResolver call in production code is a regression).
                var fonts = BundledFonts.Resolve();
                FontPath = fonts.Text ?? string.Empty;
                EmojiFontPath = fonts.Emoji;

                // The base skips the image entirely while it believes there is none, and what tells it
                // otherwise is the texture upload -- the one thing this backend has no use for. So the
                // geometry is declared with an EMPTY upload: the abstract handler ignores it, the
                // dimensions land, and the layout pass has an image to place. Without this the export
                // renders a blank surface, overlays and all, which is exactly what it did first.
                UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, (int)renderer.Width, (int)renderer.Height);
            }

            /// <summary>
            /// The post-stretch background the curve boost pivots on, resolved by the caller from the
            /// document's statistics. <see cref="DisplayRendition"/> carries the dials but not this.
            /// </summary>
            public float CurvesMidpoint { get; set; } = 0.25f;

            /// <summary>
            /// The image itself, rendered through the rendition the base resolved -- so the pixels
            /// under the annotation are the same pixels "save as displayed" writes, by construction
            /// rather than by two call sites agreeing.
            /// </summary>
            protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
                in DisplayRendition rendition, WCS? wcs,
                float left, float top, float right, float bottom, uint projW, uint projH,
                RenditionSlot slot, bool sampleBeforeChannels)
            {
                // The surface IS the image at 1:1, so the quad covers all of it and the render goes
                // straight into the surface with no resampling step. Anything else means the export
                // state was not the one this class builds.
                var surface = _renderer.Surface.Pixels;
                _pixels.RenderStretchedRgba(rendition.Stretch, surface,
                    rendition.CurvesBoost, rendition.CurvesMode, rendition.CurveSpan,
                    CurvesMidpoint, rendition.HdrAmount, rendition.HdrKnee);

                // A single-channel view shows ONE channel as grey, and the render above already applied
                // that channel's own curve, so the value is sitting in its own slot and only has to be
                // replicated. Re-rendering a one-channel image instead would lose exactly that.
                if (state.ChannelView.DisplayedSourceChannel(_pixels.ChannelCount) is not { } channel
                    || _pixels.ChannelCount < 3)
                {
                    return;
                }

                for (var i = 0; i < surface.Length; i += 4)
                {
                    var v = surface[i + channel];
                    surface[i + 0] = v;
                    surface[i + 1] = v;
                    surface[i + 2] = v;
                    surface[i + 3] = byte.MaxValue;
                }
            }

            /// <summary>No histogram in an export: it is chrome, and the chrome is off.</summary>
            protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
                ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

            /// <summary>
            /// An ellipse at any angle. The rasteriser offers only an axis-aligned one, so a rotated
            /// ellipse is walked as a closed polyline -- which is also what the GPU seam does with a
            /// curve, and is exact rather than an approximation of the shape (only of its smoothness).
            /// </summary>
            protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
                float angleRad, RGBAColor32 color, float thickness)
            {
                var stroke = MathF.Max(1f, thickness * _scale);

                if (MathF.Abs(angleRad) < 1e-3f)
                {
                    var rect = new RectInt(
                        new PointInt((int)MathF.Round(cx - semiMajor), (int)MathF.Round(cy - semiMinor)),
                        new PointInt((int)MathF.Round(cx + semiMajor), (int)MathF.Round(cy + semiMinor)));
                    _renderer.DrawEllipse(rect, color, stroke);
                    return;
                }

                // Segment count from the size, so a big marker does not read as a polygon and a small
                // one does not cost 64 line draws.
                var segments = Math.Clamp((int)(MathF.Max(semiMajor, semiMinor) * 0.7f), 16, 64);
                var cos = MathF.Cos(angleRad);
                var sin = MathF.Sin(angleRad);
                var prevX = 0f;
                var prevY = 0f;
                for (var i = 0; i <= segments; i++)
                {
                    var t = i / (float)segments * MathF.Tau;
                    var ex = semiMajor * MathF.Cos(t);
                    var ey = semiMinor * MathF.Sin(t);
                    var x = cx + ex * cos - ey * sin;
                    var y = cy + ex * sin + ey * cos;
                    if (i > 0)
                    {
                        _renderer.DrawLine(prevX, prevY, x, y, color, (int)MathF.Round(stroke));
                    }
                    prevX = x;
                    prevY = y;
                }
            }

            protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color)
            {
                var stroke = Math.Max(1, (int)MathF.Round(_scale));
                _renderer.DrawLine(cx - armLength, cy, cx + armLength, cy, color, stroke);
                _renderer.DrawLine(cx, cy - armLength, cx, cy + armLength, color, stroke);
            }

            protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
                RGBAColor32 color, float thickness) =>
                _renderer.DrawLine(x0, y0, x1, y1, color, Math.Max(1, (int)MathF.Round(thickness * _scale)));

            protected override void OnResize(uint width, uint height) { }

            /// <summary>
            /// No GPU textures exist here: the image is rendered by <see cref="RenderImageQuad"/>
            /// straight from the pixels. The export state carries
            /// <see cref="ViewerState.NeedsTextureUpdate"/> false, so this is never reached.
            /// </summary>
            public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
                int imageWidth, int imageHeight) { }

            public override void UploadHistogramData(IPreviewSource source) { }

            protected override HistogramDisplay? GetHistogramDisplay() => null;
        }
    }
}
