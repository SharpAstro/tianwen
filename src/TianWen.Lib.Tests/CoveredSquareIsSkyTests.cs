using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.FindBackgroundRegion"/> takes a COVERED square as sky however dim it is. It used to require a mean
/// luma above an absolute 0.001, which stood in for "not the canvas ring" and rejected real sky wherever the sky sits
/// below a thousandth of the frame's scale: a 3 nm L-Ultimate master of eta Car, scaled by an outlier peak of 463k ADU,
/// put its sky at 1.5e-4, the scan landed in the Ha, background neutralisation subtracted the nebula as if it were sky,
/// and red rendered as one flat value over the whole frame.
/// </summary>
public class CoveredSquareIsSkyTests
{
    private const int Size = 1024;
    private const int Ring = 64;

    [Fact]
    public void ADimSkyIsTheBackgroundNotTheBrighterNebula()
    {
        // The eta Car master's own levels, on its scale: sky 1.1e-4 / 2.5e-4 / 1.4e-4 (luma 2.1e-4, under the old floor),
        // a red nebula over the middle at a luma of 1.3e-3 (over it), and a zero canvas ring.
        var image = Frame(sky: (1.1e-4f, 2.5e-4f, 1.4e-4f), nebula: (2.0e-3f, 1.2e-3f, 6.0e-4f));

        var region = image.FindBackgroundRegion();
        var (perChannel, _) = image.ScanBackgroundRegion([0f, 0f, 0f]);

        InNebula(region.X, region.Y, region.Width).ShouldBeFalse($"the square at ({region.X}, {region.Y}) is in the nebula");
        InRing(region.X, region.Y, region.Width).ShouldBeFalse($"the square at ({region.X}, {region.Y}) touches the ring");
        perChannel[0].ShouldBe(1.1e-4f, tolerance: 1e-9f);
        perChannel[1].ShouldBe(2.5e-4f, tolerance: 1e-9f);
        perChannel[2].ShouldBe(1.4e-4f, tolerance: 1e-9f);
    }

    [Fact]
    public void WithNoCoveredSquareTheDarkestPositiveOneStillAnswers()
    {
        // Every square holds a zero (a raw frame whose dark sky clipped to zero in places): none is covered, so the scan
        // answers as it did before, with the darkest square above zero, and not with the margin's corner.
        var image = Frame(sky: (0.3f, 0.3f, 0.3f), nebula: (0.6f, 0.6f, 0.6f), ring: false, edit: static plane =>
        {
            // One darker patch, on the scan's grid (a 5 % margin of 51 px, then steps of 128), so the darkest
            // positive square is known.
            for (var y = 563; y < 563 + 32; y++)
            {
                for (var x = 51 + 128; x < 51 + 128 + 32; x++)
                {
                    plane[y, x] = 0.1f;
                }
            }
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x += 16)
                {
                    plane[y, x] = 0f;
                }
            }
        });

        var region = image.FindBackgroundRegion();

        (region.X, region.Y).ShouldBe((51 + 128, 563));
    }

    private static bool InNebula(int x, int y, int size) => x + size > 300 && x < 724 && y + size > 300 && y < 724;

    private static bool InRing(int x, int y, int size) => x < Ring || y < Ring || x + size > Size - Ring || y + size > Size - Ring;

    private static Image Frame((float R, float G, float B) sky, (float R, float G, float B) nebula, bool ring = true,
        System.Action<float[,]>? edit = null)
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[Size, Size];
            var skyLevel = c switch { 0 => sky.R, 1 => sky.G, _ => sky.B };
            var nebulaLevel = c switch { 0 => nebula.R, 1 => nebula.G, _ => nebula.B };
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    plane[y, x] = ring && InRing(x, y, 1) ? 0f
                        : InNebula(x, y, 1) ? nebulaLevel
                        : skyLevel;
                }
            }
            // One saturated star, so the frame's peak is its scale.
            plane[Size / 2, Size / 2] = 1f;
            edit?.Invoke(plane);
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
    }
}
