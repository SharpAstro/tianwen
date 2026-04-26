using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing.PolarAlignment;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for the PHD2-path failure modes that <see cref="GuiderCaptureSource"/>
    /// surfaces back to <see cref="PolarAlignmentSession"/> via the typed
    /// <see cref="CaptureAndSolveResult.FailureReason"/> field — so users see
    /// "enable Save Images in PHD2" instead of the generic "no plate solve at any rung"
    /// message when the real cause is a misconfigured PHD2 profile.
    /// </summary>
    public class GuiderCaptureSourceTests
    {
        private const double FocalLengthMm = 200;
        private const double ApertureMm = 50;
        private const double PixelSizeMicrons = 3.75;

        private static GuiderCaptureSource MakeSource(IGuider guider, IExternal? external = null) =>
            new(guider,
                displayName: "Test Guider",
                focalLengthMm: FocalLengthMm,
                apertureMm: ApertureMm,
                pixelSizeMicrons: PixelSizeMicrons,
                external ?? Substitute.For<IExternal>(),
                NullLogger.Instance);

        [Fact]
        public async Task CaptureAndSolveAsync_WhenGuiderDisconnected_ReturnsFailureWithoutCallingLoop()
        {
            var guider = Substitute.For<IGuider>();
            guider.Connected.Returns(false);
            var solver = Substitute.For<IPlateSolver>();

            var result = await MakeSource(guider).CaptureAndSolveAsync(
                TimeSpan.FromSeconds(1), solver, CancellationToken.None);

            result.Success.ShouldBeFalse();
            await guider.DidNotReceive().LoopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task CaptureAndSolveAsync_WhenLoopTimesOut_SurfacesTimeoutFailureReason()
        {
            var guider = Substitute.For<IGuider>();
            guider.Connected.Returns(true);
            guider.LoopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult(false));
            var solver = Substitute.For<IPlateSolver>();

            var result = await MakeSource(guider).CaptureAndSolveAsync(
                TimeSpan.FromSeconds(1), solver, CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.FailureReason.ShouldNotBeNull().ShouldContain("did not produce a frame");
        }

        [Fact]
        public async Task CaptureAndSolveAsync_WhenSaveImageReturnsNull_SurfacesPHD2SaveImagesHint()
        {
            var guider = Substitute.For<IGuider>();
            guider.Connected.Returns(true);
            guider.LoopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult(true));
            guider.SaveImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult<string?>(null));
            var solver = Substitute.For<IPlateSolver>();

            var result = await MakeSource(guider).CaptureAndSolveAsync(
                TimeSpan.FromSeconds(1), solver, CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.FailureReason.ShouldNotBeNull().ShouldContain("Save Images");
        }

        [Fact]
        public async Task CaptureAndSolveAsync_WhenSaveImageThrowsGuiderException_SurfacesPHD2SaveImagesHint()
        {
            var guider = Substitute.For<IGuider>();
            guider.Connected.Returns(true);
            guider.LoopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult(true));
            guider.SaveImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<ValueTask<string?>>(_ => throw new GuiderException("save_image rejected"));
            var solver = Substitute.For<IPlateSolver>();

            var result = await MakeSource(guider).CaptureAndSolveAsync(
                TimeSpan.FromSeconds(1), solver, CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.FailureReason.ShouldNotBeNull().ShouldContain("Save Images");
        }

        [Fact]
        public void OpticsProperties_AreReturnedAsConstructed()
        {
            var guider = Substitute.For<IGuider>();
            ICaptureSource source = MakeSource(guider);

            source.FocalLengthMm.ShouldBe(FocalLengthMm);
            source.ApertureMm.ShouldBe(ApertureMm);
            source.PixelSizeMicrons.ShouldBe(PixelSizeMicrons);
            source.FRatio.ShouldBe(FocalLengthMm / ApertureMm);
            // 206.265 * 3.75 / 200 ≈ 3.87 arcsec/px — well inside the 1-5"/px
            // "fast solve" band the ranker prefers.
            source.PixelScaleArcsecPerPx.ShouldBeInRange(3.8, 3.9);
        }
    }
}
