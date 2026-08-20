using Shouldly;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="AstroImageDocument.IsPreStretched"/> has to be right for FITS, because it is what makes
/// the viewer open an already-stretched frame WITHOUT applying the screen stretch on top of it.
///
/// <para>It was hardcoded <c>false</c> in <see cref="AstroImageDocument.AdoptImageAsync"/>, and every
/// FITS reaches the viewer through that method -- so the flag was never computed for FITS at all,
/// only for the TIFF/PNG/raw path, which runs its own detection. The visible bug was HorseHead.fits,
/// a scanned photographic plate (median 0.26 of full scale, nothing like a linear sub's few percent):
/// with the stretch on it rendered nearly black, and turning the stretch OFF was the only way to see
/// the image. Two stretches composed, which is not a subtle failure but did look like a stretch bug
/// rather than a detection one.</para>
///
/// <para>Pinned at the document level rather than on the detector, because the defect was not in any
/// detector -- <see cref="Image.DetectPreStretched"/> answers this correctly and always did. The
/// defect was that the FITS path never asked. A test on the detector alone would have passed
/// throughout.</para>
/// </summary>
[Collection("Imaging")]
public class PreStretchDetectionTests
{
    [Theory]
    // A linear sub sits near its black point: sky background at a few percent of full scale.
    [InlineData(0.03f, false)]
    [InlineData(0.15f, false)]
    // A scanned plate or an exported stretched image sits in the midtones. 0.26 is HorseHead.fits.
    [InlineData(0.26f, true)]
    [InlineData(0.45f, true)]
    public async Task AnAdoptedImageIsJudgedByItsMedian(float level, bool expected)
    {
        var image = Flat(64, 64, level);

        var doc = await AstroImageDocument.AdoptImageAsync(
            image, DebayerAlgorithm.None, wcs: null, filePath: "synthetic.fits", CancellationToken.None);

        doc.IsPreStretched.ShouldBe(expected);
    }

    /// <summary>
    /// 8 bits cannot hold an astronomical dynamic range, so the container answers the question and no
    /// statistic is consulted. This is the half of the rule that reaches a case pixel statistics
    /// cannot: a planetary frame is mostly empty sky whether it has been stretched or not, so its
    /// median reads low either way and the heuristic above judges it linear. Where the file is 8-bit
    /// -- a JPEG, a PNG, an 8-bit TIFF export -- that no longer matters.
    /// </summary>
    /// <para>Every case here is DARK, on purpose. A bright row would have to declare an integer depth
    /// with unit-scaled samples, and the statistics path then normalises against that container
    /// (65535 for Int16), so 0.40 arrives as ~6e-6 and the median test correctly does not fire. Such a
    /// row looks like it checks "both halves agree" and actually checks the scaling convention; the
    /// median half is covered by the Float32 theory above, where the declared scale and the samples
    /// match.</para>
    [Theory]
    // Median far below the 0.2 threshold, so ONLY the depth can decide.
    [InlineData(0.01f, BitDepth.Int8, true)]
    [InlineData(0.01f, BitDepth.Int16, false)]
    [InlineData(0.01f, BitDepth.Float32, false)]
    public async Task EightBitDataIsAlwaysJudgedPreStretched(float level, BitDepth depth, bool expected)
    {
        var doc = await AstroImageDocument.AdoptImageAsync(
            Flat(64, 64, level, depth), DebayerAlgorithm.None, wcs: null, filePath: "synthetic.fits",
            CancellationToken.None);

        doc.IsPreStretched.ShouldBe(expected);
    }

    /// <summary>
    /// A uniform plate at <paramref name="level"/> in [0,1], with a faint gradient so the median is
    /// not degenerate. Mono, because the flag is read from channel 0 either way.
    /// </summary>
    private static Image Flat(int width, int height, float level, BitDepth depth = BitDepth.Float32)
    {
        var data = new float[1][,];
        data[0] = new float[height, width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // +/- 0.01 around the level: enough spread for a meaningful MAD, far too little to move
            // the median across the 0.2 threshold either way.
            data[0][y, x] = level + ((x + y) % 3 - 1) * 0.01f;
        }

        // maxValue 1 declares the samples already unit-scaled, so AdoptImageAsync does not rescale
        // them and the median it measures is the level asked for here.
        return new Image(data, depth, 1.0f, 0f, 0f, new ImageMeta());
    }
}
