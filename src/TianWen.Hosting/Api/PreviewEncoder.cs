using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Color.Icc;
using SharpAstro.Jpeg;
using StbImageWriteSharp;
using TianWen.Lib.Imaging;

namespace TianWen.Hosting.Api
{
    /// <summary>
    /// The single JPEG-preview encoder, shared by the native-v1 per-OTA preview endpoint and the
    /// ninaAPI <c>prepared-image</c> endpoint.
    /// <para>
    /// <b>It stretches through <see cref="StretchSolver"/>, and that is the whole point.</b> The
    /// original nina-shim encoder divided each sample by <see cref="Image.MaxValue"/> and called that an
    /// auto-stretch. A linear astronomical sub is overwhelmingly background at a tiny fraction of full
    /// well, so a plain divide renders it as a near-black frame with a few star cores -- technically an
    /// image, practically useless as a preview. Going through the shared solver applies the real pipeline
    /// (pedestal, background neutralisation, shadow clipping, MTF) that the GPU viewer and the CPU/TUI
    /// renderer already agree on, so a remote preview looks like what the operator sees locally instead
    /// of being its own third rendering of the same frame.
    /// </para>
    /// <para>
    /// <b>Ownership: the image belongs to the session and is only ever read here.</b>
    /// <c>LastCapturedImages</c> pins a recycled camera buffer, so this must not mutate, normalise, or
    /// release it -- see the <see cref="Image"/> mutability notes. That is why the debayer call passes
    /// <c>normalizeToUnit: false</c>; the normalising overload rescales its input <i>in place</i>.
    /// </para>
    /// </summary>
    internal static class PreviewEncoder
    {
        internal const int DefaultQuality = 80;

        /// <summary>
        /// Renders <paramref name="image"/> to a stretched, optionally downscaled JPEG.
        /// </summary>
        /// <param name="quality">JPEG quality, clamped to [1, 100].</param>
        /// <param name="scale">Output scale factor, clamped to (0, 1]; 1 = full sensor resolution.</param>
        internal static async Task<byte[]> EncodeJpegAsync(Image image, int quality, double scale, CancellationToken cancellationToken)
        {
            // A CFA frame previews in colour rather than as a visible Bayer grid. For a mono or already
            // 3-channel image this returns the same instance untouched, so there is no copy to pay for.
            // normalizeToUnit MUST stay false -- see the ownership note on the class.
            var rgb = await image.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: false, cancellationToken);

            // Stat scan + stretch + JPEG entropy coding are all CPU-bound and a full-frame preview of a
            // modern sensor is tens of megapixels, so keep it off the request thread.
            return await Task.Run(() => Encode(rgb, quality, scale), cancellationToken);
        }

        private static byte[] Encode(Image image, int quality, double scale)
        {
            var (channelCount, width, height) = image.Shape;

            var stats = StretchSolver.CollectPerChannelStats(image, channelCount);
            var uniforms = StretchSolver.ComputeStretchUniforms(
                StretchMode.Linked,
                StretchParameters.Default,
                stats,
                lumaStats: null,
                image.MaxValue);

            var pixelCount = width * height;
            var rgbaLength = pixelCount * 4;
            var rgba = ArrayPool<byte>.Shared.Rent(rgbaLength);
            try
            {
                image.RenderStretchedRgba(uniforms, rgba.AsSpan(0, rgbaLength));

                // Anything that is not a genuine downscale factor means "full resolution". Clamping to
                // [0, 1] instead would turn scale=0 (and any negative) into a 1x1 image rather than the
                // full frame -- a query-string default of 0 would silently return a single pixel.
                var validScale = double.IsFinite(scale) && scale > 0.0 && scale < 1.0;
                var outWidth = validScale ? Math.Max(1, (int)(width * scale)) : width;
                var outHeight = validScale ? Math.Max(1, (int)(height * scale)) : height;

                var isColor = channelCount >= 3;
                var components = isColor ? ColorComponents.RedGreenBlue : ColorComponents.Grey;
                var bytesPerPixel = isColor ? 3 : 1;
                var outBytes = new byte[outWidth * outHeight * bytesPerPixel];

                Downsample(rgba, width, height, outBytes, outWidth, outHeight, bytesPerPixel);

                using var ms = new MemoryStream();
                var writer = new ImageWriter();
                writer.WriteJpg(outBytes, outWidth, outHeight, components, ms, Math.Clamp(quality, 1, 100));

                // Tag as sRGB v4 so colour-managed clients (Nina, Touch N Stars, a browser) render the
                // preview with the correct gamma. The injector slips an APP2 segment in after the existing
                // JFIF APP0, leaving the entropy-coded body untouched.
                return JpegIccInjector.EmbedIccProfile(ms.GetBuffer().AsSpan(0, (int)ms.Length), IccProfiles.SRgbV4);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rgba);
            }
        }

        /// <summary>
        /// Box-averages the stretched RGBA raster down to the requested size, dropping the alpha channel
        /// (and collapsing to luminance-free grey by taking the red channel for a mono source, where all
        /// three are equal by construction).
        /// <para>
        /// <b>Averaging rather than nearest-neighbour is deliberate.</b> Point-sampling a star field at,
        /// say, 1:8 simply misses most stars -- a star a few pixels across falls between the sampled
        /// points and vanishes, so the preview under-reports what was actually captured. A box filter
        /// keeps it as a dimmer pixel, which is the honest answer and costs one pass over the source.
        /// </para>
        /// </summary>
        private static void Downsample(
            ReadOnlySpan<byte> rgba, int srcWidth, int srcHeight,
            Span<byte> dst, int outWidth, int outHeight, int bytesPerPixel)
        {
            var xRatio = (double)srcWidth / outWidth;
            var yRatio = (double)srcHeight / outHeight;

            for (var y = 0; y < outHeight; y++)
            {
                var sy0 = (int)(y * yRatio);
                var sy1 = Math.Min(srcHeight, Math.Max(sy0 + 1, (int)((y + 1) * yRatio)));

                for (var x = 0; x < outWidth; x++)
                {
                    var sx0 = (int)(x * xRatio);
                    var sx1 = Math.Min(srcWidth, Math.Max(sx0 + 1, (int)((x + 1) * xRatio)));

                    uint rSum = 0, gSum = 0, bSum = 0;
                    var samples = 0;

                    for (var sy = sy0; sy < sy1; sy++)
                    {
                        var rowOffset = sy * srcWidth * 4;
                        for (var sx = sx0; sx < sx1; sx++)
                        {
                            var o = rowOffset + sx * 4;
                            rSum += rgba[o];
                            gSum += rgba[o + 1];
                            bSum += rgba[o + 2];
                            samples++;
                        }
                    }

                    if (samples == 0)
                    {
                        continue;
                    }

                    var offset = (y * outWidth + x) * bytesPerPixel;
                    if (bytesPerPixel == 3)
                    {
                        dst[offset] = (byte)(rSum / samples);
                        dst[offset + 1] = (byte)(gSum / samples);
                        dst[offset + 2] = (byte)(bSum / samples);
                    }
                    else
                    {
                        dst[offset] = (byte)(rSum / samples);
                    }
                }
            }
        }
    }
}
