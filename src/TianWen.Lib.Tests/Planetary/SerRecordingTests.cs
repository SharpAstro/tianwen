using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="SerRecording"/> (P5 part 5d of docs/plans/hardware-in-the-server.md, #934): every frame before its end,
/// in the camera's own shape, with when it arrived, read back by SER.Lib's own reader; and each way it ends.
/// </summary>
public class SerRecordingTests
{
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 9, 27, 21, 0, 0, TimeSpan.Zero);

    private static string NewPath() => Path.Combine(Directory.CreateTempSubdirectory("twser").FullName, "night", "capture.ser");

    /// <summary>A 16-bit Bayer mosaic whose pixel (x, y) reads <paramref name="seed"/> plus its position, in ADU.</summary>
    private static Image Mosaic(int seed, int width = 8, int height = 4, int bayerOffsetX = 1)
    {
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = seed + y * width + x + 0.4f;
            }
        }
        return new Image([plane], BitDepth.Int16, maxValue: seed + width * height, minValue: seed, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.RGGB, BayerOffsetX = bayerOffsetX });
    }

    private static SerRecording Recording(string path, TimeSpan duration) => new SerRecording(path, T0, T0 + duration, NullLogger.Instance);

    [Fact(Timeout = 30_000)]
    public async Task EveryFrameIsWrittenInTheCamerasOwnShapeWithWhenItArrived()
    {
        var path = NewPath();
        var recording = Recording(path, TimeSpan.FromMinutes(1));

        for (var i = 0; i < 3; i++)
        {
            recording.TryAppend(Mosaic(1000 * (i + 1)), T0.AddSeconds(i)).ShouldBeTrue();
        }
        recording.End("done");
        await recording.Completion.WaitAsync(TestContext.Current.CancellationToken);

        (recording.FramesWritten, recording.FramesDropped, recording.EndReason, recording.FailureReason).ShouldBe((3, 0, "done", (string?)null));
        using var reader = SerReader.Open(path);
        (reader.Width, reader.Height, reader.FrameCount, reader.PixelDepthPerPlane).ShouldBe((8, 4, 3, 16));
        reader.ColorId.ShouldBe(SerColorId.BayerGRBG, "the mosaic's own phase: red one pixel in");
        reader.Timestamps.ShouldBe([T0, T0.AddSeconds(1), T0.AddSeconds(2)]);
        var samples = new ushort[8 * 4];
        reader.ReadFrame16(2, samples);
        samples.ShouldBe(Enumerable.Range(0, 32).Select(i => (ushort)(3000 + i)).ToArray(), "whole ADU, rounded");
    }

    [Fact(Timeout = 30_000)]
    public async Task AFrameArrivingAtItsEndEndsIt()
    {
        var path = NewPath();
        var recording = Recording(path, TimeSpan.FromSeconds(2));

        recording.TryAppend(Mosaic(10), T0.AddSeconds(1)).ShouldBeTrue();
        recording.TryAppend(Mosaic(20), T0.AddSeconds(2)).ShouldBeFalse("its end is not recorded");
        recording.TryAppend(Mosaic(30), T0.AddSeconds(1.5)).ShouldBeFalse("nor anything after it has ended");
        await recording.Completion.WaitAsync(TestContext.Current.CancellationToken);

        (recording.IsRecording, recording.EndReason, recording.FramesWritten).ShouldBe((false, "its duration is over", 1));
        using var reader = SerReader.Open(path);
        reader.FrameCount.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task AFrameOfAnotherSizeEndsIt()
    {
        // A SER frame has one size: a window resized mid-recording ends it rather than writing a file no reader could read.
        var recording = Recording(NewPath(), TimeSpan.FromMinutes(1));

        recording.TryAppend(Mosaic(10), T0).ShouldBeTrue();
        recording.TryAppend(Mosaic(20, width: 16), T0.AddSeconds(1)).ShouldBeFalse();
        await recording.Completion.WaitAsync(TestContext.Current.CancellationToken);

        recording.EndReason.ShouldBe("the frames changed from 8x4 to 16x4");
        recording.FramesWritten.ShouldBe(1);
    }

    /// <summary>An 8-bit readout of the same mosaic: whole ADU 0 to 255, as a camera streaming in 8 bits hands over.</summary>
    private static Image EightBitMosaic(int seed)
    {
        var plane = new float[4, 8];
        for (var i = 0; i < 32; i++)
        {
            plane[i / 8, i % 8] = seed + i;
        }
        return new Image([plane], BitDepth.Int8, maxValue: seed + 31, minValue: seed, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.RGGB, SensorFullScaleAdu = byte.MaxValue });
    }

    [Fact(Timeout = 30_000)]
    public async Task AnEightBitStreamRecordsAnEightBitFile()
    {
        // The depth the camera streams in is the file's: 8 bits a sample, not 0..255 padded into 16.
        var path = NewPath();
        var recording = Recording(path, TimeSpan.FromMinutes(1));

        recording.TryAppend(EightBitMosaic(100), T0).ShouldBeTrue();
        recording.TryAppend(EightBitMosaic(200), T0.AddSeconds(1)).ShouldBeTrue();
        recording.End("done");
        await recording.Completion.WaitAsync(TestContext.Current.CancellationToken);

        using var reader = SerReader.Open(path);
        (reader.FrameCount, reader.PixelDepthPerPlane).ShouldBe((2, 8));
        var samples = new byte[32];
        reader.ReadFrameBytes(1, samples);
        samples.ShouldBe(Enumerable.Range(0, 32).Select(i => (byte)(200 + i)).ToArray());
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamSwitchedToAnotherDepthEndsIt()
    {
        // A SER file has one depth, as it has one size.
        var recording = Recording(NewPath(), TimeSpan.FromMinutes(1));

        recording.TryAppend(Mosaic(10, bayerOffsetX: 0), T0).ShouldBeTrue();
        recording.TryAppend(EightBitMosaic(20), T0.AddSeconds(1)).ShouldBeFalse();
        await recording.Completion.WaitAsync(TestContext.Current.CancellationToken);

        recording.EndReason.ShouldBe("the frames changed from 16 to 8 bits");
        recording.FramesWritten.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task AColourFrameIsInterleavedAndAUnitFrameIsScaledToSixteenBits()
    {
        var path = NewPath();
        var recording = Recording(path, TimeSpan.FromMinutes(1));
        float[,] Plane(float v) => new float[,] { { v, v }, { v, v } };
        var colour = new Image([Plane(0.25f), Plane(0.5f), Plane(1f)], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            new ImageMeta { SensorType = SensorType.Color }, samplesAreUnitReferred: true);

        recording.TryAppend(colour, T0).ShouldBeTrue();
        recording.End("done");
        await recording.Completion.WaitAsync(TestContext.Current.CancellationToken);

        using var reader = SerReader.Open(path);
        reader.ColorId.ShouldBe(SerColorId.Rgb);
        var samples = new ushort[2 * 2 * 3];
        reader.ReadFrame16(0, samples);
        samples.Take(3).ShouldBe([(ushort)16384, (ushort)32768, ushort.MaxValue], "R, G, B of the first pixel, side by side");
    }

    [Fact(Timeout = 30_000)]
    public async Task ARecordingThatNeverTookAFrameLeavesNoFile()
    {
        var path = NewPath();
        var recording = Recording(path, TimeSpan.FromMinutes(1));

        recording.End("it was stopped");
        await recording.Completion.WaitAsync(TestContext.Current.CancellationToken);

        File.Exists(path).ShouldBeFalse("a SER of no frame, and no frame size, is nothing to keep");
    }
}
