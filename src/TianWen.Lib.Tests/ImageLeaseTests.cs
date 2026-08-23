using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the borrow path a hosted consumer needs to read a live driver frame: <see cref="ChannelBuffer.TryAddRef"/>
/// never resurrecting a released buffer, and <see cref="Image.TryLease"/> handing out a disposable
/// <see cref="ImageLease"/> token (or refusing) so the camera cannot recycle the pixels mid-read.
/// <para>
/// The race test below is the reason <c>TryAddRef</c> is a CAS loop rather than a liveness check followed
/// by an increment. The old shape would let a borrower observe "alive", lose the count to zero on the
/// other thread, and then increment from zero: a live reference to a buffer already handed back to the
/// camera, which reads as valid and silently returns another frame's pixels.
/// </para>
/// </summary>
public class ImageLeaseTests
{
    private static Channel BufferedChannel(out ChannelBuffer buffer, out int[] releaseCount)
    {
        var counter = new int[1];
        var data = new float[4, 4];
        var captured = new ChannelBuffer(data, onRelease: _ => counter[0]++);
        buffer = captured;
        releaseCount = counter;
        return new Channel(data, default, 0f, 1f, 0) { Buffer = captured };
    }

    private static Image PlainImage()
        => new Image(
            [new Channel(new float[4, 4], default, 0f, 1f, 0)],
            BitDepth.Float32,
            pedestal: 0f,
            new ImageMeta());

    [Fact]
    public void TryAddRef_OnALiveBuffer_TakesARefTheHolderMustRelease()
    {
        var buffer = new ChannelBuffer(new float[2, 2], onRelease: _ => { });

        buffer.TryAddRef().ShouldBeTrue();
        buffer.RefCount.ShouldBe(2);

        buffer.Release();
        buffer.IsReleased.ShouldBeFalse(); // the creator's ref is still outstanding
        buffer.Release();
        buffer.IsReleased.ShouldBeTrue();
    }

    /// <summary>
    /// A shared refcount DETECTS a double-release; it cannot prevent one. This pins both halves,
    /// because the gap between them is the reason <see cref="Image"/> has to be the token.
    /// </summary>
    /// <remarks>
    /// The offending call is byte-identical to a legitimate last release -- same method, same
    /// count, no holder identity anywhere -- so it is served, the array goes back, and only the
    /// NEXT release finds the till empty. What the throw buys is that the excess stops being
    /// absorbed in silence: before it, the whole sequence completed with no signal of any kind.
    /// </remarks>
    [Fact]
    public void ADoubleReleaseRecyclesUnderTheOtherHolderAndIsCaughtOnlyAfterwards()
    {
        var recycled = 0;
        var buffer = new ChannelBuffer(new float[2, 2], onRelease: _ => recycled++);

        buffer.TryAddRef().ShouldBeTrue();
        buffer.RefCount.ShouldBe(2, "creator plus borrower");

        buffer.Release();                       // creator, legitimate: 2 -> 1
        recycled.ShouldBe(0);

        buffer.Release();                       // creator AGAIN, the bug: 1 -> 0
        recycled.ShouldBe(1,
            "indistinguishable from the borrower's legitimate release, so it is served -- the array "
            + "goes back while the borrower is still reading, and no shared counter can see it coming");

        // The borrower's own legitimate release is the one that finds the till empty. It is the
        // innocent party, and it is where the evidence surfaces.
        Should.Throw<ObjectDisposedException>(() => buffer.Release());
        buffer.RefCount.ShouldBe(0, "the refused release put the count back rather than leaving it negative");
    }

    /// <summary>
    /// And the reason the hazard above is not reachable from production: every holder is an
    /// <see cref="Image"/>, whose <c>Release</c> is one-shot. Prevention lives HERE, in the token,
    /// not in the counter.
    /// </summary>
    [Fact]
    public void ReleasingTheSameImageTwiceSpendsExactlyOneRef()
    {
        var recycled = 0;
        var data = new float[2, 2];
        var buffer = new ChannelBuffer(data, onRelease: _ => recycled++);
        var image = new Image(
            [new Channel(data, default, 0f, 1f, 0) { Buffer = buffer }],
            BitDepth.Float32,
            pedestal: 0f,
            new ImageMeta());

        buffer.TryAddRef().ShouldBeTrue();      // stand in for a second holder
        buffer.RefCount.ShouldBe(2);

        image.Release();
        image.Release();
        image.Release();

        buffer.RefCount.ShouldBe(1, "three calls, one ref spent -- the exchange makes the rest no-ops");
        recycled.ShouldBe(0, "the other holder's ref is intact, so the array has not gone back");

        buffer.Release();
        recycled.ShouldBe(1);
    }

    [Fact]
    public void TryAddRef_OnAReleasedBuffer_RefusesInsteadOfResurrecting()
    {
        var released = 0;
        var buffer = new ChannelBuffer(new float[2, 2], onRelease: _ => released++);
        buffer.Release();

        buffer.TryAddRef().ShouldBeFalse();
        buffer.RefCount.ShouldBe(0);
        released.ShouldBe(1); // and the refusal did not re-run the recycle callback
    }

