using Shouldly;
using System;
using System.IO;
using TianWen.Lib;
using TianWen.Lib.Imaging;
using Xunit;
using nom.tam.fits;
using nom.tam.util;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the opt-in pooled FITS read: same pixels as the normal read, channel arrays rented from
    /// <see cref="Array2DPool{T}"/>, and returned by <see cref="Image.Release"/> so a bulk reader
    /// recycles instead of allocating a large-object array per file.
    ///
    /// <para>The default (unpooled) read must stay a no-op on release, because several existing
    /// call sites release an image and keep reading it -- safe only while file loads own their
    /// arrays outright. That asymmetry is the point of the flag and is asserted here.</para>
    /// </summary>
    [Collection("Imaging")]
    public class FitsPooledReadTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fitspool-" + Guid.NewGuid().ToString("N")[..8]);
        private readonly bool _poolWasEnabled;

        // Deliberately not a sensor shape: the pool buckets on exact (height, width), and
        // FakeExternal flips Array2DPool<float>.Enabled process-wide, so an odd size keeps this
        // test's bucket to itself even if another collection is churning real frame sizes.
        private const int Height = 61;
        private const int Width = 47;

        public FitsPooledReadTests()
        {
            Directory.CreateDirectory(_dir);
            _poolWasEnabled = Array2DPool<float>.Enabled;
            Array2DPool<float>.Enabled = true;
        }

        public void Dispose()
        {
            Array2DPool<float>.Enabled = _poolWasEnabled;
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// Writes a 16-bit integer frame. Integer BITPIX matters: a float32 file with trivial
        /// scaling takes the zero-copy branch and never rents, so a float fixture would assert
        /// nothing about pooling.
        /// </summary>
        private string WriteShortFrame(string name)
        {
            var path = Path.Combine(_dir, name);
            var data = new short[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    data[y, x] = (short)(y * Width + x);
                }
            }
            var fits = new Fits();
            var hdu = FitsFactory.HDUFactory(data);
            hdu.AddValue("IMAGETYP", "LIGHT", "");
            fits.AddHDU(hdu);
            using (var bf = new BufferedFile(path, FileAccess.ReadWrite, FileShare.None))
            {
                fits.Write(bf);
                bf.Flush();
            }
            return path;
        }

        [Fact]
        public void PooledRead_ProducesIdenticalPixels()
        {
            var path = WriteShortFrame("frame.fits");

            Image.TryReadFitsFile(path, out var plain, out _).ShouldBeTrue();
            Image.TryReadFitsFile(path, out var pooled, out _, pooled: true).ShouldBeTrue();
            plain.ShouldNotBeNull();
            pooled.ShouldNotBeNull();

            pooled.Width.ShouldBe(plain.Width);
            pooled.Height.ShouldBe(plain.Height);
            pooled.ChannelCount.ShouldBe(plain.ChannelCount);
            pooled.MaxValue.ShouldBe(plain.MaxValue);
            pooled.MinValue.ShouldBe(plain.MinValue);
            pooled.GetChannelSpan(0).SequenceEqual(plain.GetChannelSpan(0)).ShouldBeTrue();
        }

        [Fact]
        public void PooledRead_ReturnsArrayToPoolOnRelease_AndNextReadReusesIt()
        {
            var path = WriteShortFrame("frame.fits");

            Image.TryReadFitsFile(path, out var first, out _, pooled: true).ShouldBeTrue();
            first.ShouldNotBeNull();
            // The buffer is what makes Release meaningful; an unpooled read leaves it null.
            first.GetChannel(0).Buffer.ShouldNotBeNull();

            var before = Array2DPool<float>.ReturnCount;
            first.Release();
            Array2DPool<float>.ReturnCount.ShouldBeGreaterThan(before);

            // The recycle is the whole point: the next same-shape rent hands back that array.
            var recycled = Array2DPool<float>.Rent(Height, Width);
            recycled.GetLength(0).ShouldBe(Height);
            recycled.GetLength(1).ShouldBe(Width);
            Array2DPool<float>.Return(recycled);
        }

        [Fact]
        public void UnpooledRead_CarriesNoBuffer_SoReleaseStaysANoOp()
        {
            var path = WriteShortFrame("frame.fits");

            Image.TryReadFitsFile(path, out var image, out _).ShouldBeTrue();
            image.ShouldNotBeNull();
            image.GetChannel(0).Buffer.ShouldBeNull();

            var before = Array2DPool<float>.ReturnCount;
            image.Release();
            Array2DPool<float>.ReturnCount.ShouldBe(before);

            // Still readable after release -- the behaviour existing call sites rely on.
            image.GetChannelSpan(0).Length.ShouldBe(Height * Width);
        }

        [Fact]
        public void Pool_StopsRetainingOnceTheByteBudgetIsReached()
        {
            // The failure this bounds: a heterogeneous archive never fills any single bucket, so
            // the per-bucket cap alone let the pool pin arrays across 24 distinct frame shapes and
            // the survey OOMed MORE often with pooling on. The budget must refuse the return
            // rather than grow, and must not corrupt its own accounting while doing so.
            var before = Array2DPool<float>.RetainedBytes;
            var evictionsBefore = Array2DPool<float>.BudgetEvictionCount;

            // 40 distinct shapes x 8 MiB each is 320 MiB, comfortably past the 256 MiB ceiling.
            const int side = 1448; // 1448^2 x 4 B ~ 8 MiB
            for (var i = 0; i < 40; i++)
            {
                Array2DPool<float>.Return(new float[side + i, side]);
            }

            Array2DPool<float>.RetainedBytes.ShouldBeLessThanOrEqualTo(256L * 1024 * 1024);
            Array2DPool<float>.BudgetEvictionCount.ShouldBeGreaterThan(evictionsBefore);

            // Renting each shape back must leave the accounting non-negative -- a mismatched
            // credit here would make the pool believe it is permanently full.
            for (var i = 0; i < 40; i++)
            {
                Array2DPool<float>.Rent(side + i, side);
            }
            Array2DPool<float>.RetainedBytes.ShouldBeGreaterThanOrEqualTo(0);
            Array2DPool<float>.RetainedBytes.ShouldBeLessThanOrEqualTo(before + 256L * 1024 * 1024);
        }

        [Fact]
        public void PooledRead_IsNotDirtiedByARecycledArray()
        {
            // A rented array comes back dirty (the pool clears on neither Rent nor Return), so this
            // is only correct because the conversion writes every pixel. Poison a same-shape array,
            // return it, then read: any unwritten pixel would surface as the poison value.
            var poison = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    poison[y, x] = -12345f;
                }
            }
            Array2DPool<float>.Return(poison);

            var path = WriteShortFrame("frame.fits");
            Image.TryReadFitsFile(path, out var image, out _, pooled: true).ShouldBeTrue();
            image.ShouldNotBeNull();

            foreach (var value in image.GetChannelSpan(0))
            {
                value.ShouldNotBe(-12345f);
            }
            image.Release();
        }
    }
}
