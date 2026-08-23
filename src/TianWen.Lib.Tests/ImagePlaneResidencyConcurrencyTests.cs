using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Residency (D1 of <c>docs/plans/viewer-memory-footprint.md</c>) must be safe to observe from more
    /// than one thread, because <see cref="Image"/> is public surface in a published package and is
    /// documented as immutable.
    /// </summary>
    /// <remarks>
    /// <para>The hazard is not the flag's visibility, which is what a <c>volatile</c> would address. It
    /// is that restoring is a WRITE performed from a read accessor: build the replacement one channel at
    /// a time into the shared field and a second reader sees a half-restored array, some channels real
    /// and some 0x0 stubs, and throws on the stub. No amount of annotating a boolean prevents that.</para>
    /// <para>So these tests read through the accessors a CONSUMER has -- the indexer,
    /// <c>GetChannelSpan</c>, <c>GetChannel</c> -- rather than the private snapshot, and they assert
    /// VALUES rather than merely absence of an exception: a torn read can also present as a plausible
    /// zero, which is the failure the loud-throw design exists to avoid and which an
    /// exception-only assertion would sail straight past.</para>
    /// <para>Multi-channel on purpose. With one channel a partially-restored array is nearly
    /// unobservable; the tear needs a second channel to land in a different generation from the first.</para>
    /// </remarks>
    public class ImagePlaneResidencyConcurrencyTests
    {
        private const int W = 16;
        private const int H = 12;
        private const int Channels = 3;

        [Fact]
        public void ConcurrentReadersOfAnEvictedImageAllSeeTheRealValues()
        {
            var image = WithRaster();
            image.TryEvictFloatPlanes().ShouldBeTrue();
            image.PlanesResident.ShouldBeFalse();

            var failures = Hammer(image, readers: 8, iterations: 200);

            failures.ShouldBeEmpty();
            image.PlanesResident.ShouldBeTrue("a read restores them");
        }

        [Fact]
        public async Task AnEvictionRacingReadersNeverHandsBackAnEmptyPlane()
        {
            // The writer keeps flipping residency underneath the readers, so every reader is repeatedly
            // meeting an image mid-transition -- which is the interleaving a single-threaded test can
            // never produce.
            var testToken = TestContext.Current.CancellationToken;
            var image = WithRaster();

            // Linked, so a cancelled run stops the hammer immediately instead of holding the runner for
            // the full window: the two-second budget is the test's own bound, not a floor.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(testToken);
            stop.CancelAfter(TimeSpan.FromSeconds(2));
            var failures = new ConcurrentQueue<string>();

            var writer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    image.TryEvictFloatPlanes();
                    // Reading restores, so the pair loops without needing a restore API of its own.
                    _ = image[0, 0, 0];
                }
            }, testToken);

            var readers = new Task[6];
            for (var r = 0; r < readers.Length; r++)
            {
                readers[r] = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        foreach (var f in ReadOnce(image))
                        {
                            failures.Enqueue(f);
                        }
                    }
                }, testToken);
            }

            await Task.WhenAll([writer, .. readers]);
            failures.ShouldBeEmpty();
        }

        [Fact]
        public void EveryPublicReadPathHonoursResidency()
        {
            // The three that used to bypass the accessor and read the evicted stub directly:
            // GetChannelArray (FITS write, guider tracker), the subpixel sampler, and the in-place
            // unit rescale -- which indexed plane[0, 0] on a 0x0 array and threw.
            var image = WithRaster();
            image.TryEvictFloatPlanes().ShouldBeTrue();

            image.GetChannelArray(0).Length.ShouldBe(W * H, "GetChannelArray must not hand back the stub");

            var evicted = WithRaster();
            evicted.TryEvictFloatPlanes().ShouldBeTrue();
            var scaled = evicted.ScaleFloatValuesToUnitInPlace();
            scaled.Width.ShouldBe(W);
            scaled[0, 0, 0].ShouldBe(Expected(0, 0, 0), 1e-6f);
        }

        private static ConcurrentQueue<string> Hammer(Image image, int readers, int iterations)
        {
            var failures = new ConcurrentQueue<string>();
            var start = new ManualResetEventSlim(false);
            var tasks = new Task[readers];
            for (var r = 0; r < readers; r++)
            {
                tasks[r] = Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < iterations; i++)
                    {
                        foreach (var f in ReadOnce(image))
                        {
                            failures.Enqueue(f);
                        }
                    }
                });
            }

            start.Set();
            Task.WaitAll(tasks);
            return failures;
        }

        // Reads all three channels through three different accessors, so a tear that only one of them
        // would notice still lands somewhere.
        private static string[] ReadOnce(Image image)
        {
            try
            {
                for (var c = 0; c < Channels; c++)
                {
                    var span = image.GetChannelSpan(c);
                    if (span.Length != W * H)
                    {
                        return [$"GetChannelSpan({c}) length {span.Length}"];
                    }

                    var mid = image[c, H / 2, W / 2];
                    var want = Expected(c, H / 2, W / 2);
                    if (MathF.Abs(mid - want) > 1e-6f)
                    {
                        return [$"indexer[{c}] read {mid}, expected {want}"];
                    }

                    if (image.GetChannel(c).Data.Length != W * H)
                    {
                        return [$"GetChannel({c}).Data was the stub"];
                    }
                }

                return [];
            }
            catch (Exception ex)
            {
                return [$"{ex.GetType().Name}: {ex.Message}"];
            }
        }

        private static float Expected(int channel, int y, int x) => (byte)((y * W + x) * 7 + 3 + channel * 11) / 255f;

        private static Image WithRaster()
        {
            var channels = new Channel[Channels];
            var rasters = new byte[Channels][];
            for (var c = 0; c < Channels; c++)
            {
                var raster = new byte[W * H];
                var plane = new float[H, W];
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        var b = (byte)((y * W + x) * 7 + 3 + c * 11);
                        raster[y * W + x] = b;
                        plane[y, x] = b / 255f;
                    }
                }

                rasters[c] = raster;
                channels[c] = new Channel(plane, Filter.None, 0f, 1f, (byte)c);
            }

            return new Image([.. channels], BitDepth.Int8, 0f, Meta(),
                samplesAreUnitReferred: true, sourceRaster: [.. rasters]);
        }

        private static ImageMeta Meta()
            => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN);
    }
}
