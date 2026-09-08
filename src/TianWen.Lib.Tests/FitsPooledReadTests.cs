using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
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

            const long Budget = 256L * 1024 * 1024;

            // Accumulation across distinct shapes: 40 x ~8 MiB, none of which fills its own bucket.
            // The assertion inside the loop is the CEILING, which is trim-safe by construction: the
            // Gen2 trim only ever lowers the retained total, so it can make this pass sooner but
            // never fail it.
            const int side = 1448; // 1448^2 x 4 B ~ 8 MiB
            for (var i = 0; i < 40; i++)
            {
                Array2DPool<float>.Return(new float[side + i, side]);
                Array2DPool<float>.RetainedBytes.ShouldBeLessThanOrEqualTo(Budget,
                    "the budget must refuse a return rather than let the pool grow past its ceiling");
            }

            // The REFUSAL itself, proved in one return rather than by accumulating to the ceiling and
            // hoping it is still there. One array larger than the whole budget is over it from any
            // starting state, including an empty pool, so this cannot race the trim.
            //
            // It is what the loop above used to assert, and could only assert while the pool survived
            // 320 MiB of allocation: measured on a box at 88% memory load, that loop itself takes the
            // machine to 95%, where the trim drops every pooled array and the ceiling is never
            // reached. The eviction count then never moves and the test fails having exercised the
            // right code with the wrong preconditions.
            Array2DPool<float>.Return(new float[8192, 8256]); // 258 MiB, past the 256 MiB budget alone
            Array2DPool<float>.BudgetEvictionCount.ShouldBeGreaterThan(evictionsBefore,
                "a single array bigger than the whole budget must be refused whatever else is pooled");

            // Renting each shape back must leave the accounting non-negative -- a mismatched
            // credit here would make the pool believe it is permanently full.
            for (var i = 0; i < 40; i++)
            {
                Array2DPool<float>.Rent(side + i, side);
            }
            Array2DPool<float>.RetainedBytes.ShouldBeGreaterThanOrEqualTo(0);
            Array2DPool<float>.RetainedBytes.ShouldBeLessThanOrEqualTo(before + 256L * 1024 * 1024);
        }

        /// <summary>
        /// P3 of <c>docs/plans/frame-lifecycle.md</c>: master building is the first bulk reader
        /// switched to pooled, and this is what says the arrays actually come back rather than the
        /// stage merely compiling.
        /// </summary>
        /// <remarks>
        /// Asserted on the pool's RETURN accounting, not on the master's pixels alone -- a build that
        /// silently stopped pooling would still produce a correct master, which is exactly the kind
        /// of regression that goes unnoticed for a release. The pixel check is here too, because a
        /// rented array arrives dirty and a combine that failed to write every pixel would surface as
        /// the previous frame's data rather than as an error.
        /// </remarks>
        [Fact]
        public async Task MasterBuild_RentsEveryFrameAndHandsThemAllBack()
        {
            const int Frames = 3;
            var infos = new List<FrameInfo>(Frames);
            for (var i = 0; i < Frames; i++)
            {
                var path = WriteShortFrame($"bias_{i}.fits");
                Image.TryReadFitsFile(path, out var probe, out _).ShouldBeTrue();
                probe.ShouldNotBeNull();
                infos.Add(new FrameInfo(path, probe.Width, probe.Height, probe.ChannelCount, probe.BitDepth, probe.ImageMeta));
                probe.Release();
            }

            var returnsBefore = Array2DPool<float>.ReturnCount;

            // Rents, counted as hits + misses, because whether a rent HITS is not this build's to
            // decide. A median needs every frame resident at once, so CombinePooledAsync loads them
            // all and releases them only in its finally: no rent here can reuse an earlier frame of
            // this same build, and a hit could only come from what some earlier test happened to
            // leave in the pool. Asserting one made this depend on test order and on the pool
            // surviving the Gen2 trim, which drops everything above 90% memory load by design.
            var rentsBefore = Array2DPool<float>.HitCount + Array2DPool<float>.MissCount;

            var master = await MasterFrameBuilder.BuildBiasMasterAsync(infos, TestContext.Current.CancellationToken);

            master.Width.ShouldBe(Width);
            master.Height.ShouldBe(Height);
            // Every frame carries the same ramp, so the median is that ramp exactly.
            master[0, 0, 0].ShouldBe(0f);
            master[0, Height - 1, Width - 1].ShouldBe((Height - 1) * Width + Width - 1);

            // At LEAST, not exactly. The fixture's odd frame shape keeps this test's BUCKET to
            // itself, but HitCount and ReturnCount are process-wide, and Image.Debayer,
            // Session.Focus and Session.IO all rent from the same pool in collections that run in
            // parallel with this one. An exact delta here is a race, and it flaked as one before
            // being written down.
            Array2DPool<float>.ReturnCount.ShouldBeGreaterThanOrEqualTo(returnsBefore + Frames,
                "each loaded frame is released once the combine has read it");
            (Array2DPool<float>.HitCount + Array2DPool<float>.MissCount)
                .ShouldBeGreaterThanOrEqualTo(rentsBefore + Frames,
                    "every frame is read THROUGH the pool, which is what pooling being reverted would undo");

            // The master itself is NOT pooled: it outlives the build and is the thing the caller keeps.
            master.GetChannel(0).Buffer.ShouldBeNull();
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
