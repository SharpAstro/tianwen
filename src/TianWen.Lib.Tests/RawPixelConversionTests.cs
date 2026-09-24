using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one-pass widening a camera download uses: every sample arrives as its unsigned value and the
/// range is the samples' own, whatever the length, so the vector loop and the scalar tail agree.
/// </summary>
public class RawPixelConversionTests
{
    // 0 and 1 never enter the vector loop; 7 to 17 straddle 8 and 16 lanes (a 128-bit register holds
    // 8 ushort or 16 byte lanes); 100003 is long and ends in a ragged tail on any register width.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(100003)]
    public void SixteenBitSamplesWidenToTheirUnsignedValues(int length)
    {
        var source = new ushort[length];
        var rng = new Random(length);
        for (var i = 0; i < length; i++)
        {
            source[i] = (ushort)rng.Next(0, 65536);
        }

        if (length > 2)
        {
            source[length / 2] = 65535;
            source[length - 1] = 32768;
        }

        var destination = new float[length];
        var (min, max) = RawPixelConversion.WidenToSingle(source, destination);

        for (var i = 0; i < length; i++)
        {
            destination[i].ShouldBe(source[i]);
        }

        (min, max).ShouldBe(length == 0 ? ((ushort)0, (ushort)0) : (source.Min(), source.Max()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(100003)]
    public void EightBitSamplesWidenToTheirValues(int length)
    {
        var source = new byte[length];
        new Random(length).NextBytes(source);
        var destination = new float[length];

        var (min, max) = RawPixelConversion.WidenToSingle(source, destination);

        for (var i = 0; i < length; i++)
        {
            destination[i].ShouldBe(source[i]);
        }

        (min, max).ShouldBe(length == 0 ? ((byte)0, (byte)0) : (source.Min(), source.Max()));
    }

    [Fact]
    public void ADestinationTooShortIsRefusedBeforeAnythingIsWritten()
    {
        var destination = new float[3];
        Should.Throw<ArgumentException>(() => RawPixelConversion.WidenToSingle(new ushort[4], destination));
        destination.ShouldAllBe(v => v == 0f);
    }
}
