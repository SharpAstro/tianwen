using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A frame a capture did not stamp (a zero tick in its SER trailer, #1409) is no time at all: never the earliest time of a capture,
/// which put its span, its middle and a de-rotation's epoch two thousand years away, and never a frame a de-rotated stack carries to
/// its epoch.
/// </summary>
public sealed class UnstampedFrameTests : IDisposable
{
    private const int Frames = 6;
    private static readonly DateTimeOffset T0 = new(2025, 1, 20, 3, 0, 0, TimeSpan.Zero);
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // A timed capture of a plain disk, 100 ms a frame, with frame `unstamped`'s tick zeroed in the trailer
    private string WriteWithAnUnstampedFrame(int unstamped)
    {
        const int n = 16;
        var path = Path.Combine(_folders.Create("twunstamped").FullName, "capture.ser");
        var frame = Enumerable.Range(0, n * n).Select(i => (ushort)(Math.Abs((i % n) - (n / 2)) + Math.Abs((i / n) - (n / 2)) < 5 ? 40000 : 500)).ToArray();
        PlanetarySerFixtures.WriteSer(path, n, n, SerColorId.Mono, [.. Enumerable.Repeat(frame, Frames)],
            [.. Enumerable.Range(0, Frames).Select(i => T0.AddMilliseconds(100 * i))]);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - ((Frames - unstamped) * sizeof(long))), 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void AnUnstampedFrameHasNoTimeAndTheSpanIsTheStampedFrames()
    {
        using var stream = new SerFrameStream(SerReader.Open(WriteWithAnUnstampedFrame(unstamped: 0)), splitBayer: false);

        stream.TimestampOf(0).ShouldBeNull();
        stream.TimestampOf(1).ShouldBe(T0.AddMilliseconds(100));
        stream.CaptureSpan.ShouldBe((T0.AddMilliseconds(100), T0.AddMilliseconds(500)));
    }

    [Fact]
    public void ADerotatedStackLeavesAnUnstampedFrameOut()
    {
        // carried from its own instant to the epoch, a frame with no instant cannot be placed; a capture with no times at all is
        // never de-rotated, so it keeps every grade
        using var stream = new SerFrameStream(SerReader.Open(WriteWithAnUnstampedFrame(unstamped: 3)), splitBayer: false);
        ImmutableArray<FrameGrade> grades = [.. Enumerable.Range(0, Frames).Select(i => new FrameGrade(i, 1f + i))];

        var timed = FrameGrader.WithoutUntimedFrames(grades, stream);

        timed.Select(g => g.Score).ShouldBe([1f, 2f, 3f, 0f, 5f, 6f]);
    }
}
