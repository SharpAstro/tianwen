using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="CloudLayer"/> exists so a session can be given three genuinely different kinds of bad
/// night, not three shades of one. These tests assert that difference rather than any particular
/// star count, because a count is a property of this fixture while the ORDERING is a property of the
/// model: at the same coverage, cirrus must cost least and the low decks must cost most.
/// </summary>
[Collection("Scheduling")]
public sealed class CloudLayerTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Seed = 42;
    private const double Exposure = 10.0;

    private static async Task<int> StarsAsync(double coverage, CloudLayer layer)
    {
        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 120, seed: Seed, noiseSeed: 1,
            cloudCoverage: coverage, cloudSeed: 77, cloudLayer: layer);

        var (min, max) = (float.MaxValue, float.MinValue);
        foreach (var v in data)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(Exposure),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return (await new Image([data], BitDepth.Float32, max, min, 0, meta)
            .FindStarsAsync(0, snrMin: 5, maxStars: 400,
                cancellationToken: TestContext.Current.CancellationToken)).Count;
    }

    /// <summary>
    /// The whole point of the enum, at two coverages so it is a trend and not one lucky pattern.
    /// </summary>
    [Theory]
    [InlineData(0.6)]
    [InlineData(0.8)]
    public async Task AtOneCoverageTheLayerDecidesHowMuchOfTheFieldSurvives(double coverage)
    {
        var clear = await StarsAsync(0.0, CloudLayer.Altocumulus);
        var cirrus = await StarsAsync(coverage, CloudLayer.Cirrus);
        var alto = await StarsAsync(coverage, CloudLayer.Altocumulus);
        var stratus = await StarsAsync(coverage, CloudLayer.Stratus);

        var report = $"clear={clear} cirrus={cirrus} alto={alto} stratus={stratus} at coverage {coverage:F2}";

        cirrus.ShouldBeGreaterThan(alto, $"thin ice cloud must cost less than a droplet deck ({report})");
        alto.ShouldBeGreaterThanOrEqualTo(stratus,
            $"a patchy mid deck leaves gaps a broad low deck does not ({report})");
    }

    /// <summary>
    /// Cirrus is the night that degrades without ending. It is the case a session should NOT react to,
    /// so it has to stay clearly above the condition-deterioration gate (default 0.5 of baseline).
    /// </summary>
    [Fact]
    public async Task CirrusThinsTheFieldWithoutClosingIt()
    {
        var clear = await StarsAsync(0.0, CloudLayer.Cirrus);
        var thick = await StarsAsync(0.95, CloudLayer.Cirrus);

        thick.ShouldBeLessThan(clear, $"cirrus must still cost something (clear={clear} covered={thick})");
        thick.ShouldBeGreaterThan(clear * 4 / 10,
            $"0.76 mag of ice cloud must not read as a closed sky (clear={clear} covered={thick})");
    }

    /// <summary>
    /// And the low decks are the opposite: where they sit, the sky is gone and no recovery is coming.
    /// Distinct from the <c>coverage &gt;= 1.0</c> blackout, which renders no stars at all.
    /// </summary>
    [Fact]
    public Task AnAltocumulusDeckClosesTheSkyOnceItIsNearlyComplete()
        => ADropletDeckClosesTheSkyAsync(CloudLayer.Altocumulus);

    [Fact]
    public Task AStratusDeckClosesTheSkyOnceItIsNearlyComplete()
        => ADropletDeckClosesTheSkyAsync(CloudLayer.Stratus);

    // Two facts over a private helper rather than a [Theory]: CloudLayer is internal, and an internal
    // type cannot be the parameter of the public method xUnit needs to discover (CS0051).
    private static async Task ADropletDeckClosesTheSkyAsync(CloudLayer layer)
    {
        var clear = await StarsAsync(0.0, layer);
        var closed = await StarsAsync(0.95, layer);

        closed.ShouldBeLessThanOrEqualTo(clear / 20,
            $"{layer} at 95% coverage must be all but starless (clear={clear} covered={closed})");
    }
}
