using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Canon;
using Xunit;
using static TianWen.Lib.Devices.Canon.CanonCameraDriver;

namespace TianWen.Lib.Tests;

/// <summary>
/// How a Canon exposure is taken and what becomes of each object the body announces, as measured on an EOS 6D over WPD on
/// 2026-09-30: a body shooting RAW+JPEG announces two objects a shot and holds every one it is not released from, which
/// stopped a burst after eight frames; its GetObjectInfo answers InternalError, so an object cannot be told by its name; and
/// with mirror lockup armed on the body a release only raises the mirror.
/// </summary>
public class CanonCaptureTests
{
    [Theory]
    [InlineData("IMG_0001.CR2", true)]
    [InlineData("img_0001.cr3", true)]
    [InlineData("CRW_0001.CRW", true)]
    [InlineData("IMG_0001.JPG", false)]
    [InlineData("MVI_0001.MOV", false)]
    public void A_raw_is_told_by_its_extension(string fileName, bool isRaw)
    {
        IsRawFileName(fileName).ShouldBe(isRaw);
    }

    [Fact]
    public void The_raw_an_exposure_is_owed_is_downloaded()
    {
        FateOf("IMG_0001.CR2", awaitingRaw: 3, currentGeneration: 3).ShouldBe(AnnouncedObjectFate.Download);
    }

    [Fact]
    public void The_jpeg_of_a_raw_and_jpeg_body_is_released_whether_or_not_a_raw_is_owed()
    {
        FateOf("IMG_0001.JPG", awaitingRaw: 3, currentGeneration: 3).ShouldBe(AnnouncedObjectFate.ReleaseNotRaw);
        FateOf("IMG_0001.JPG", awaitingRaw: 0, currentGeneration: 3).ShouldBe(AnnouncedObjectFate.ReleaseNotRaw);
    }

    [Theory]
    [InlineData(0, 3)] // no exposure is waiting: the second object of a shot already downloaded
    [InlineData(2, 3)] // owed to an exposure since given up
    public void A_raw_no_exposure_is_owed_is_released_not_kept(int awaitingRaw, int currentGeneration)
    {
        FateOf("IMG_0001.CR2", awaitingRaw, currentGeneration).ShouldBe(AnnouncedObjectFate.ReleaseUnowed);
    }

    [Fact]
    public void An_object_the_body_will_not_name_is_downloaded_when_a_raw_is_owed_and_released_otherwise()
    {
        // The 6D answers GetObjectInfo with InternalError over WPD: the bytes, once downloaded, say whether it was the raw.
        FateOf(null, awaitingRaw: 3, currentGeneration: 3).ShouldBe(AnnouncedObjectFate.Download);
        FateOf(null, awaitingRaw: 0, currentGeneration: 3).ShouldBe(AnnouncedObjectFate.ReleaseUnowed);
    }

    [Fact]
    public void A_jpeg_is_told_from_a_raw_by_its_first_bytes()
    {
        IsJpeg([0xFF, 0xD8, 0xFF, 0xE1]).ShouldBeTrue();
        IsJpeg("II*\0"u8).ShouldBeFalse("a CR2 is a TIFF");
        IsJpeg([0xFF, 0xD8]).ShouldBeFalse("too short to say");
    }

    [Fact]
    public void The_queue_of_announced_objects_can_say_how_many_are_waiting()
    {
        // The queue restores the drive only once nothing more is waiting. A single-reader unbounded channel cannot count,
        // and its Count threw after the first object, which left every later one unread and held in the body.
        var queue = NewAnnouncedQueue();
        queue.Reader.CanCount.ShouldBeTrue();
        queue.Writer.TryWrite(0x3004B151).ShouldBeTrue();
        queue.Reader.Count.ShouldBe(1);
    }

    // What a 6D in third-stop steps announces for Tv down to 1/10 s (it offers none of the half-stop codes), with bulb (0x0C).
    private static readonly uint[] ThirdStops = [0x0C, 0x10, 0x13, 0x15, 0x18, 0x1B, 0x1D, 0x20, 0x23, 0x25, 0x28, 0x2B, 0x2D, 0x30, 0x33, 0x35, 0x38, 0x53];

