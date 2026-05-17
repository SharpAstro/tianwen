using System.Drawing;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class DebayerRegionTests
{
    private const string BayerFixture = "RGGB_frame_bx0_by0_top_down";

    /// <summary>
    /// Byte-equivalence test for sub-region AHD: debayering a strip via
    /// <see cref="Image.DebayerRegionIntoAsync"/> must produce the SAME pixel
    /// values inside that strip as a full-frame debayer would. Picks an
    /// interior strip (away from the canvas edges by AHD's totalRadius=4
    /// plus the homogeneityRadius=2 halo grown into Phase 1) so every pixel
    /// in the strip is produced by the algorithm proper, not by
    /// <c>ProcessEdgePixels</c>. The whole point of Phase 10.x is that the
    /// sub-region path stays a single AHD impl with one set of correctness
    /// guarantees -- this test enforces that invariant.
    /// </summary>
    [Fact]
    public async Task DebayerRegion_AHD_StripMatchesFullFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(BayerFixture, cancellationToken: ct);

        // Full-frame AHD reference.
        var full = await image.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
        full.ChannelCount.ShouldBe(3);

        // Sub-region path. Pick a 64-row strip well inside the interior so
        // we're comparing the algorithm proper, not edge fill.
        const int stripY = 200;
        const int stripH = 64;
        var sourceRect = new Rectangle(0, stripY, image.Width, stripH);

        // Pre-allocate full-canvas destination channels (same shape as full
        // frame); the sub-region pass only fills the strip rows + halo.
        var destChannels = new Channel[]
        {
            Channel.Create(image.Height, image.Width),
            Channel.Create(image.Height, image.Width),
            Channel.Create(image.Height, image.Width),
        };
        var region = await image.DebayerRegionIntoAsync(destChannels, DebayerAlgorithm.AHD, sourceRect, ct);

        // Inside the strip every pixel must match the full-frame output
        // bit-for-bit. The algorithm is deterministic and we expanded the
        // sub-region Phase 1 + Phase 3 windows to cover the strip's halo
        // neighbourhoods, so identical input -> identical output.
        for (var c = 0; c < 3; c++)
        {
            for (var y = stripY; y < stripY + stripH; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var fullPx = full[c, y, x];
                    var regionPx = region[c, y, x];
                    if (fullPx != regionPx)
                    {
                        // Detailed failure: tell us where + by how much before
                        // dumping the full pixel table.
                        regionPx.ShouldBe(fullPx,
                            $"channel {c}, pixel ({x}, {y}): sub-region != full-frame AHD");
                    }
                }
            }
        }
    }

    /// <summary>Halo growth: a small interior strip (4 rows) still produces
    /// byte-identical output. Stresses the bound-clamp math more than the
    /// 64-row test because the halo (homogeneityRadius=2) is half the
    /// strip's height.</summary>
    [Fact]
    public async Task DebayerRegion_AHD_TinyStripMatchesFullFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(BayerFixture, cancellationToken: ct);

        var full = await image.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);

        const int stripY = 500;
        const int stripH = 4;
        var sourceRect = new Rectangle(0, stripY, image.Width, stripH);

        var destChannels = new Channel[]
        {
            Channel.Create(image.Height, image.Width),
            Channel.Create(image.Height, image.Width),
            Channel.Create(image.Height, image.Width),
        };
        var region = await image.DebayerRegionIntoAsync(destChannels, DebayerAlgorithm.AHD, sourceRect, ct);

        for (var c = 0; c < 3; c++)
        {
            for (var y = stripY; y < stripY + stripH; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    region[c, y, x].ShouldBe(full[c, y, x]);
                }
            }
        }
    }
}
