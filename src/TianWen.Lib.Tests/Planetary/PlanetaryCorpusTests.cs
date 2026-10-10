using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planetary corpus as a registry and its lossless crop (docs/plans/planetary-restoration.md, R0, #1048): every
/// capture's header and settings read and never written, a manifest a re-run repeats byte for byte, and a crop that keeps
/// every frame, the Bayer phase, the source's header and its trailer, verified against the source byte for byte.
/// </summary>
public class PlanetaryCorpusTests : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private static readonly DateTimeOffset T0 = new DateTimeOffset(2024, 12, 15, 12, 33, 50, TimeSpan.Zero);

    /// <summary>A crop's options on a drive with room to spare: the default reserve is the user's D:, not a test machine's temp.</summary>
    private static readonly CropOptions Roomy = new CropOptions(KeepFreeBytes: 0);

    private DirectoryInfo NewFolder() => _folders.Create("twcorpus");

    /// <summary>
    /// A folder of one FITS file a frame, as SharpCap writes one by accident: <paramref name="frames"/> mono frames of 16x12 in
    /// <paramref name="depth"/>, stored <paramref name="rowOrder"/>, the first at <paramref name="start"/> (T0 when null) and each
    /// <paramref name="interval"/> after the last, of <paramref name="exposure"/> and <paramref name="frameType"/>. Frame i's
    /// sample at (x, y) is <see cref="FitsSample"/>.
    /// </summary>
    private static void WriteFitsFrames(string folder, int frames, TimeSpan interval, TimeSpan exposure, FrameType frameType,
        BitDepth? depth = null, RowOrder rowOrder = RowOrder.TopDown, DateTimeOffset? start = null)
    {
        Directory.CreateDirectory(folder);
        var bitDepth = depth ?? BitDepth.Int8;
        for (var i = 0; i < frames; i++)
        {
            var data = new float[12, 16];
            for (var y = 0; y < 12; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    data[y, x] = FitsSample(bitDepth, i, x, y);
                }
            }
            var meta = new ImageMeta("ZWO ASI290MM", (start ?? T0) + (i * interval), exposure, frameType, "", 2.9f, 2.9f, 0, -1, Filter.Unknown, 1, 1,
                float.NaN, SensorType.Monochrome, 0, 0, rowOrder, float.NaN, float.NaN);
            new Image([data], bitDepth, bitDepth == BitDepth.Int8 ? 255f : 65535f, 0f, 0f, meta)
                .WriteToFitsFile(Path.Combine(folder, $"2022-09-03-1209_8_LUM_{i + 1:00000}.fits"));
        }
    }

    private static int FitsSample(BitDepth depth, int frame, int x, int y)
        => depth == BitDepth.Int8 ? ((frame * 7) + (y * 16) + x) % 256 : (((frame * 7) + (y * 16) + x) * 331) % 65536;

    private static byte[] HeaderBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[SerHeader.Size];
        stream.ReadExactly(header);
        return header;
    }

    // A header as SharpCap writes one, where SerWriter's differs: the local start time ten hours from UTC (a Sydney night),
    // and the strings padded with spaces rather than zeros.
    private static void AsSharpCapWritesIt(string path)
    {
        var header = HeaderBytes(path);
        var utc = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(170));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(162), utc + TimeSpan.FromHours(10).Ticks);
        header.AsSpan(42, 120).Replace((byte)0, (byte)' ');
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Write(header);
    }

    /// <summary>
    /// A synthetic capture: a bright disk of <paramref name="radius"/> drifting by (<paramref name="vx"/>, <paramref name="vy"/>)
    /// a frame on a dark, faintly noisy sky, in <paramref name="bytesPerSample"/>-byte samples. Frames listed in
    /// <paramref name="dark"/> hold no disk, as a real capture's frames do once the planet drifts out of the field: mostly 0,
    /// with one pixel in 2,000 at 1 to 3 (the real density, about 1,100 of 2.1 million). Such a frame's threshold falls to
    /// about one ADU, so that scattered noise passes it and boxes nearly, but not exactly, the whole frame.
    /// </summary>
    private static void WriteCapture(string path, int width = 320, int height = 240, int frames = 12, int radius = 18, double vx = 1.5,
        double vy = 0.75, SerColorId color = SerColorId.BayerRGGB, int bytesPerSample = 1, bool timestamps = true, int[]? dark = null,
        int littleEndianFlag = -1, Func<int, DateTimeOffset>? timeOf = null)
    {
        var depth = bytesPerSample == 1 ? 8 : 12;
        using var writer = new SerWriter(path, width, height, color, depth, "observer", "ZWO ASI462MC", "Skywatcher 10 inch", littleEndianFlag: littleEndianFlag);
        var rng = new Random(7);
        var frame = new byte[width * height * bytesPerSample];
        for (var i = 0; i < frames; i++)
        {
            var cx = (width / 3.0) + (vx * i);
            var cy = (height / 3.0) + (vy * i);
            var cloud = dark?.Contains(i) ?? false;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var inside = !cloud && ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius;
                    var value = inside ? (bytesPerSample == 1 ? 200 + rng.Next(20) : 3000 + rng.Next(300))
                        : cloud ? (rng.Next(10_000) < 5 ? rng.Next(1, 4) : 0)
                        : rng.Next(bytesPerSample == 1 ? 6 : 60);
                    var at = ((y * width) + x) * bytesPerSample;
                    if (bytesPerSample == 1)
                    {
                        frame[at] = (byte)value;
                    }
                    else if (littleEndianFlag == 1)
                    {
                        frame[at] = (byte)(value >> 8);
                        frame[at + 1] = (byte)value;
                    }
                    else
                    {
                        frame[at] = (byte)value;
                        frame[at + 1] = (byte)(value >> 8);
                    }
                }
            }
            if (timestamps)
            {
                writer.AppendFrame(frame, timeOf?.Invoke(i) ?? T0.AddMilliseconds(i * 2.25));
            }
            else
            {
                writer.AppendFrame(frame);
            }
        }
    }

    [Fact]
    public void ADriveKeepsItsReserveFreeAndARefusalSaysWhy()
    {
        const long gib = 1024L * 1024 * 1024;
        ScratchSpace.Refusal("D:/Astro-Dataset/planetary", 10 * gib, 109 * gib, _ => 222 * gib).ShouldBeNull("222 GB free less 10 leaves 212, over the 109 kept");
        var refusal = ScratchSpace.Refusal("D:/Astro-Dataset/planetary", 120 * gib, 109 * gib, _ => 222 * gib).ShouldNotBeNull();
        refusal.ShouldContain("102.0 GB free");
        refusal.ShouldContain("109.0 GB kept free");

        ScratchSpace.TryParseSize("109G", out var reserve).ShouldBeTrue();
        reserve.ShouldBe(109 * gib);
        ScratchSpace.TryParseSize("500M", out var half).ShouldBeTrue();
        half.ShouldBe(500L * 1024 * 1024);
        ScratchSpace.TryParseSize("lots", out _).ShouldBeFalse();
    }

    [Fact]
    public void SevenZipsListingGivesItsFilesAndDropsItsFolders()
    {
        const string listing = """
            Path = Jupiter
            Size = 0
            Attributes = D

            Path = Jupiter\21_42_31.ser
            Size = 16301234567
            Packed Size = 9000000000
            Attributes = A

            Path = Jupiter\21_42_31.CameraSettings.txt
            Size = 1234
            Attributes = A
            """;
        SevenZipTool.ParseListing(listing).ShouldBe([
            new ArchiveMember("Jupiter\\21_42_31.ser", 16_301_234_567),
            new ArchiveMember("Jupiter\\21_42_31.CameraSettings.txt", 1234),
        ]);
    }

    [Fact]
    public void SharpCapsSettingsAreReadWithItsSectionAsTheCamera()
    {
        var settings = PlanetaryCorpus.ParseSettings("[Uranus-C (IMX585)]\r\nColour Space=RAW8\r\nCapture Area=320x240\r\nExposure=0.0800ms\r\nExposure=ignored\r\n");
        settings["Camera"].ShouldBe("Uranus-C (IMX585)");
        settings["Colour Space"].ShouldBe("RAW8");
        settings["Capture Area"].ShouldBe("320x240");
        settings["Exposure"].ShouldBe("0.0800ms", "the first of a repeated key");
        settings.Keys.ToArray().ShouldBe(settings.Keys.Order(StringComparer.Ordinal).ToArray(), "keys sorted, so the manifest's bytes are stable");
    }

    [Fact(Timeout = 60_000)]
    public async Task AFolderOfFitsFramesIsAPlanetaryVideoOnlyWhenItsTimesSayVideoAndABiasVideoIsFlaggedCalibration()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = NewFolder();
        // SharpCap's accidental FITS video at 250 fps; a night of deep-sky subs, one every ten seconds; a set of flats at two a
        // second; and a bias video at 50 fps whose frames name no type, only its folder.
        WriteFitsFrames(Path.Combine(root.FullName, "Jupiter", "lum"), 6, TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(4), FrameType.Light);
        WriteFitsFrames(Path.Combine(root.FullName, "NGC 3521", "rawframes"), 6, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(8), FrameType.Light);
        WriteFitsFrames(Path.Combine(root.FullName, "Saturn Nebula", "Flats"), 6, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(150), FrameType.Flat);
        WriteFitsFrames(Path.Combine(root.FullName, "Bias", "ASI462_100g"), 6, TimeSpan.FromMilliseconds(20), TimeSpan.FromTicks(320), FrameType.None);

        var manifest = await PlanetaryCorpus.SurveyAsync([root.FullName], new CorpusSurveyOptions(MinFitsSequence: 4), NullLogger.Instance, ct);

        var sequences = manifest.Captures.Where(c => c.Kind == CaptureKind.FitsSequence).ToDictionary(c => Path.GetFileName(c.Path));
        sequences.Keys.Order(StringComparer.Ordinal).ToArray().ShouldBe(["ASI462_100g", "lum"], "the subs and the flats are not videos, whatever their count");
        sequences["lum"].Frames.ShouldBe(6);
        sequences["lum"].FramesPerSecond.ShouldNotBeNull().ShouldBeGreaterThan(5);
        sequences["lum"].Flags.ShouldBeEmpty();
        sequences["ASI462_100g"].Flags.ShouldBe(["calibration"], "a bias video is the camera's own noise, kept and marked");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(8, RowOrder.TopDown)]
    [InlineData(16, RowOrder.TopDown)]
    [InlineData(8, RowOrder.BottomUp)]
    public async Task AFitsVideoBecomesOneSerOfTheFilesOwnSamplesAndTimesToTheTick(int bits, RowOrder rowOrder)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = NewFolder();
        var folder = Path.Combine(root.FullName, "Jupiter", "lum");
        var depth = bits == 8 ? BitDepth.Int8 : BitDepth.Int16;
        // 43 ms and a fraction of a millisecond past the second: a DATE-OBS FITS.Lib used to write as ".43" and read to the ms.
        var start = T0.AddMilliseconds(43).AddTicks(2_280);
        var interval = TimeSpan.FromMilliseconds(4).Add(TimeSpan.FromTicks(7));
        WriteFitsFrames(folder, 6, interval, TimeSpan.FromMilliseconds(4), FrameType.Light, depth, rowOrder, start);

        var outcome = PlanetaryFitsVideo.Convert(folder, Path.Combine(root.FullName, "converted"), new FitsVideoOptions(KeepFreeBytes: 0), NullLogger.Instance, ct);
        var result = outcome.Result.ShouldNotBeNull(outcome.Refusal);
        Path.GetFileName(result.Output).ShouldStartWith("2022-09-03-1209_8_LUM.", Case.Sensitive, "the capture's name, without the frame number");

        using var ser = SerReader.Open(result.Output);
        (ser.FrameCount, ser.Width, ser.Height, ser.ColorId, ser.PixelDepthPerPlane).ShouldBe((6, 16, 12, SerColorId.Mono, bits));
        ser.Header.Instrument.ShouldBe("ZWO ASI290MM");
        var samples = new ushort[16 * 12];
        for (var i = 0; i < 6; i++)
        {
            ser.Timestamps[i].ShouldBe(start + (i * interval), $"frame {i}'s DATE-OBS, to the tick");
            ser.ReadFrame16(i, samples);
            for (var y = 0; y < 12; y++)
            {
                var stored = rowOrder == RowOrder.BottomUp ? 11 - y : y;
                for (var x = 0; x < 16; x++)
                {
                    samples[(y * 16) + x].ShouldBe((ushort)FitsSample(depth, i, x, stored), $"frame {i} at ({x}, {y}), the top row first");
                }
            }
        }

        var sidecar = JsonSerializer.Deserialize(File.ReadAllBytes(result.Sidecar), PlanetaryCorpusJsonContext.Default.FitsVideoSidecar).ShouldNotBeNull();
        sidecar.Files.Length.ShouldBe(6);
        sidecar.Files[0].ShouldBe("2022-09-03-1209_8_LUM_00001.fits");
        sidecar.FirstHeader.ShouldContainKey("DATE-OBS");
        sidecar.TimestampsMonotonic.ShouldBe(true);
        var manifest = await PlanetaryCorpus.SurveyAsync([root.FullName], new CorpusSurveyOptions(MinFitsSequence: 4), NullLogger.Instance, ct);
        sidecar.SourceId.ShouldBe(manifest.Captures.Single(c => c.Kind == CaptureKind.FitsSequence).Id, "the SER names its source by the manifest's id");
    }

    [Fact(Timeout = 60_000)]
    public void AFitsVideoWithAFrameOfAnotherSizeIsNotConvertedAndLeavesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = NewFolder();
        var folder = Path.Combine(root.FullName, "lum");
        WriteFitsFrames(folder, 4, TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(4), FrameType.Light);
        var odd = new ImageMeta("ZWO ASI290MM", T0.AddSeconds(1), TimeSpan.FromMilliseconds(4), FrameType.Light, "", 2.9f, 2.9f, 0, -1, Filter.Unknown, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        new Image([new float[10, 16]], BitDepth.Int8, 255f, 0f, 0f, odd).WriteToFitsFile(Path.Combine(folder, "2022-09-03-1209_8_LUM_00005.fits"));
        var output = Path.Combine(root.FullName, "converted");

        Should.Throw<InvalidDataException>(() => PlanetaryFitsVideo.Convert(folder, output, new FitsVideoOptions(KeepFreeBytes: 0), NullLogger.Instance, ct))
            .Message.ShouldContain("16x10");
        Directory.EnumerateFileSystemEntries(output).ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task ASurveyRegistersEveryCaptureAndARerunRepeatsItsBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = NewFolder();
        var night = root.CreateSubdirectory("Jupiter");
        WriteCapture(Path.Combine(night.FullName, "12_33_50Z_.ser"));
        File.WriteAllText(Path.Combine(night.FullName, "12_33_50Z_.CameraSettings.txt"), "[ZWO ASI462MC]\nColour Space=RAW8\nGain=121\n");
        var copies = root.CreateSubdirectory("Copies");
        File.Copy(Path.Combine(night.FullName, "12_33_50Z_.ser"), Path.Combine(copies.FullName, "renamed.ser"));
        WriteCapture(Path.Combine(night.FullName, "12_40_00_pipp.ser"), timestamps: false, vx: 0.5);
        File.WriteAllBytes(Path.Combine(night.FullName, "big.avi"), new byte[1000]);
        WriteCapture(Path.Combine(night.FullName, "cut.ser"), frames: 4);
        using (var stream = new FileStream(Path.Combine(night.FullName, "cut.ser"), FileMode.Open))
        {
            stream.SetLength(stream.Length / 2);
        }

        var first = await PlanetaryCorpus.SurveyAsync([root.FullName], new CorpusSurveyOptions(), NullLogger.Instance, ct);
        var again = await PlanetaryCorpus.SurveyAsync([root.FullName], new CorpusSurveyOptions(), NullLogger.Instance, ct);
        PlanetaryCorpus.ManifestBytes(again).ShouldBe(PlanetaryCorpus.ManifestBytes(first), "a re-run over unchanged files repeats the manifest byte for byte");

        var byName = first.Captures.ToDictionary(c => Path.GetFileName(c.Path));
        var capture = byName["12_33_50Z_.ser"];
        capture.Kind.ShouldBe(CaptureKind.Ser);
        capture.Header.ShouldNotBeNull().FrameCount.ShouldBe(12);
        (capture.Header.Width, capture.Header.Height, capture.Header.ColorId).ShouldBe((320, 240, nameof(SerColorId.BayerRGGB)));
        capture.Timestamps.ShouldBe("trailer");
        capture.TimestampsMonotonic.ShouldBe(true);
        capture.FirstUtc.ShouldBe("2024-12-15T12:33:50.0000000Z");
        capture.Settings.ShouldNotBeNull()["Gain"].ShouldBe("121");

        var copy = byName["renamed.ser"];
        copy.Id.ShouldBe(capture.Id, "the same bytes under another name are the same capture");
        // Which of two identical captures is the original the bytes cannot say: the later in path order is the duplicate.
        var duplicate = new[] { capture, copy }.Single(c => c.Flags.Contains("duplicate"));
        var kept = new[] { capture, copy }.Single(c => !c.Flags.Contains("duplicate"));
        duplicate.DuplicateOf.ShouldBe(kept.Path);

        byName["12_40_00_pipp.ser"].Flags.ShouldBe(["no-timestamps", "pipp"]);
        byName["big.avi"].Flags.ShouldBe(["not-read"]);
        var cut = byName["cut.ser"];
        cut.Problem.ShouldNotBeNull().ShouldContain("shorter than");
        cut.Flags.ShouldBe(["truncated"]);

        // The manifest reads back through the same context it was written with.
        var back = JsonSerializer.Deserialize(PlanetaryCorpus.ManifestBytes(first), PlanetaryCorpusJsonContext.Default.CorpusManifest).ShouldNotBeNull();
        back.Captures.Length.ShouldBe(first.Captures.Length);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1, -1)]
    [InlineData(2, -1)]
    [InlineData(2, 1)]
    public void ACropKeepsEveryFrameThePhaseTheHeaderAndTheTimesAndIsTheSourcesWindow(int bytesPerSample, int littleEndianFlag)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewFolder();
        var source = Path.Combine(folder.FullName, "whole-sensor.ser");
        // Three empty frames of 16 is more than the tenth the window's sizing percentile tolerates: before the locator counted
        // only a blob, their scattered noise boxed the whole frame and the crop was refused.
        WriteCapture(source, width: 480, height: 360, frames: 16, vx: 3, vy: 2, bytesPerSample: bytesPerSample, littleEndianFlag: littleEndianFlag,
            dark: [5, 9, 13]);
        AsSharpCapWritesIt(source);

        var outcome = PlanetaryCrop.Crop(source, Path.Combine(folder.FullName, "crops"), Roomy, NullLogger.Instance, ct);
        var result = outcome.Result.ShouldNotBeNull(outcome.Refusal);
        result.Frames.ShouldBe(16, "every frame, the empty ones too");
        result.Located.ShouldBe(13, "all but the three with no disk");
        result.Width.ShouldBeLessThan(480);
        result.OutputBytes.ShouldBeLessThan(new FileInfo(source).Length / 3);

        using var original = SerReader.Open(source);
        using var crop = SerReader.Open(result.Output);
        (crop.ColorId, crop.PixelDepthPerPlane, crop.Header.LittleEndianFlag).ShouldBe((original.ColorId, original.PixelDepthPerPlane, original.Header.LittleEndianFlag));
        crop.Timestamps.ShouldBe(original.Timestamps);
        crop.Header.LocalDateTime.ShouldBe(original.Header.LocalDateTime, "the site's time zone survives the crop");
        var (sourceHeader, cropHeader) = (HeaderBytes(source), HeaderBytes(result.Output));
        cropHeader.AsSpan(0, 26).SequenceEqual(sourceHeader.AsSpan(0, 26)).ShouldBeTrue("the header is the source's bar its width and height");
        cropHeader.AsSpan(34).SequenceEqual(sourceHeader.AsSpan(34)).ShouldBeTrue("the header is the source's bar its width and height");

        var sidecar = JsonSerializer.Deserialize(File.ReadAllBytes(result.Sidecar), PlanetaryCorpusJsonContext.Default.CropSidecar).ShouldNotBeNull();
        sidecar.SourceHeader.Instrument.ShouldBe("ZWO ASI462MC", "the source's header, as it was");
        sidecar.LocatedFrames[5].ShouldBeFalse();
        (sidecar.OriginX[5], sidecar.OriginY[5]).ShouldBe((sidecar.OriginX[4], sidecar.OriginY[4]), "a frame with no disk keeps the last window");
        sidecar.OriginX.ShouldAllBe(x => x % 2 == 0, "an even origin keeps the Bayer phase");
        sidecar.OriginY.ShouldAllBe(y => y % 2 == 0);
        sidecar.OriginX[^1].ShouldBeGreaterThan(sidecar.OriginX[0], "the window followed the drifting disk");

        // Read independently of the crop's own check: every frame is the source's window at its recorded origin.
        var pixel = original.PlaneCount * original.BytesPerSample;
        var whole = new byte[original.FrameSizeBytes];
        var cropped = new byte[crop.FrameSizeBytes];
        for (var i = 0; i < 16; i++)
        {
            original.ReadFrameBytes(i, whole);
            crop.ReadFrameBytes(i, cropped);
            for (var row = 0; row < crop.Height; row++)
            {
                var from = (((sidecar.OriginY[i] + row) * original.Width) + sidecar.OriginX[i]) * pixel;
                cropped.AsSpan(row * crop.Width * pixel, crop.Width * pixel).SequenceEqual(whole.AsSpan(from, crop.Width * pixel))
                    .ShouldBeTrue($"frame {i}, row {row}");
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public void ACropOfADiskFillingTheFrameOrOfADriveWithoutRoomIsRefusedAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = NewFolder();
        var output = Path.Combine(folder.FullName, "crops");

        var small = Path.Combine(folder.FullName, "small.ser");
        WriteCapture(small, width: 480, height: 360, frames: 4);
        var full = PlanetaryCrop.Crop(small, output, Roomy with { Width = 480, Height = 360 }, NullLogger.Instance, ct);
        full.Result.ShouldBeNull();
        full.Refusal.ShouldNotBeNull().ShouldContain("nothing to gain");

        var cramped = PlanetaryCrop.Crop(small, output, new CropOptions(KeepFreeBytes: 109L * 1024 * 1024 * 1024, FreeBytesOf: _ => 100L * 1024 * 1024 * 1024),
            NullLogger.Instance, ct);
        cramped.Result.ShouldBeNull();
        cramped.Refusal.ShouldNotBeNull().ShouldContain("kept free");
        Directory.GetFiles(output).ShouldBeEmpty("a refusal writes nothing");
    }
    // A surveyed SER capture as the session rule reads it: its path, frame size, colour and the span of its frames' times.
    private static CaptureRecord Ser(string path, DateTimeOffset first, double minutes, int width = 640, int frames = 1000,
        SerColorId colour = SerColorId.BayerRGGB, params string[] flags) => new CaptureRecord
    {
        Id = path,
        Kind = CaptureKind.Ser,
        Path = path,
        LengthBytes = 1,
        Header = new SerHeaderRecord(0, colour.ToString(), 0, width, 480, 8, frames, "", "", "", 0, 0),
        Timestamps = "trailer",
        FirstUtc = first.UtcDateTime.ToString("O"),
        LastUtc = first.AddMinutes(minutes).UtcDateTime.ToString("O"),
        Flags = flags,
    };

    [Fact(Timeout = 60_000)]
    public async Task ACaptureSortedByQualityIsSurveyedOverItsEarliestAndLatestFramesNeverItsFirstAndLast()
    {
        // #1292, found again by the audit on #1343: PIPP writes a capture sorted by quality, so its first frame was taken mid-run and its
        // last before the end. The survey's span is what Sessions chains captures on, so it is the frames' earliest and latest times.
        var ct = TestContext.Current.CancellationToken;
        var night = NewFolder().CreateSubdirectory("Saturn");
        int[] takenAt = [5, 0, 9, 2, 11, 7, 1, 4, 10, 3, 8, 6]; // frame i was taken takenAt[i] seconds into the run
        WriteCapture(Path.Combine(night.FullName, "sorted.ser"), timeOf: i => T0.AddSeconds(takenAt[i]));

        var survey = await PlanetaryCorpus.SurveyAsync([night.FullName], new CorpusSurveyOptions(), NullLogger.Instance, ct);

        var capture = survey.Captures.ShouldHaveSingleItem();
        capture.TimestampsMonotonic.ShouldBe(false);
        capture.FirstUtc.ShouldBe("2024-12-15T12:33:50.0000000Z", "the earliest frame, not the first, which was taken 5 s in");
        capture.LastUtc.ShouldBe("2024-12-15T12:34:01.0000000Z", "the latest frame, not the last, which was taken 6 s in");
    }

    [Fact]
    public async Task AFrameTheCaptureDidNotStampIsNoTimeInTheSurvey()
    {
        // #1409: a zero tick in the trailer read as 0001-01-01, the capture's earliest time, and Sessions chains captures on that span
        var ct = TestContext.Current.CancellationToken;
        var night = NewFolder().CreateSubdirectory("Jupiter");
        var path = Path.Combine(night.FullName, "unstamped.ser");
        WriteCapture(path);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - (12 * sizeof(long))), 0); // the first of 12 frames
        await File.WriteAllBytesAsync(path, bytes, ct);

        var survey = await PlanetaryCorpus.SurveyAsync([night.FullName], new CorpusSurveyOptions(), NullLogger.Instance, ct);

        var capture = survey.Captures.ShouldHaveSingleItem();
        capture.FirstUtc.ShouldBe(T0.AddMilliseconds(2.25).UtcDateTime.ToString("O"), "the earliest STAMPED frame, the second");
        capture.LastUtc.ShouldBe(T0.AddMilliseconds(11 * 2.25).UtcDateTime.ToString("O"));
        capture.Flags.ShouldContain("unstamped-frames");
    }

    [Fact]
    public void ASessionIsBackToBackCapturesOfOneFolderAndFrameSizeAndAGapEndsIt()
    {
        // #1308: a capture program saves a long run as many files seconds apart. They join in time order while each starts within ten
        // minutes of the last one's end; a longer pause, another folder, another frame size, a copy or a calibration video does not join.
        var t = new DateTimeOffset(2022, 9, 29, 12, 0, 0, TimeSpan.Zero);
        CaptureRecord[] captures =
        [
            Ser("D:/Jupiter 4ms/b.ser", t.AddMinutes(1.5), 1),     // listed out of order: the session sorts by time
            Ser("D:/Jupiter 4ms/a.ser", t, 1),
            Ser("D:/Jupiter 4ms/c.ser", t.AddMinutes(12), 1),      // 9.5 minutes after b ends: still the session
            Ser("D:/Jupiter 4ms/d.ser", t.AddMinutes(30), 1),      // 17 minutes after c ends: a new chain, alone, so no session
            Ser("D:/Jupiter 4ms/e.ser", t.AddMinutes(2), 1, width: 320), // another frame size
            Ser("D:/Jupiter 4ms/copy.ser", t.AddMinutes(3), 1, flags: ["duplicate"]),
            Ser("D:/Jupiter 4ms/dark.ser", t.AddMinutes(4), 1, flags: ["calibration"]),
            Ser("D:/Saturn/s1.ser", t, 1),                          // another folder, alone
        ];

        var sessions = PlanetaryCorpus.Sessions(captures).ToArray();

        var session = sessions.ShouldHaveSingleItem();
        session.Captures.ShouldBe(["D:/Jupiter 4ms/a.ser", "D:/Jupiter 4ms/b.ser", "D:/Jupiter 4ms/c.ser"]);
        session.Name.ShouldBe("Jupiter 4ms 2022-09-29 1200");
        (session.Folder, session.Width, session.Height, session.Frames).ShouldBe(("D:/Jupiter 4ms", 640, 480, 3000));
        session.FirstUtc.ShouldBe(captures[1].FirstUtc);
        session.LastUtc.ShouldBe(captures[2].LastUtc);

        // The manifest carries its sessions and reads them back.
        var manifest = new CorpusManifest(PlanetaryCorpus.ManifestVersion, ["D:/"], captures) { Sessions = sessions };
        var back = JsonSerializer.Deserialize(PlanetaryCorpus.ManifestBytes(manifest), PlanetaryCorpusJsonContext.Default.CorpusManifest).ShouldNotBeNull();
        back.Sessions.ShouldHaveSingleItem().Captures.ShouldBe(session.Captures);
    }

    [Fact]
    public void AMonoFilterWheelRunInOneFolderIsASessionPerFilter()
    {
        // #1336: the owner's Saturn of 2026-10-07 is ten bursts of red, green and blue, 158 s each, every file started about two and a
        // half minutes after the last, all in one folder, the bursts 13 minutes apart: a filter's own files are further apart than the
        // gap while the run goes on. Each filter is a session of its own, named for its wavelength; a capture whose name says no filter
        // joins none, and a pause longer than the gap still ends the run.
        var t = new DateTimeOffset(2026, 10, 7, 11, 34, 0, TimeSpan.Zero);
        var captures = new List<CaptureRecord>();
        string[] filters = ["Red", "Green", "Blue"];
        for (var burst = 0; burst < 3; burst++)
        {
            for (var f = 0; f < filters.Length; f++)
            {
                var start = t.AddMinutes((burst * 13) + (f * 2.6));
                captures.Add(Ser($"E:/Saturn/2026-10-07/Light/{start:HH_mm_ss}Z_{filters[f]}.ser", start, 158 / 60.0, colour: SerColorId.Mono));
            }
        }
        captures.Add(Ser("E:/Saturn/2026-10-07/Light/11_45_00Z.ser", t.AddMinutes(11), 2, colour: SerColorId.Mono));
        captures.Add(Ser("E:/Saturn/2026-10-07/Light/13_00_00Z_Red.ser", t.AddMinutes(86), 2.6, colour: SerColorId.Mono)); // 40 minutes on

        var sessions = PlanetaryCorpus.Sessions(captures).ToArray();

        sessions.Select(s => s.Name).ShouldBe(["Light 2026-10-07 1134 650 nm", "Light 2026-10-07 1136 530 nm", "Light 2026-10-07 1139 460 nm"]);
        for (var f = 0; f < filters.Length; f++)
        {
            sessions[f].Captures.Length.ShouldBe(3);
            sessions[f].Captures.ShouldAllBe(path => path.EndsWith($"_{filters[f]}.ser", StringComparison.Ordinal));
            sessions[f].Frames.ShouldBe(3000);
        }
    }

    [Fact]
    public async Task TheBestFramesTimeIsTheTimeOfTheFrameAStackWouldTakeForItsReference()
    {
        // #1308's --epoch: a run is carried to its middle file's best frame, graded as the stack grades. The sharp frame is the third.
        var t = new DateTimeOffset(2022, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var frames = new float[5][,];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = new float[64, 64];
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    var r = Math.Sqrt(((x - 32) * (x - 32)) + ((y - 32) * (y - 32)));
                    var detail = i == 2 ? 0.2 * Math.Sin(x * 1.3) * Math.Cos(y * 1.1) : 0;
                    frames[i][y, x] = r < 20 ? (float)(0.6 + detail) : 0.02f;
                }
            }
        }
        var stream = new InMemoryFrameStream(frames, [.. Enumerable.Range(0, frames.Length).Select(i => t.AddSeconds(i))]);

        var best = await new FrameGrader(new GradientEnergyEstimator()).BestFrameTimeAsync(stream, TestContext.Current.CancellationToken);

        best.ShouldBe(t.AddSeconds(2));
        (await new FrameGrader(new GradientEnergyEstimator()).BestFrameTimeAsync(new InMemoryFrameStream(frames), TestContext.Current.CancellationToken))
            .ShouldBeNull("a capture without frame times has no instant to give");
    }
}
