using FC.SDK.Canon;
using FC.SDK.Raw;
using NSubstitute;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Canon;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a Canon preview does when the body does not cooperate, and what a Canon frame is once it arrives. Each test is one
/// thing seen live on an EOS 6D over WPD (#1100, #1101, #1102): a blown window rendered magenta, a snapshot whose pixels
/// were the integers 0, 1 and 2, a release that never produced a picture and waited for ever, and settings the body
/// answered busy to and the driver carried on without.
/// </summary>
public class CanonPreviewRobustnessTests
{
    private static readonly CanonWhiteBalance Daylight = new(2.0f, 1.0f, 1.0f, 1.4f);

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Data", "CR2", "_MG_7578.CR2");

    private static bool FixtureUsable => File.Exists(FixturePath) && new FileInfo(FixturePath).Length > 4096;

    // ── the white point ──

    [Fact]
    public void A_body_clips_at_its_own_white_level_and_not_at_the_14_bit_ceiling()
    {
        // An EOS 6D saturates at 0x3C82 = 15490 of 16383, which is 0.9377 of the range above the black level.
        CanonWhitePoint.UnitFor(14, 2048, 0x3C82, Daylight).ShouldBe((0x3C82 - 2048) / 14335f, 1e-6f);
    }

    [Fact]
    public void The_lowest_white_balance_factor_sets_the_white_point_because_that_channel_clips_first()
    {
        var lowestIsGreen = CanonWhitePoint.UnitFor(14, 2048, 0x3C82, new CanonWhiteBalance(1.8f, 1.1f, 1.2f, 1.5f));
        lowestIsGreen.ShouldBe((0x3C82 - 2048) / 14335f * 1.1f, 1e-6f);

        // Red and blue above green is the usual case, which is what made a clipped highlight magenta: after the clamp they
        // meet green at this value instead of running on to their own factor.
        (CanonWhitePoint.UnitFor(14, 2048, 0x3C82, Daylight) * Daylight.R).ShouldBeGreaterThan(1.0f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2048)]
    [InlineData(20000)]
    public void A_body_with_no_usable_saturation_level_clips_at_the_ceiling(int maxRaw)
    {
        CanonWhitePoint.UnitFor(14, 2048, maxRaw, Daylight).ShouldBe(1.0f, 1e-6f);
    }

    [Fact]
    public void A_frame_in_ADU_counts_is_the_unit_referred_frame_times_the_range_above_the_black_level_and_both_stop_at_the_white_point()
    {
        if (!FixtureUsable)
        {
            Assert.Skip($"CR2 fixture not present or LFS pointer at {FixturePath}. Run `git lfs pull --include=\"*.CR2\"` to fetch.");
            return;
        }

        Image.TryReadCanonRaw(FixturePath, null, out var unit).ShouldBeTrue();
        Image.TryReadCanonRaw(FixturePath, null, out var adu, aduDomain: true).ShouldBeTrue();

        var raw = CanonRaw.Open(FixturePath);
        var headroom = CanonWhitePoint.HeadroomAdu(raw.BitDepth, CanonWhitePoint.BlackLevel);
        var whiteAdu = adu.ImageMeta.SensorFullScaleAdu.ShouldNotBeNull("a driver's frame states where it clips");
        unit.ImageMeta.SensorFullScaleAdu.ShouldBeNull("the file import stays unit-referred, as it always was");

        var u = unit.GetChannelSpan(0);
        var a = adu.GetChannelSpan(0);
        u.Length.ShouldBe(a.Length);
        var different = 0;
        for (var i = 0; i < u.Length; i++)
        {
            if (a[i] != (u[i] * headroom) + CanonWhitePoint.BlackLevel) different++;
        }

        different.ShouldBe(0, "the two reads differ by the scale and the black level, and nothing else");
        ((whiteAdu - CanonWhitePoint.BlackLevel) / headroom).ShouldBeLessThanOrEqualTo(2.0f);
    }

