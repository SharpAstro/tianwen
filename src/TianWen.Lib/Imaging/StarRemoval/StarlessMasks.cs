using System.IO;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The one naming rule of a starless plate's two masks, beside the plate <c>&lt;stem&gt;_plate.fits</c>: where the builder
/// subtracted a star's model and where it filled pixels in. <c>tianwen dataset starless-plates</c> writes them
/// (<see cref="Sources.SourceDetectionWriter.MaskToImage"/>); a reader takes them back with
/// <see cref="Sources.SourceDetectionWriter.ReadMask"/>.
/// </summary>
public static class StarlessMasks
{
    /// <summary>The mask of the pixels a subtracted star's model reached.</summary>
    public static string SubtractedPathFor(string platesDir, string stem) => Path.Combine(platesDir, stem + "_plate.subtracted.fits.gz");

    /// <summary>The mask of the pixels the builder filled in.</summary>
    public static string InpaintedPathFor(string platesDir, string stem) => Path.Combine(platesDir, stem + "_plate.inpainted.fits.gz");
}
