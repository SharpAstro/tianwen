using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The session's preview slots (<see cref="Session.PublishCapturedImage"/>): a published frame stays readable
/// until the next one replaces it, whatever its publisher does with its own hold, the replaced frame goes
/// back to its camera, and the slot's number moves once per frame and is never reused. The imaging loop's
/// end-to-end case is <c>SessionImagingTests.GivenAWrittenFrameWhenTheLoopMovesOnThenThePreviewSlotStillLeasesIt</c>
/// (P0b item 15 of docs/plans/hardware-in-the-server.md, #752).
/// </summary>
[Collection("Session")]
public class SessionPreviewSlotTests(ITestOutputHelper output)
{
    private async Task<SessionTestContext> CreateAsync()
        => await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task AFrameStaysReadableAfterItsPublisherLetsGo()
    {
        await using var ctx = await CreateAsync();
        var frame = TestFrames.BufferedMono(out var buffer);

        ctx.Session.PublishCapturedImage(0, frame);
        frame.Release(); // the imaging loop's release once the FITS write is done

        buffer.IsReleased.ShouldBeFalse("the slot's own lease keeps the camera from recycling what it shows");
        ctx.Session.LastCapturedImages[0].ShouldNotBeNull().TryLease(out var lease).ShouldBeTrue();
        using (lease)
        {
            TestFrames.StarPeak(lease.Image).ShouldBe(0.9f);
        }

        ctx.Session.ReleaseCapturedImages();
    }

    [Fact]
    public async Task TheNextFrameReplacesItAndTheCameraGetsTheOldOneBack()
    {
        await using var ctx = await CreateAsync();
        var first = TestFrames.BufferedMono(out var firstBuffer, star: 0.9f);
        var second = TestFrames.BufferedMono(out var secondBuffer, star: 0.7f);

        ctx.Session.PublishCapturedImage(0, first);
        first.Release();
        ctx.Session.PublishCapturedImage(0, second);
        second.Release();

        firstBuffer.IsReleased.ShouldBeTrue("a replaced frame goes back to its camera, not a sub later");
        secondBuffer.IsReleased.ShouldBeFalse();
        ctx.Session.LastCapturedImages[0].ShouldNotBeNull().TryLease(out var lease).ShouldBeTrue();
        using (lease)
        {
            TestFrames.StarPeak(lease.Image).ShouldBe(0.7f);
        }

        ctx.Session.ReleaseCapturedImages();
    }

    [Fact]
    public async Task TheNumberMovesOncePerFrameAndIsNeverReused()
    {
        await using var ctx = await CreateAsync();
        var session = ctx.Session;
        session.LastCapturedImageNumber(0).ShouldBe(0, "nothing published yet");

        session.PublishCapturedImage(0, TestFrames.BufferedMono(out _));
        var first = session.LastCapturedImageNumber(0);
        session.PublishCapturedImage(0, TestFrames.BufferedMono(out _));
        var second = session.LastCapturedImageNumber(0);

        first.ShouldNotBe(0);
        second.ShouldNotBe(first);

        // Emptying the slots leaves the number, so a client holding it keeps its picture; the next frame gets
        // one no earlier frame had, where the camera's own counter restarts at every target.
        session.ReleaseCapturedImages();
        session.LastCapturedImageNumber(0).ShouldBe(second);
        session.PublishCapturedImage(0, TestFrames.BufferedMono(out _));
        session.LastCapturedImageNumber(0).ShouldNotBeOneOf(0, first, second);

        session.LastCapturedImageNumber(7).ShouldBe(0, "an index past the rig has no slot");
        session.ReleaseCapturedImages();
    }

    [Fact]
    public async Task RepublishingTheFrameOnShowKeepsItOnShow()
    {
        await using var ctx = await CreateAsync();
        var frame = TestFrames.BufferedMono(out var buffer);

        // The flats path used to guard this by reference: replacing a frame with itself released the ref the
        // slot had just taken. A fresh lease is taken before the old one goes, so there is nothing to guard.
        ctx.Session.PublishCapturedImage(0, frame);
        ctx.Session.PublishCapturedImage(0, frame);
        frame.Release();

        buffer.IsReleased.ShouldBeFalse();
        ctx.Session.LastCapturedImages[0].ShouldNotBeNull().TryLease(out var lease).ShouldBeTrue();
        lease.Dispose();

        ctx.Session.ReleaseCapturedImages();
        buffer.IsReleased.ShouldBeTrue();
    }

    [Fact]
    public async Task AFrameAlreadyGivenBackIsNotShown()
    {
        await using var ctx = await CreateAsync();
        var frame = TestFrames.BufferedMono(out _);
        frame.Release();

        ctx.Session.PublishCapturedImage(0, frame);

        ctx.Session.LastCapturedImages[0].ShouldBeNull("its pixels may already hold the camera's next frame");
        ctx.Session.LastCapturedImageNumber(0).ShouldBe(0);
    }

    [Fact]
    public async Task EmptyingTheSlotsGivesEveryFrameBack()
    {
        await using var ctx = await CreateAsync();
        var frame = TestFrames.BufferedMono(out var buffer);
        ctx.Session.PublishCapturedImage(0, frame);
        frame.Release();

        ctx.Session.ReleaseCapturedImages();

        buffer.IsReleased.ShouldBeTrue();
        ctx.Session.LastCapturedImages[0].ShouldBeNull();
    }
}
