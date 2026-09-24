using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A session's resume fingerprint covers the calibration it CHOSE, not the whole library: removing
/// another camera's damaged darks (2026-09-24, which invalidated 58 finished sessions of other cameras
/// under the library-wide digest) leaves it valid, while a change to its own calibration, or a set it
/// would now choose instead, makes it stale.
/// </summary>
public sealed class CalibrationDigestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "caldigest-" + Guid.NewGuid().ToString("N")[..8]);

    public CalibrationDigestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private FrameInfo Frame(string name, FrameType type, string camera, double exposureSec, float tempC, DateTimeOffset when)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[64]);
        var meta = new ImageMeta(
            Instrument: camera,
            ExposureStartTime: when,
            ExposureDuration: TimeSpan.FromSeconds(exposureSec),
            FrameType: type,
            Telescope: "T",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 135,
            FocusPos: -1,
            Filter: Filter.None,
            BinX: 1,
            BinY: 1,
            CCDTemperature: tempC,
            SensorType: SensorType.RGGB,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN,
            Gain: 100,
            Offset: 20);
        return new FrameInfo(path, 100, 100, 1, BitDepth.Int16, meta);
    }

    private static readonly DateTimeOffset Night = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private (ImagingSession Session, List<FrameInfo> Library) Archive()
    {
        var lights = Enumerable.Range(0, 3)
            .Select(i => Frame($"light_{i}.fits", FrameType.Light, "Camera A", 60, -10, Night.AddMinutes(i)))
            .ToImmutableArray();
        var session = new ImagingSession(_dir, "night", "Camera A", "M 42", "", lights);
        var library = new List<FrameInfo>
        {
            Frame("a_dark_0.fits", FrameType.Dark, "Camera A", 60, -10, Night.AddDays(-2)),
            Frame("a_dark_1.fits", FrameType.Dark, "Camera A", 60, -10, Night.AddDays(-2).AddMinutes(2)),
            Frame("b_dark_0.fits", FrameType.Dark, "Camera B", 200, 17, Night.AddDays(-5)),
            Frame("b_dark_1.fits", FrameType.Dark, "Camera B", 200, 17, Night.AddDays(-5).AddMinutes(4)),
        };
        return (session, library);
    }

    private static string Digest(ImagingSession session, IEnumerable<FrameInfo> library)
        => DatasetSessionLedger.CalibrationDigest(
            CalibrationResolver.Choose(session, CalibrationResolver.GroupCalibration(library)));

    [Fact]
    public void RemovingAnotherCamerasCalibration_LeavesTheSessionValid()
    {
        var (session, library) = Archive();
        var before = Digest(session, library);
        CalibrationResolver.Choose(session, CalibrationResolver.GroupCalibration(library)).Dark
            .ShouldNotBeNull().Frames.ShouldAllBe(f => f.Meta.Instrument == "Camera A");

        // The 2026-09-24 case: another camera's darks deleted from the library.
        var without = library.Where(f => f.Meta.Instrument != "Camera B").ToList();
        Digest(session, without).ShouldBe(before);
    }

    [Fact]
    public void AChangeToItsOwnChosenCalibration_MakesTheSessionStale()
    {
        var (session, library) = Archive();
        var before = Digest(session, library);
        File.WriteAllBytes(library[1].Path, new byte[128]);
        Digest(session, library).ShouldNotBe(before, "a frame of the chosen dark grew");
    }

    [Fact]
    public void ANewSetItWouldChoose_MakesTheSessionStale_AndOneItWouldNot_DoesNot()
    {
        var (session, library) = Archive();
        var before = Digest(session, library);

        // Another camera's library growing is not this session's business.
        var unrelated = library.Append(Frame("b_dark_2.fits", FrameType.Dark, "Camera B", 60, -10, Night)).ToList();
        Digest(session, unrelated).ShouldBe(before);

        // A same-camera dark set shot the night of the lights, two days nearer than the one in use:
        // the resolver now chooses it, so the session's calibration changed.
        var nearer = library
            .Append(Frame("a_dark_night_0.fits", FrameType.Dark, "Camera A", 60, -10, Night.AddHours(3)))
            .Append(Frame("a_dark_night_1.fits", FrameType.Dark, "Camera A", 60, -10, Night.AddHours(3).AddMinutes(2)))
            .ToList();
        var chosen = CalibrationResolver.Choose(session, CalibrationResolver.GroupCalibration(nearer)).Dark.ShouldNotBeNull();
        chosen.Frames.ShouldContain(f => Path.GetFileName(f.Path).StartsWith("a_dark_night", StringComparison.Ordinal),
            "the precondition: the nearer set is the one chosen");
        Digest(session, nearer).ShouldNotBe(before);
    }

    [Fact]
    public void TheLibraryWideDigestItReplaced_WouldHaveInvalidatedTheSession()
    {
        // What the ledger used to do, stated so the difference is on record: any calibration frame
        // anywhere moving changed every session's fingerprint.
        var (session, library) = Archive();
        var digestAll = LibraryWide(library);
        var digestWithout = LibraryWide(library.Where(f => f.Meta.Instrument != "Camera B"));
        digestWithout.ShouldNotBe(digestAll);
        Digest(session, library).ShouldBe(Digest(session, library.Where(f => f.Meta.Instrument != "Camera B")));

        static string LibraryWide(IEnumerable<FrameInfo> frames)
            => string.Join("|", frames.Select(f => f.Path).Order(StringComparer.Ordinal));
    }
}
