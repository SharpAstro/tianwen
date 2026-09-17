using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.ObservedRange"/> skips NaN wherever it sits, including the first pixel and a whole
/// plane of it, and agrees with a plain scalar scan at lengths that do and do not fill a vector.
/// </summary>
/// <remarks>
/// The scan used to be <c>TensorPrimitives.MaxNumber</c> / <c>MinNumber</c>, on the belief that they
/// skip NaN like IEEE 754 maxNum. Over a span holding a NaN they answered NaN, so every plane of a
/// drizzle master (NaN around its canvas) scanned to NaN and the master kept a false label. The
/// reference here is the obvious scalar loop, never the previous form, so a disagreement says which
/// side moved.
/// </remarks>
public class ObservedRangeTests
{
    private static (float Min, float Max) ScalarReference(float[][,] channels)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var plane in channels)
        {
            foreach (var v in plane)
            {
                if (!float.IsNaN(v))
                {
                    min = MathF.Min(min, v);
                    max = MathF.Max(max, v);
                }
            }
        }

        return (min, max);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(4, 4)]
    [InlineData(7, 13)]
    [InlineData(64, 64)]
    [InlineData(65, 63)]
    public void NaNAnywhereIsSkippedAtEveryLength(int height, int width)
    {
        var random = new Random(height * 1000 + width);
        var planes = new float[2][,];
        for (var c = 0; c < planes.Length; c++)
        {
            planes[c] = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    planes[c][y, x] = (float)(random.NextDouble() * 200.0 - 50.0);
                }
            }
        }

        // NaN in the first pixel of every plane, and scattered through it.
        planes[0][0, 0] = float.NaN;
        planes[1][0, 0] = float.NaN;
        planes[1][height / 2, width / 2] = float.NaN;
        planes[0][height - 1, width - 1] = float.NaN;
        if (height * width > 2)
        {
            planes[0][0, width - 1] = 1000f;
            planes[1][height - 1, 0] = -500f;
        }

        var expected = ScalarReference(planes);
        var actual = Image.ObservedRange(planes);

        actual.Max.ShouldBe(expected.Max);
        actual.Min.ShouldBe(expected.Min);
    }

    [Fact]
    public void APlaneOfOnlyNaNContributesNothing()
    {
        var allNaN = new float[8, 8];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                allNaN[y, x] = float.NaN;
            }
        }

        var numbers = new float[3, 3];
        numbers[1, 1] = 42f;
        numbers[2, 2] = -3f;

        Image.ObservedRange([allNaN, numbers]).ShouldBe((-3f, 42f));
        Image.ObservedRange([allNaN]).ShouldBe((float.MaxValue, float.MinValue),
            "no number at all answers the fold's identity, which a caller can recognise");
    }
}
