using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pins the DEBUG-only <see cref="ChannelBufferLeakTracker"/> (P2 of
    /// <c>docs/plans/frame-lifecycle.md</c>): a buffer that is released leaves the table, one that is
    /// not stays in it attributed to the code that produced it, and one that is dropped unreleased is
    /// reported once the collector has taken it.
    /// </summary>
    /// <remarks>
    /// <para><b>Every test states its premise first.</b> Tracking is compiled out of Release, so
    /// without the <see cref="Assert.SkipUnless"/> these would assert zero against zero and pass
    /// green forever with the feature deleted.</para>
    /// <para><b>The <c>Category=DebugOnly</c> trait is what gets them actually RUN.</b> The main CI
    /// leg builds Release, where the skip above fires and this suite is no cover at all; a second
    /// Debug leg in <c>dotnet.yml</c> selects on that trait. Any other suite that needs a DEBUG build
    /// joins by carrying the same trait -- the leg names no class, so nothing there has to be
    /// remembered.</para>
    /// <para><b>Assertions are per PRODUCING SITE, never on the global count.</b> Other collections
    /// run in parallel and create camera frames of their own, so a total is not this test's to
    /// predict -- but a site is: nothing else in the tree constructs a buffer at these lines.</para>
    /// <para>In the <see cref="Collection"/> that serialises against <c>FitsPooledReadTests</c>, which
    /// is the only other suite that produces buffers at <c>WrapPooledPlanes</c>.</para>
    /// </remarks>
    [Collection("Imaging")]
    [Trait("Category", "DebugOnly")]
    public class ChannelBufferLeakTrackerTests
    {
        /// <summary>16-bit integer BITPIX, so the read converts into a RENTED array and wraps it.
        /// A float32 frame with trivial scaling takes the zero-copy branch, never rents, and would
        /// assert nothing.</summary>
        private const string IntegerFrame = "image_file-snr-20_stars-28_1280x960x16";

        /// <summary>The member in <c>Image.Fits.cs</c> that constructs a pooled read's buffer.</summary>
        private const string PooledReadSite = "WrapPooledPlanes";

        [Fact]
        public async Task AReleasedPooledFrameLeavesNothingOutstandingAtItsProducer()
        {
            Assert.SkipUnless(ChannelBufferLeakTracker.IsActive, "Buffer tracking is compiled out of Release.");
            var ct = TestContext.Current.CancellationToken;
            var path = await SharedTestData.ExtractGZippedFitsFileAsync(IntegerFrame, ct);

            var before = OutstandingAt(PooledReadSite);
            Image.TryReadFitsFile(path, out var image, out _, pooled: true).ShouldBeTrue();
            image.ShouldNotBeNull();
            image.GetChannel(0).Buffer.ShouldNotBeNull("a pooled read must carry the buffer this tracks");

            OutstandingAt(PooledReadSite).ShouldBe(before + image.ChannelCount);

            image.Release();
            OutstandingAt(PooledReadSite).ShouldBe(before, "release is what clears the table");
        }

        [Fact]
        public async Task AnUnreleasedPooledFrameStaysOutstandingAndNamesItsProducer()
        {
            Assert.SkipUnless(ChannelBufferLeakTracker.IsActive, "Buffer tracking is compiled out of Release.");
            var ct = TestContext.Current.CancellationToken;
            var path = await SharedTestData.ExtractGZippedFitsFileAsync(IntegerFrame, ct);

            // The shape of the bug this exists for: both tile-pipelined stacking strategies read a
            // frame and never released it. Free while file loads owned their arrays, a real leak the
            // moment the read was pooled, and invisible either way. Read at a point where nothing
            // should still be held, an outstanding count IS the diagnosis -- no GC required.
            Image.TryReadFitsFile(path, out var image, out _, pooled: true).ShouldBeTrue();
            image.ShouldNotBeNull();

            var report = ChannelBufferLeakTracker.Report();
            var site = FindSite(report.Live, PooledReadSite);
            site.ShouldNotBe(default, report.Describe());
            site.Count.ShouldBeGreaterThanOrEqualTo(image.ChannelCount);
            site.Bytes.ShouldBeGreaterThanOrEqualTo((long)image.Width * image.Height * sizeof(float));
            site.Producer.ShouldStartWith(PooledReadSite, Case.Sensitive);
            site.Producer.ShouldContain(":", customMessage: "the line number is half the attribution");

            image.Release();
        }

        [Fact]
        public void AFrameDroppedWithoutReleaseIsReportedAsCollectedWhileOutstanding()
        {
            Assert.SkipUnless(ChannelBufferLeakTracker.IsActive, "Buffer tracking is compiled out of Release.");

            // Clear anything already collectable so the delta below is this test's own.
            var before = ChannelBufferLeakTracker.Report(collectFirst: true);
            var leakedBefore = CountAt(before.Leaked, nameof(DropABufferWithoutReleasingIt));

            DropABufferWithoutReleasingIt();

            var after = ChannelBufferLeakTracker.Report(collectFirst: true);
            CountAt(after.Leaked, nameof(DropABufferWithoutReleasingIt))
                .ShouldBe(leakedBefore + 1, after.Describe());
            after.LeakCount.ShouldBeGreaterThan(before.LeakCount);

            // Billed once: the entry is removed as it is counted, so a second sweep must not
            // re-report the same buffer and turn one bug into a rising number.
            var again = ChannelBufferLeakTracker.Report(collectFirst: true);
            CountAt(again.Leaked, nameof(DropABufferWithoutReleasingIt)).ShouldBe(leakedBefore + 1);
            again.LeakCount.ShouldBe(after.LeakCount);
        }

        [Fact]
        public void ReleaseClearsTheEntryWhileTheBufferItselfIsStillAlive()
        {
            Assert.SkipUnless(ChannelBufferLeakTracker.IsActive, "Buffer tracking is compiled out of Release.");

            var before = OutstandingAt(nameof(ReleaseClearsTheEntryWhileTheBufferItselfIsStillAlive));
            var buffer = new ChannelBuffer(new float[8, 8]);
            OutstandingAt(nameof(ReleaseClearsTheEntryWhileTheBufferItselfIsStillAlive)).ShouldBe(before + 1);

            buffer.Release();
            buffer.Release();

            // Object liveness is not the fact being tracked: OWNERSHIP is. The buffer is still very
            // much alive here and must be gone from the table anyway, and the second release must not
            // disturb an entry that is already gone.
            OutstandingAt(nameof(ReleaseClearsTheEntryWhileTheBufferItselfIsStillAlive)).ShouldBe(before);
            GC.KeepAlive(buffer);
        }

        [Fact]
        public void AnExtraReferenceKeepsTheBufferOutstandingUntilTheLastRelease()
        {
            Assert.SkipUnless(ChannelBufferLeakTracker.IsActive, "Buffer tracking is compiled out of Release.");

            var site = nameof(AnExtraReferenceKeepsTheBufferOutstandingUntilTheLastRelease);
            var before = OutstandingAt(site);
            var buffer = new ChannelBuffer(new float[8, 8]);
            buffer.AddRef();

            buffer.Release();
            OutstandingAt(site).ShouldBe(before + 1, "one holder is still reading it");

            buffer.Release();
            OutstandingAt(site).ShouldBe(before);
        }

        /// <summary>
        /// Not inlined, and the buffer never leaves it: a DEBUG build extends a local's lifetime to
        /// the end of its enclosing method, so a buffer created in the test body would still be
        /// rooted when the collection runs and the leak would not reproduce.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DropABufferWithoutReleasingIt() => _ = new ChannelBuffer(new float[64, 64]);

        private static int OutstandingAt(string member)
            => CountAt(ChannelBufferLeakTracker.Report().Live, member);

        /// <summary>
        /// Sum of the entries produced by <paramref name="member"/>, matched on the member half of
        /// the <c>member:line</c> attribution -- exactly, not by prefix, so a member cannot be
        /// credited with a longer-named one's buffers.
        /// </summary>
        /// <remarks>
        /// A linear scan, which is the whole cost: a site is a <c>new ChannelBuffer(...)</c>
        /// expression and the tree has 17 of them (5 in the library, 12 across the tests), so the
        /// array is bounded by source, not by frames. A report during these tests carries one or two.
        /// The tracker's own per-buffer accounting is dictionary-keyed and never scans this.
        /// </remarks>
        private static int CountAt(ImmutableArray<ChannelBufferSite> sites, string member)
        {
            var count = 0;
            foreach (var entry in sites.IsDefault ? ImmutableArray<ChannelBufferSite>.Empty : sites)
            {
                if (IsMember(entry, member))
                {
                    count += entry.Count;
                }
            }

            return count;
        }

        private static ChannelBufferSite FindSite(ImmutableArray<ChannelBufferSite> sites, string member)
        {
            foreach (var entry in sites.IsDefault ? ImmutableArray<ChannelBufferSite>.Empty : sites)
            {
                if (IsMember(entry, member))
                {
                    return entry;
                }
            }

            return default;
        }

        /// <summary>Attribution is <c>member:line</c>, so the character after the member must be the
        /// separator -- that is what makes this an exact member match and not a prefix one.</summary>
        private static bool IsMember(ChannelBufferSite site, string member)
            => site.Producer.Length > member.Length
                && site.Producer[member.Length] == ':'
                && site.Producer.StartsWith(member, StringComparison.Ordinal);
    }
}