    [Fact]
    public void A_drivers_frame_saved_as_a_snapshot_reads_back_as_the_same_picture_and_not_as_three_values()
    {
        if (!FixtureUsable)
        {
            Assert.Skip($"CR2 fixture not present or LFS pointer at {FixturePath}. Run `git lfs pull --include=\"*.CR2\"` to fetch.");
            return;
        }

        // What GetImageAsync does with the driver's plane: an Int16-depth image, because the driver says its counts are
        // integers. Handed unit-referred floats that was the integers 0, 1 and 2 in the file (#1101).
        Image.TryReadCanonRaw(FixturePath, null, out var read, aduDomain: true).ShouldBeTrue();
        var frame = new Image([read.GetChannelArray(0)], BitDepth.Int16, read.MaxValue, read.MinValue, 0f, read.ImageMeta);
        var path = Path.Combine(Path.GetTempPath(), $"canon_snapshot_{Guid.NewGuid():N}.fits");
        try
        {
            frame.WriteToFitsFile(path);
            Image.TryReadFitsFile(path, out var back).ShouldBeTrue();

            var written = frame.GetChannelSpan(0);
            var readBack = back.GetChannelSpan(0);
            readBack.Length.ShouldBe(written.Length);
            var (off, distinct) = (0, new System.Collections.Generic.HashSet<float>());
            for (var i = 0; i < written.Length; i++)
            {
                // Under one count, not half: the file stores whole counts and truncates, and white balance leaves the
                // blue photosites (x1.4) fractional.
                if (MathF.Abs(readBack[i] - written[i]) >= 1f) off++;
                if (distinct.Count < 5000) distinct.Add(readBack[i]);
            }

            off.ShouldBe(0, "every count survives the file to within a whole count");
            distinct.Count.ShouldBeGreaterThan(1000, "a photograph, not a handful of levels");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_darks_noise_below_the_black_level_survives_the_file_because_the_frame_keeps_the_black_level()
    {
        // A dark is read noise around the black level, so black-subtracted half of it is negative, and a 16-bit FITS file
        // (BZERO 32768) wrote every such pixel as 0: a 6D's darks read back with a median of 0 in every colour. The frame
        // keeps the black level, as a camera's offset, so the file holds the whole of the noise.
        var white = CanonWhitePoint.UnitFor(14, 2048, 0x3C82, Daylight);
        var headroom = CanonWhitePoint.HeadroomAdu(14, 2048);
        var random = new Random(20260930);
        const int Width = 64, Height = 64;
        var plane = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            // Read noise of about 3 counts either side of the black level, in unit terms.
            var row = Enumerable.Range(0, Width).Select(_ => (float)((random.NextDouble() - 0.5) * 6.0 / headroom)).ToArray();
            CanonWhitePoint.ClampRow(row, System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref plane[y, 0], Width), white, headroom, CanonWhitePoint.BlackLevel);
        }

        var frame = new Image([plane], BitDepth.Int16, CanonWhitePoint.BlackLevel + 3f, CanonWhitePoint.BlackLevel - 3f, 0f,
            new ImageMeta { SensorFullScaleAdu = (white * headroom) + CanonWhitePoint.BlackLevel });
        var path = Path.Combine(Path.GetTempPath(), $"canon_dark_{Guid.NewGuid():N}.fits");
        try
        {
            frame.WriteToFitsFile(path);
            Image.TryReadFitsFile(path, out var back).ShouldBeTrue();

            var readBack = back.GetChannelSpan(0).ToArray();
            readBack.Count(v => v < CanonWhitePoint.BlackLevel).ShouldBeGreaterThan(readBack.Length / 3, "the noise below the black level is kept");
            readBack.Count(v => v == 0f).ShouldBe(0, "nothing is clipped to 0");
            var mean = readBack.Average();
            mean.ShouldBe(CanonWhitePoint.BlackLevel, 1.0, "centred on the black level, not pushed up by a clip");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_clipped_pixel_is_held_at_the_white_point_in_every_channel_so_a_blown_highlight_is_neutral()
    {
        // A window the body clipped, after white balance (2.0, 1.0, 1.0, 1.4) and the range above the black level: green
        // stopped at the white point, red and blue went on to their factor. Unclamped that is (R, G, B) = (1.88, 0.94, 1.31),
        // magenta; held at the white point all three are the same, white.
        var white = CanonWhitePoint.UnitFor(14, 2048, 0x3C82, Daylight);
        float[] clipped = [white * 2.0f, white, white * 1.4f];
        var held = new float[3];

        CanonWhitePoint.ClampRow(clipped, held, white, toOutput: 1f);

        held.ShouldAllBe(v => v == white);
    }

    [Fact]
    public void The_vector_row_is_the_scalar_row_bit_for_bit_at_every_length_including_the_tail()
    {
        var random = new Random(20260930);
        var white = CanonWhitePoint.UnitFor(14, 2048, 0x3C82, Daylight);
        foreach (var length in Enumerable.Range(0, 70).Append(4097))
        {
            // Values from black to well past the white point, so the clamp is in play on every lane.
            var row = Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 2.0 * white)).ToArray();
            var expected = new float[length];
            var actual = new float[length];

            var expectedPeak = CanonWhitePoint.ClampRowScalar(row, expected, white, toOutput: 14335f, offset: CanonWhitePoint.BlackLevel);
            var actualPeak = CanonWhitePoint.ClampRow(row, actual, white, toOutput: 14335f, offset: CanonWhitePoint.BlackLevel);

            actual.SequenceEqual(expected).ShouldBeTrue($"length {length}");
            actualPeak.ShouldBe(expectedPeak, $"peak at length {length}");
        }
    }