    [Fact]
    public async Task TryAddRef_RacingTheLastRelease_NeverHandsOutARecycledBuffer()
    {
        var ct = TestContext.Current.CancellationToken;

        // Many rounds because the window being closed is a couple of instructions wide: the point is
        // that no interleaving can report success while the recycle callback has already fired.
        for (var round = 0; round < 1000; round++)
        {
            var recycled = 0;
            var buffer = new ChannelBuffer(new float[2, 2], onRelease: _ => Interlocked.Increment(ref recycled));
            using var start = new SemaphoreSlim(0, 2);

            var releaser = Task.Run(() =>
            {
                start.Wait(ct);
                buffer.Release();
            }, ct);

            var borrower = Task.Run(() =>
            {
                start.Wait(ct);
                return buffer.TryAddRef();
            }, ct);

            start.Release(2);
            var took = await borrower;
            await releaser;

            if (took)
            {
                // Winning the race means the buffer was still alive, so it cannot have been recycled
                // yet - the borrower's own ref is what keeps it alive until it releases.
                Volatile.Read(ref recycled).ShouldBe(0);
                buffer.Release();
            }

            Volatile.Read(ref recycled).ShouldBe(1); // exactly once, whoever got there last
            buffer.RefCount.ShouldBe(0);
        }
    }

    [Fact]
    public void TryLease_OnABufferedImage_SurvivesTheOwnerReleasingIt()
    {
        var channel = BufferedChannel(out _, out var releases);
        var owner = new Image([channel], BitDepth.Float32, pedestal: 0f, new ImageMeta());

        owner.TryLease(out var lease).ShouldBeTrue();

        // The owner publishing its next frame must not pull the pixels out from under the borrower.
        owner.Release();
        releases[0].ShouldBe(0);

        lease.Dispose();
        releases[0].ShouldBe(1); // recycled exactly once, by the last holder
    }

    [Fact]
    public void TryLease_HandsBackADistinctImage_SoDisposeAndTheOwnersReleaseAreIndependent()
    {
        var channel = BufferedChannel(out _, out var releases);
        var owner = new Image([channel], BitDepth.Float32, pedestal: 0f, new ImageMeta());

        owner.TryLease(out var lease).ShouldBeTrue();
        lease.Image.ShouldNotBeSameAs(owner);

        // Disposing the lease twice must not consume the owner's ref: the leased image's Release is
        // one-shot, which is what makes a copied lease struct harmless.
        lease.Dispose();
        lease.Dispose();
        releases[0].ShouldBe(0);

        owner.Release();
        releases[0].ShouldBe(1);
    }

    [Fact]
    public void TryLease_AfterTheFrameIsReleased_RefusesWithAnInertLease()
    {
        var channel = BufferedChannel(out _, out var releases);
        var owner = new Image([channel], BitDepth.Float32, pedestal: 0f, new ImageMeta());
        owner.Release();
        releases[0].ShouldBe(1);

        owner.TryLease(out var lease).ShouldBeFalse();

        // The refused lease is default(ImageLease): reading it is a call-site bug and says so, and
        // disposing it is a no-op rather than a second spend of anything.
        Should.Throw<InvalidOperationException>(() => lease.Image);
        lease.Dispose();
        releases[0].ShouldBe(1);
    }

    [Fact]
    public void ADefaultLease_IsInert()
    {
        var lease = default(ImageLease);

        lease.Dispose(); // must not throw: an empty lease has nothing to give back
        Should.Throw<InvalidOperationException>(() => lease.Image);
    }

    [Fact]
    public void TryLease_OnAMultiChannelFrame_IsAllOrNothing()
    {
        var alive = new float[4, 4];
        var doomedData = new float[4, 4];
        var aliveBuffer = new ChannelBuffer(alive, onRelease: _ => { });
        var doomedBuffer = new ChannelBuffer(doomedData, onRelease: _ => { });

        var image = new Image(
            [
                new Channel(alive, default, 0f, 1f, 0) { Buffer = aliveBuffer },
                new Channel(doomedData, default, 0f, 1f, 1) { Buffer = doomedBuffer },
            ],
            BitDepth.Float32,
            pedestal: 0f,
            new ImageMeta());

        // One plane already gone: a partially referenced frame is not a frame, so the lease fails and
        // the ref taken on the healthy plane is unwound rather than leaked to a caller who cannot see it.
        doomedBuffer.Release();

        image.TryLease(out var lease).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => lease.Image);
        aliveBuffer.RefCount.ShouldBe(1);
    }

    [Fact]
    public void TryLease_OnAnImageWithNoRecycledBuffers_Succeeds_AndTheSourceStaysLeasable()
    {
        // File loads, debayer output and the synthetic fake frames carry no ChannelBuffer at all, and
        // this is the common case for a remote preview - it has to succeed, not fall through to a 404.
        var image = PlainImage();

        image.TryLease(out var first).ShouldBeTrue();

        // Distinct, so disposing the borrow cannot mark the SOURCE released. When the lease was the
        // source itself, one borrow cycle poisoned every later lease of the same published frame: a
        // repeat-polling preview got its first frame and then 404s until the frame happened to swap.
        first.Image.ShouldNotBeSameAs(image);
        first.Dispose();
        image.Width.ShouldBe(4);

        // The regression the distinct lease closes: the SECOND borrow of the same frame.
        image.TryLease(out var second).ShouldBeTrue();
        second.Image.Width.ShouldBe(4);
        second.Dispose();
    }

    [Fact]
    public void TryLease_OnAReleasedUnbufferedImage_StillRefuses()
    {
        var image = PlainImage();
        image.Release();

        image.TryLease(out var lease).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => lease.Image);
    }
}
