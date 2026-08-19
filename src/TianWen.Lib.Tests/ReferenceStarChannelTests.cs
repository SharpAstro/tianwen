using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Star detection on a colour image measures GREEN, not channel 0.
/// </summary>
/// <remarks>
/// <para>Channel 0 is red, and red is where the emission lives. On a real Bubble Nebula master red's
/// MAD was 12.2 against 6.2 / 6.4 for green and blue, and a plate solve from red matched 1 of 102
/// detections against the catalog -- rejected as noise -- while green matched 10 of 109 and blue 11 of
/// 116, agreeing on the answer to about an arcsecond. Same file, same solver, only the channel.</para>
/// <para>The choice already existed as <c>DatasetPsfNoiseReport.ReferenceChannel</c> and applied to
/// nothing else; the plate solver, the viewer's star overlay, the CLI, the stacker and the session all
/// still detected in red. It lives on <see cref="Image"/> now so there is one owner.</para>
/// </remarks>
[Collection("Imaging")]
public class ReferenceStarChannelTests(ITestOutputHelper output)
{
    private const int Size = 200;
    private const float Sky = 0.002f;
    private const float NoiseSpan = 0.0012f;
    private const float Sigma = 1.7f;
    private const float StarAmplitude = 0.25f;

    private static readonly (float X, float Y)[] Stars =
    [
        (50.4f, 60.3f), (140.6f, 55.2f), (90.5f, 130.7f), (160.2f, 150.6f), (40.8f, 150.1f),
        (110.3f, 90.9f), (70.7f, 40.2f), (150.9f, 100.4f),
    ];

    private static ImageMeta Meta() => new ImageMeta(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(150),
        FrameType.Light, "", 2.9f, 2.9f, 1180, -1, Filter.None, 1, 1,
        float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    /// <summary>
    /// Three channels sharing one star field. RED additionally carries lumpy emission -- broad blobs
    /// on the scale of a small nebula, which is what a detector tuned for point sources reports as
    /// stars.
    /// </summary>
    private static Image BuildEmissionHeavyRed()
    {
        var rng = new Random(42);
        var twoSigmaSq = 2f * Sigma * Sigma;

        // Nebulosity: a handful of wide, bright blobs, none of them where a star is.
        (float X, float Y, float A, float S)[] blobs =
        [
            (30f, 100f, 0.10f, 14f), (120f, 30f, 0.12f, 17f), (175f, 75f, 0.09f, 12f),
            (85f, 175f, 0.11f, 16f), (60f, 85f, 0.08f, 13f), (145f, 190f, 0.10f, 15f),
        ];

        var planes = new float[3][,];
        var max = 0f;
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var v = Sky + (float)(rng.NextDouble() - 0.5) * NoiseSpan;
                    foreach (var (sx, sy) in Stars)
                    {
                        var dx = x - sx;
                        var dy = y - sy;
                        v += StarAmplitude * MathF.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
                    }

                    if (c == 0)
                    {
                        foreach (var (bx, by, amp, bs) in blobs)
                        {
                            var dx = x - bx;
                            var dy = y - by;
                            v += amp * MathF.Exp(-(dx * dx + dy * dy) / (2f * bs * bs));
                        }
                    }

                    planes[c][y, x] = v;
                    if (v > max)
                    {
                        max = v;
                    }
                }
            }
        }

        return new Image(planes, BitDepth.Float32, max, 0f, 0f, Meta());
    }

    /// <summary>How many planted stars a list actually found.</summary>
    private static int Recovered(StarList stars)
    {
        var found = 0;
        foreach (var (sx, sy) in Stars)
        {
            foreach (var star in stars)
            {
                if (MathF.Abs(star.XCentroid - sx) < 2f && MathF.Abs(star.YCentroid - sy) < 2f)
                {
                    found++;
                    break;
                }
            }
        }
        return found;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    public void GreenOnColourAndChannelZeroOtherwise(int channelCount, int expected)
        => Image.ReferenceStarChannelFor(channelCount).ShouldBe(expected);

    [Fact]
    public void AMonochromeOrMosaicFrameIsUnaffected()
    {
        // The reason routing every call site through this was safe: a camera sub is mono or a
        // 1-channel Bayer MOSAIC, so it keeps detecting exactly where it always did.
        var plane = new float[2, 2];
        new Image([plane], BitDepth.Float32, 1f, 0f, 0f, Meta()).ReferenceStarChannel.ShouldBe(0);
    }

    [Fact]
    public async Task EmissionInRedDoesNotBecomeStarsWhenTheReferenceChannelIsUsed()
    {
        var image = BuildEmissionHeavyRed();
        var ct = TestContext.Current.CancellationToken;

        image.ReferenceStarChannel.ShouldBe(1);

        var red = await image.FindStarsAsync(0, snrMin: 10f, maxStars: 500, cancellationToken: ct);
        var reference = await image.FindStarsAsync(
            image.ReferenceStarChannel, snrMin: 10f, maxStars: 500, cancellationToken: ct);

        var redFound = Recovered(red);
        var referenceFound = Recovered(reference);
        output.WriteLine($"red       {red.Count} detections, {redFound}/{Stars.Length} planted stars recovered");
        output.WriteLine($"reference {reference.Count} detections, {referenceFound}/{Stars.Length} planted stars recovered");

        // Green sees the whole star field.
        referenceFound.ShouldBe(Stars.Length);
        reference.Count.ShouldBe(Stars.Length);

        // Red sees fewer of them. Note the DIRECTION, which is the counter-intuitive part and was
        // worth measuring rather than assuming: emission does not mainly add spurious detections, it
        // lifts the local background so the threshold rises and real stars drop out. The real frame
        // behaves the same way -- red detected 102 against green's 109 and blue's 116, and matched 1
        // of them to the catalog against 10 and 11.
        redFound.ShouldBeLessThan(referenceFound);
    }
}