    [Fact]
    public void A_row_that_clips_nowhere_is_unchanged_in_unit_terms_and_scaled_exactly_in_ADU_terms()
    {
        var white = CanonWhitePoint.UnitFor(14, 2048, 0x3C82, Daylight);
        float[] row = [0f, 0.001f, 0.25f, 0.5f, white];
        var unit = new float[row.Length];
        var adu = new float[row.Length];
        var headroom = CanonWhitePoint.HeadroomAdu(14, 2048);

        var unitPeak = CanonWhitePoint.ClampRow(row, unit, white, toOutput: 1f);
        var aduPeak = CanonWhitePoint.ClampRow(row, adu, white, toOutput: headroom);

        unit.ShouldBe(row, "a bit-identical row when nothing clips");
        adu.ShouldBe(row.Select(v => v * headroom).ToArray());
        (unitPeak, aduPeak).ShouldBe((white, white * headroom));
    }

    // ── a body that answers busy ──

    [Fact]
    public async Task A_busy_answer_is_asked_again_until_the_body_takes_the_write()
    {
        var answers = new[] { EdsError.DeviceBusy, EdsError.DeviceBusy, EdsError.OK };
        var calls = 0;

        var result = await CanonBusyRetry.RunAsync(() => Task.FromResult(answers[calls++]), new FakeTimeProviderWrapper(), TestContext.Current.CancellationToken);

        result.ShouldBe(EdsError.OK);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_body_that_stays_busy_is_asked_a_bounded_number_of_times_and_the_answer_is_returned()
    {
        var calls = 0;

        var result = await CanonBusyRetry.RunAsync(() => { calls++; return Task.FromResult(EdsError.DeviceBusy); }, new FakeTimeProviderWrapper(), TestContext.Current.CancellationToken);

        result.ShouldBe(EdsError.DeviceBusy);
        calls.ShouldBe(CanonBusyRetry.Attempts);
    }

    [Fact]
    public async Task Any_other_answer_is_final_the_first_time()
    {
        var calls = 0;

        var result = await CanonBusyRetry.RunAsync(() => { calls++; return Task.FromResult(EdsError.InvalidParameter); }, new FakeTimeProviderWrapper(), TestContext.Current.CancellationToken);

        result.ShouldBe(EdsError.InvalidParameter);
        calls.ShouldBe(1);
    }

    // ── a picture that never comes ──

    [Fact]
    public void A_lost_exposure_is_given_up_after_a_tenth_more_than_the_exposure_and_a_grace_for_the_download()
    {
        CanonCameraDriver.LostExposureDeadline(TimeSpan.FromSeconds(10)).ShouldBe(TimeSpan.FromSeconds(41));
        CanonCameraDriver.LostExposureDeadline(TimeSpan.Zero).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_preview_whose_frame_never_comes_ends_with_an_error_naming_the_camera_and_aborts_the_exposure()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.Name.Returns("Canon EOS 6D");
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));

        var error = await Should.ThrowAsync<TimeoutException>(() => PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(1), gain: null, binning: 1, new FakeTimeProviderWrapper(), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("Canon EOS 6D");
        await camera.Received(1).AbortExposureAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_preview_that_is_cancelled_aborts_its_exposure_so_the_camera_can_expose_again()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));
        using var cancelled = new CancellationTokenSource();
        var clock = new FakeTimeProviderWrapper();
        camera.StartExposureAsync(Arg.Any<TimeSpan>(), Arg.Any<FrameType>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The Stop arrives while the exposure runs.
                cancelled.Cancel();
                return ValueTask.FromResult(DateTimeOffset.UnixEpoch);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(600), gain: null, binning: 1, clock, cancelled.Token));

        await camera.Received(1).AbortExposureAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_driver_that_says_the_picture_is_not_coming_ends_the_preview_with_its_own_message()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.GetImageReadyAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask<bool>>(_ => throw new InvalidOperationException("Canon EOS 6D sent no picture for a 0.1 s exposure."));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(0.1), gain: null, binning: 1, new FakeTimeProviderWrapper(), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("sent no picture");
    }

    [Fact]
    public async Task A_gain_the_camera_refuses_ends_the_preview_before_an_exposure_is_started()
    {
        var camera = Substitute.For<ICameraDriver>();
        camera.UsesGainMode.Returns(true);
        camera.SetGainAsync(Arg.Any<short>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("Canon EOS 6D is busy and refused the ISO 8000 setting."));

        await Should.ThrowAsync<InvalidOperationException>(() => PreviewCapture.CaptureAsync(
            camera, TimeSpan.FromSeconds(0.1), gain: 19, binning: 1, new FakeTimeProviderWrapper(), TestContext.Current.CancellationToken));

        await camera.DidNotReceiveWithAnyArgs().StartExposureAsync(default, default, TestContext.Current.CancellationToken);
    }
}