    // And a body in half-stop steps.
    private static readonly uint[] HalfStops = [0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x54];

    [Theory]
    [InlineData(10.0, 0x1Du)]
    [InlineData(30.0, 0x10u)]
    [InlineData(6.0, 0x23u)]
    [InlineData(5.0, 0x25u)]
    [InlineData(2.5, 0x2Du)]
    [InlineData(1.0, 0x38u)]
    [InlineData(0.1, 0x53u)]
    public void A_shutter_speed_is_one_the_body_offers(double seconds, uint code)
    {
        // A 6D answered DeviceBusy to the half-stop 10 s code (0x1C), for nine seconds and every retry, and the exposure
        // failed as a busy body.
        ClosestTv(TimeSpan.FromSeconds(seconds), ThirdStops).ShouldBe(code);
    }

    [Theory]
    [InlineData(10.0, 0x1Cu)]
    [InlineData(6.0, 0x24u)]
    [InlineData(0.1, 0x54u)]
    public void A_body_in_half_stops_is_given_its_own_codes(double seconds, uint code)
    {
        ClosestTv(TimeSpan.FromSeconds(seconds), HalfStops).ShouldBe(code);
    }

    [Fact]
    public void Bulb_is_never_picked_for_a_timed_exposure()
    {
        ClosestTv(TimeSpan.FromSeconds(60), ThirdStops).ShouldBe(0x10u, "30 s, the longest timed speed; longer is the bulb path's");
    }

    [Theory]
    [InlineData(0x38u, 1.0)]
    [InlineData(0x40u, 0.5)]
    [InlineData(0x30u, 2.0)]
    [InlineData(0x10u, 32.0)]
    public void A_code_is_its_APEX_duration_in_eighths_of_a_stop(uint code, double seconds)
    {
        TvDuration(code).TotalSeconds.ShouldBe(seconds, 1e-9);
    }

    [Fact]
    public void A_body_that_announces_no_speeds_is_given_a_third_stop_code()
    {
        ClosestTv(TimeSpan.FromSeconds(10), null).ShouldBe(0x1Du);
        ClosestTv(TimeSpan.FromSeconds(30), []).ShouldBe(0x10u);
    }

    [Fact]
    public void Mirror_lockup_and_a_daylight_white_balance_are_on_unless_the_device_says_otherwise()
    {
        var device = new CanonDevice(new Uri("Camera://CanonDevice/2977d924d49d424eb38ae2be6157bda3?port=wpd#Canon EOS 6D"));
        device.MirrorLockup.ShouldBeTrue();
        device.DaylightWhiteBalance.ShouldBeTrue();

        var off = new CanonDevice(new Uri("Camera://CanonDevice/2977d924d49d424eb38ae2be6157bda3?port=wpd&mirrorLockup=False&daylightWhiteBalance=False#Canon EOS 6D"));
        off.MirrorLockup.ShouldBeFalse();
        off.DaylightWhiteBalance.ShouldBeFalse();
    }

    [Fact]
    public void A_camera_on_a_cable_shows_its_mirror_lockup_and_white_balance_rows_and_toggling_one_writes_its_key()
    {
        var uri = new Uri("Camera://CanonDevice/2977d924d49d424eb38ae2be6157bda3?port=wpd#Canon EOS 6D");
        var device = new CanonDevice(uri);
        var visible = device.Settings.Where(s => s.IsVisible?.Invoke(uri) ?? true).ToArray();

        visible.Select(s => s.Label).ShouldBe(["Mirror lockup", "White balance"], "the WiFi host row is for a camera on WiFi");
        var lockup = visible[0];
        lockup.FormatValue(uri).ShouldBe("On (2 s settle)");

        var toggled = lockup.Increment(uri);
        new CanonDevice(toggled).MirrorLockup.ShouldBeFalse();
        lockup.FormatValue(toggled).ShouldBe("Off");
        toggled.Query.ShouldContain($"{DeviceQueryKey.MirrorLockup.Key}=False");
    }
}
