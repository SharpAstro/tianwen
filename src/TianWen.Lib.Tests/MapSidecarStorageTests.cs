using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// How a per-pixel map sidecar is STORED, which is a separate question from what it means.
    ///
    /// <para>Every one of these files was float32 because that is what a plane is held in, and
    /// float32 is what made them both large and incompressible: a real 3072x3060x3 coverage map of
    /// 81 frames is 112.8 MB, and gzip alone takes it to 94.7 MB because the low mantissa bits of a
    /// weight are noise. Quantised first it gzips to 1.71 MB. So the rule under test is
    /// quantise-then-compress, and the assertions below are on the SIZE and on what survives the
    /// round trip, since a storage change that loses the values is worse than the bytes it saved.</para>
    /// </summary>
    [Collection("Imaging")]
    public class MapSidecarStorageTests : IDisposable
    {
        private const int W = 320;
        private const int H = 256;
        private const int Frames = 37;

        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "tianwen-map-storage", Guid.NewGuid().ToString("N"));

        public MapSidecarStorageTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // A locked temp file must not fail a green test run.
            }
        }

        /// <summary>A coverage plane's shape: full frame count inside, a ramp down over the dither
        /// band, nothing in the outermost ring. That structure is what compresses.</summary>
        private static Image CoverageCounts()
        {
            var plane = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    var edge = Math.Min(Math.Min(x, W - 1 - x), Math.Min(y, H - 1 - y));
                    plane[y, x] = edge >= 24 ? Frames : MathF.Round(Frames * (edge / 24f));
                }
            }

            return new Image([plane], BitDepth.Float32, maxValue: Frames, minValue: 0f, pedestal: 0f,
                new ImageMeta { Instrument = "TestCam" });
        }

        [Fact]
        public void ACoverageCountRoundTripsEXACTLYAndCostsAFractionOfTheFloat32File()
        {
            var counts = CoverageCounts();
            var masterPath = Path.Combine(_dir, "master.fits");

            // The float32 file this replaces, written through the same writer with no storage asked
            // for, so the comparison is of storage alone and not of two different writers.
            var asFloat = Path.Combine(_dir, "asfloat.fits");
            counts.WriteToFitsFile(asFloat, wcs: null, extraHeaders: null);

            IntegrationFitsWriter.WriteCoverageMap(masterPath, counts, Frames);
            var sidecar = IntegrationFitsWriter.ExistingSidecarPath(IntegrationFitsWriter.CoveragePathFor(masterPath));
            sidecar.ShouldNotBeNull();

            var stored = new FileInfo(sidecar).Length;
            var asFloatSize = new FileInfo(asFloat).Length;
            stored.ShouldBeLessThan(asFloatSize / 10,
                $"quantise-then-compress has to be worth an order of magnitude; was {stored} against {asFloatSize}");

            // And it has to be the SAME MAP. A count is a whole number within a byte, so the storage
            // keeps unit steps and the round trip is exact rather than merely close: no tolerance.
            Image.TryReadFitsFile(sidecar, out var readBack).ShouldBeTrue();
            readBack.Width.ShouldBe(W);
            readBack.Height.ShouldBe(H);
            for (var y = 0; y < H; y += 7)
            {
                for (var x = 0; x < W; x += 5)
                {
                    readBack[0, y, x].ShouldBe(counts[0, y, x]);
                }
            }
        }

        [Fact]
        public void TheStorageRuleKeepsUnitStepsForCountsAndSpreadsTheRangeForWeights()
        {
            // A count: whole numbers inside a byte, so it stays readable as the count it is.
            var counts = IntegrationFitsWriter.MapStorage(CoverageCounts());
            counts.Depth.ShouldBe(BitDepth.Int8);
            counts.BScale.ShouldBe(1.0);
            counts.BZero.ShouldBe(0.0);

            // A drizzle's accumulated weight is fractional, so unit steps would throw away
            // everything between two frames. It goes wider and scales to its own peak instead.
            var weights = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    weights[y, x] = 0.63f * Frames * (x + 1) / W;
                }
            }

            var weightMap = new Image([weights], BitDepth.Float32, maxValue: Frames, minValue: 0f, pedestal: 0f,
                new ImageMeta());
            var storage = IntegrationFitsWriter.MapStorage(weightMap);
            storage.Depth.ShouldBe(BitDepth.Int16);
            // Scaled to the OBSERVED peak, not the declared frame count: the finer step is free.
            storage.BScale.ShouldBe(0.63 * Frames / ushort.MaxValue, tolerance: 1e-6);
        }

        [Fact]
        public void AFractionalMapSurvivesToItsQuantisationStep()
        {
            var fraction = new float[H, W];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    fraction[y, x] = 0.37f * ((x * 31 + y * 17) % 1000) / 1000f;
                }
            }

            var map = new Image([fraction], BitDepth.Float32, maxValue: 0.37f, minValue: 0f, pedestal: 0f,
                new ImageMeta());
            var masterPath = Path.Combine(_dir, "fractions.fits");
            IntegrationFitsWriter.WriteRejectionMap(masterPath, map, Frames, meanRejectionRate: 0.01);

            var sidecar = IntegrationFitsWriter.ExistingSidecarPath(IntegrationFitsWriter.RejectionPathFor(masterPath));
            sidecar.ShouldNotBeNull();
            Image.TryReadFitsFile(sidecar, out var readBack).ShouldBeTrue();

            // One step of a 16-bit container spread over the map's own range, which for a rejection
            // rate of a few percent is four orders of magnitude below the values it carries.
            var step = 0.37f / ushort.MaxValue;
            for (var y = 0; y < H; y += 11)
            {
                for (var x = 0; x < W; x += 13)
                {
                    readBack[0, y, x].ShouldBe(fraction[y, x], tolerance: step);
                }
            }
        }

        /// <summary>
        /// The row-wise narrowing and the scalar conversion are one rule expressed twice, the first
        /// being the second with its invariants lifted out of the loop. If they ever disagree, a
        /// test that pins one is not pinning the other, so this pins them TO EACH OTHER over the
        /// values that separate the cases: integers, halves, the container's ends, past both ends,
        /// and non-finite.
        /// </summary>
        [Fact]
        public void TheRowWiseNarrowingAgreesWithTheScalarConversionSampleForSample()
        {
            float[] samples =
            [
                0f, 1f, 1.5f, 2.5f, -0.4f, 36.999998f, 37f, 37.000004f, 82.0019f,
                254.5f, 255f, 256f, 65534.5f, 65535f, 70000f, -5f,
                float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            ];

            FitsSampleStorage[] storages =
            [
                FitsSampleStorage.Conventional(BitDepth.Int8),
                FitsSampleStorage.Conventional(BitDepth.Int16),
                FitsSampleStorage.Conventional(BitDepth.Int32),
                FitsSampleStorage.Spanning(BitDepth.Int8, 37, valuesAreWholeNumbers: true),
                FitsSampleStorage.Spanning(BitDepth.Int8, 50.7, valuesAreWholeNumbers: false),
                FitsSampleStorage.Spanning(BitDepth.Int16, 0.37, valuesAreWholeNumbers: false),
            ];

            foreach (var storage in storages)
            {
                var narrowed = new int[samples.Length];
                storage.Narrow<int>(samples, narrowed);
                for (var i = 0; i < samples.Length; i++)
                {
                    narrowed[i].ShouldBe((int)storage.ToRaw(samples[i]),
                        $"{storage.Depth} bscale={storage.BScale:G6} round={storage.RoundToNearest} sample={samples[i]}");
                }
            }
        }

        [Fact]
        public void AMaskRoundTripsThroughTheFormatTheArchivesOwnMapsUse()
        {
            var mask = new BitMatrix[1];
            var m = new BitMatrix(H, W);
            // A realistic defect set: isolated photosites plus one small cluster, and one bit in the
            // last word of a row, which is the one a padded row length can lose.
            int[,] flagged = { { 5, 9 }, { 5, 10 }, { 6, 9 }, { 40, 200 }, { 41, 201 }, { 100, W - 1 }, { H - 1, 0 } };
            for (var i = 0; i < flagged.GetLength(0); i++)
            {
                m[flagged[i, 0], flagged[i, 1]] = true;
            }
            mask[0] = m;

            var image = BadPixelMap.ToImage(mask, new ImageMeta { Instrument = "TestCam" });
            image.BitDepth.ShouldBe(BitDepth.Int8);
            image[0, 5, 9].ShouldBe(BadPixelMap.HotLevel);
            image[0, 5, 11].ShouldBe(BadPixelMap.LinearLevel);

            var masterPath = Path.Combine(_dir, "masked.fits");
            IntegrationFitsWriter.WriteBadPixelMap(masterPath, mask, new ImageMeta { Instrument = "TestCam" }, Frames);
            var sidecar = IntegrationFitsWriter.ExistingSidecarPath(IntegrationFitsWriter.BadPixelPathFor(masterPath));
            sidecar.ShouldNotBeNull();

            Image.TryReadFitsFile(sidecar, out var readBack).ShouldBeTrue();
            var recovered = BadPixelMap.FromImage(readBack);
            recovered.Length.ShouldBe(1);
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    recovered[0][y, x].ShouldBe(m[y, x], $"pixel ({x}, {y}) changed state across the round trip");
                }
            }

            // The count is in the header, which is what makes a map legible without decoding it --
            // the all-blue-flagged incident is one card, not a pixel inspection.
            // The whole HDU, not a header-only peek: skipping the data block needs a seek and a
            // gzip stream has none, which is the same constraint the coverage reader works around.
            using var fits = Image.OpenFits(sidecar);
            var header = fits.ReadFirstImageHdu()?.Header;
            header.ShouldNotBeNull();
            header.GetIntValue("NBADPIX").ShouldBe(flagged.GetLength(0));
            header.GetStringValue(IntegrationFitsWriter.MapKindCard).Trim()
                .ShouldBe(IntegrationFitsWriter.BadPixelMapKind);
        }

        [Fact]
        public void AColdPixelInAnArchiveMapIsFlaggedToo()
        {
            // APP writes three levels and we write two, so the READER has to key on "not linear"
            // rather than on "hot". An archive map with cold pixels in it is the case that proves
            // it: 127 is the only level that means the pixel is fine.
            var plane = new float[4, 4];
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    plane[y, x] = BadPixelMap.LinearLevel;
                }
            }

            plane[1, 1] = BadPixelMap.HotLevel;
            plane[2, 3] = BadPixelMap.ColdLevel;

            var map = new Image([plane], BitDepth.Int8, maxValue: 255f, minValue: 0f, pedestal: 0f, new ImageMeta());
            var mask = BadPixelMap.FromImage(map);

            mask[0][1, 1].ShouldBeTrue("a hot pixel is flagged");
            mask[0][2, 3].ShouldBeTrue("and so is a cold one");
            mask[0][0, 0].ShouldBeFalse();
            BadPixelDetection.CountMaskedPixels(mask).ShouldBe(2);
        }
    }
}
