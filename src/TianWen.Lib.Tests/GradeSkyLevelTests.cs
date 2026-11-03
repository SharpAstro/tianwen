using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The sky level a grade carries is the star-masked median in the file's own counts, through the path the viewer takes:
/// the image <see cref="AstroImageDocument.AdoptImageAsync"/> has already scaled in place to [0, 1] by the frame's own observed
/// peak. Read as a fraction of that peak, two frames of one sky differ by whatever their brightest star did (#1975 review).
/// </summary>
[Collection("UI")]
public sealed class GradeSkyLevelTests
{
    private const int W = 128, H = 96;
    private const float Sky = 1000f;

    private static Image Frame(float starPeak)
    {
        var rng = new Random(1975);
        var plane = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                plane[y, x] = Sky + (float)((rng.NextDouble() * 20.0) - 10.0);
            }
        }

        (int X, int Y)[] centres = [(20, 20), (60, 30), (100, 25), (30, 70), (80, 65), (110, 80), (50, 50), (90, 45)];
        foreach (var (cx, cy) in centres)
        {
            for (var dy = -6; dy <= 6; dy++)
            {
                for (var dx = -6; dx <= 6; dx++)
                {
                    plane[cy + dy, cx + dx] += starPeak * MathF.Exp(-((dx * dx) + (dy * dy)) / (2f * 1.5f * 1.5f));
                }
            }
        }

        return new Image([plane], BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("sky", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(30), FrameType.Light, "",
                3.76f, 3.76f, 135, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN));
    }

    private static async Task<LightGrade> GradeAsync(float starPeak)
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await AstroImageDocument.AdoptImageAsync(Frame(starPeak), DebayerAlgorithm.None, cancellationToken: ct);
        LightGrade? grade = null;
        await document.DetectStarsAsync(ct, whileResident: (image, stars) => grade = LightGrading.Grade(image, stars, document.FullScaleAdu));
        return grade.ShouldNotBeNull("the planted stars must be found");
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoFramesOfOneSkyReadTheSameLevelWhateverTheirBrightestStar()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var faint = await GradeAsync(starPeak: 15_000f);
        var bright = await GradeAsync(starPeak: 50_000f);

        faint.SkyLevel.ShouldBe(Sky, Sky * 0.03f, "the median in counts, not a fraction of the frame's peak");
        bright.SkyLevel.ShouldBe(Sky, Sky * 0.03f);
        (bright.SkyLevel / faint.SkyLevel).ShouldBe(1f, 0.03f, "a 3x difference in peak must not read as a 3x difference in sky");
    }
}
