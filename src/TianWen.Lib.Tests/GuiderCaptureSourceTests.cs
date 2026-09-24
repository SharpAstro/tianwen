using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing.PolarAlignment;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Coverage for the PHD2-path failure modes that <see cref="GuiderCaptureSource"/>
    /// surfaces back to <see cref="PolarAlignmentSession"/> via the typed
    /// <see cref="CaptureAndSolveResult.FailureReason"/> field, so users see
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
                TimeSpan.FromSeconds(1), solver, ct: CancellationToken.None);

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
                TimeSpan.FromSeconds(1), solver, ct: CancellationToken.None);

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
                TimeSpan.FromSeconds(1), solver, ct: CancellationToken.None);

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
                TimeSpan.FromSeconds(1), solver, ct: CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.FailureReason.ShouldNotBeNull().ShouldContain("Save Images");
        }

        /// <summary>
        /// The refine loop reads every guide frame through <c>CaptureAsync</c> and releases it after its
        /// solves, so the read is POOLED: releasing the frame hands its plane back, where an unpooled read
        /// left a new float plane per frame to the GC.
        /// </summary>
        [Fact]
        public async Task CaptureAsync_ReadsTheSavedFrameIntoAPlaneItsReleaseHandsBack()
        {
            // A shape nothing else rents, so the rent below can only be answered by this frame's plane.
            var template = WriteGuideFrameTemplate(width: 67, height: 41);
            try
            {
                var capture = await MakeSource(GuiderSaving(template)).CaptureAsync(
                    TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

                capture.Success.ShouldBeTrue();
                var image = capture.Image.ShouldNotBeNull();
                var plane = image.GetChannelArray(0);
                image.Release();

                var rented = Array2DPool<float>.Rent(41, 67);
                rented.ShouldBeSameAs(plane, "a released pooled frame's plane is the next one of its shape");
                Array2DPool<float>.Return(rented);
                File.Delete(capture.FitsPath.ShouldNotBeNull());
            }
            finally
            {
                File.Delete(template);
            }
        }

        /// <summary>A 16-bit mono guide frame on disk, as PHD2's save leaves it.</summary>
        internal static string WriteGuideFrameTemplate(int width, int height)
        {
            var data = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    data[y, x] = 1000f + ((x * 7 + y * 13) % 300);
                }
            }

            var image = new Image([data], BitDepth.Int16, 1299f, 1000f, 0f, new ImageMeta { SensorType = SensorType.Monochrome });
            var path = Path.Combine(Path.GetTempPath(), $"tianwen-guide-{Guid.NewGuid():N}.fits");
            image.WriteToFitsFile(path);
            return path;
        }

        /// <summary>A guider whose every save is a fresh copy of <paramref name="template"/> in the folder asked for.</summary>
        internal static IGuider GuiderSaving(string template)
        {
            var guider = Substitute.For<IGuider>();
            guider.Connected.Returns(true);
            guider.LoopAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult(true));
            guider.SaveImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var saved = Path.Combine(call.ArgAt<string>(0), $"guide_{Guid.NewGuid():N}.fits");
                    File.Copy(template, saved);
                    return ValueTask.FromResult<string?>(saved);
                });
            return guider;
        }

        internal static GuiderCaptureSource MakeSourceFor(IGuider guider) => MakeSource(guider);

        [Fact]
        public void OpticsProperties_AreReturnedAsConstructed()
        {
            var guider = Substitute.For<IGuider>();
            ICaptureSource source = MakeSource(guider);

            source.FocalLengthMm.ShouldBe(FocalLengthMm);
            source.ApertureMm.ShouldBe(ApertureMm);
            source.PixelSizeMicrons.ShouldBe(PixelSizeMicrons);
            source.FRatio.ShouldBe(FocalLengthMm / ApertureMm);
            // 206.265 * 3.75 / 200 ≈ 3.87 arcsec/px: well inside the 1-5"/px
            // "fast solve" band the ranker prefers.
            source.PixelScaleArcsecPerPx.ShouldBeInRange(3.8, 3.9);
        }
    }

    /// <summary>
    /// <c>CaptureAndSolveAsync</c> solves the saved FILE, so reading the frame into memory as well was a
    /// whole frame of garbage per capture for an image nothing looked at: the FITS reader's buffers, its
    /// typed array and the float plane. In the allocation collection because the measurement spans awaits.
    /// </summary>
    [Collection("Allocations")]
    public class GuiderCaptureSourceAllocationTests(ITestOutputHelper output)
    {
        [Fact]
        public async Task CaptureAndSolveAsync_SolvesTheSavedFileWithoutReadingIt()
        {
            const int width = 1024, height = 768;
            var template = GuiderCaptureSourceTests.WriteGuideFrameTemplate(width, height);
            try
            {
                var source = GuiderCaptureSourceTests.MakeSourceFor(GuiderCaptureSourceTests.GuiderSaving(template));
                var solver = Substitute.For<IPlateSolver>();
                var ct = TestContext.Current.CancellationToken;

                await source.CaptureAndSolveAsync(TimeSpan.FromSeconds(1), solver, ct: ct);

                var before = GC.GetTotalAllocatedBytes(precise: true);
                await source.CaptureAndSolveAsync(TimeSpan.FromSeconds(1), solver, ct: ct);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

                var plane = (long)width * height * sizeof(float);
                output.WriteLine($"{width}x{height} guide frame, capture and file solve: {allocated:N0} bytes, against a {plane:N0}-byte plane");
                allocated.ShouldBeLessThan(plane / 2, $"the file is solved, not read into memory: {allocated:N0} bytes");
            }
            finally
            {
                File.Delete(template);
            }
        }
    }
}
