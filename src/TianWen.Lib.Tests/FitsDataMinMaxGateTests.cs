using System;
using System.IO;
using nom.tam.fits;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// A frame TianWen writes states DATAMIN and DATAMAX, so reading it back skips a full-frame
    /// min/max traversal.
    /// </summary>
    /// <remarks>
    /// <para>This became load-bearing when the reader's conversion loop stopped tracking min/max
    /// inline: the values now come only from the vectorised pass that <c>NeedsMinMaxRecalc</c> gates,
    /// so whether our own files state the cards decides whether that pass runs at all. Measured on
    /// three ASI533 subs written by N.I.N.A.: neither card present, so a third-party frame pays the
    /// traversal. Our own should not, and nothing asserted that.</para>
    /// <para>Every captured light, flat, master and plate-solve input reaches
    /// <c>Image.WriteToFitsFile</c> through <c>IExternal.WriteFitsFileAsync</c>, so pinning the writer
    /// pins the capture path.</para>
    /// </remarks>
    public class FitsDataMinMaxGateTests
    {
        private const int Width = 24;
        private const int Height = 16;

        /// <summary>
        /// The cards are read back off the file with FITS.Lib rather than inferred from the decoded
        /// image: a reader that recalculated would report the same min and max, so equal values prove
        /// nothing about whether the header stated them.
        /// </summary>
        [Fact]
        public void AFrameWeWriteStatesBothCardsAndSkipsTheRecalc()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tw-minmax-{Guid.NewGuid():N}.fits");
            try
            {
                CaptureShapedImage().WriteToFitsFile(path);

                using var fits = new Fits(path);
                var hdu = fits.ReadFirstImageHdu();
                hdu.ShouldNotBeNull();

                var min = (float)hdu.MinimumValue;
                var max = (float)hdu.MaximumValue;

                float.IsNaN(min).ShouldBeFalse("DATAMIN must be present, or min stays NaN and the gate trips");
                float.IsNaN(max).ShouldBeFalse("DATAMAX must be present");
                Image.NeedsMinMaxRecalc(min, max).ShouldBeFalse(
                    $"our own frame must take the fast path (DATAMIN={min}, DATAMAX={max})");
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        /// <summary>
        /// A minimum of exactly zero is the common case for a calibrated frame, and it must still be
        /// written. The writer skips only NaN, but a "has value" helper that treated 0 as absent would
        /// silently drop DATAMIN and re-enable the traversal for every such frame.
        /// </summary>
        [Fact]
        public void AZeroMinimumIsStillStated()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tw-minmax0-{Guid.NewGuid():N}.fits");
            try
            {
                CaptureShapedImage(minSample: 0f).WriteToFitsFile(path);

                using var fits = new Fits(path);
                var hdu = fits.ReadFirstImageHdu();
                hdu.ShouldNotBeNull();

                var min = (float)hdu.MinimumValue;
                float.IsNaN(min).ShouldBeFalse("a zero DATAMIN must be written, not treated as absent");
                min.ShouldBe(0f);
                Image.NeedsMinMaxRecalc(min, (float)hdu.MaximumValue).ShouldBeFalse();
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        /// <summary>
        /// The gate itself. Both halves are required, which is why writing DATAMAX alone would buy
        /// nothing -- the case the reader would otherwise appear to handle.
        /// </summary>
        [Theory]
        [InlineData(0f, 65535f, false)]              // a stated, sane pair: fast path
        [InlineData(12f, 4095f, false)]
        [InlineData(float.NaN, 65535f, true)]        // DATAMAX only
        [InlineData(0f, float.NaN, true)]            // DATAMIN only
        [InlineData(float.NaN, float.NaN, true)]     // neither card
        [InlineData(-5f, 65535f, true)]              // negative minimum
        [InlineData(0f, 0f, true)]                   // degenerate
        [InlineData(900f, 100f, true)]               // inverted
        public void TheGateRequiresBothHalvesToBeStatedAndSane(float min, float max, bool expectRecalc)
        {
            Image.NeedsMinMaxRecalc(min, max).ShouldBe(expectRecalc);
        }

        // Int16 with a BZERO-style offset range, the shape a camera frame arrives in.
        private static Image CaptureShapedImage(float minSample = 100f)
        {
            var plane = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    plane[y, x] = minSample + (y * Width + x);
                }
            }

            var max = minSample + (Height * Width - 1);
            return new Image([plane], BitDepth.Int16, max, minSample, 0f,
                new ImageMeta("synth", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60),
                    FrameType.Light, "", 0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN,
                    SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN));
        }
    }
}
