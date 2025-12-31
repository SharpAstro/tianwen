using ImageMagick;
using System.Text;

namespace TianWen.Lib.CLI;

class SixelRenderer : IConsoleImageRenderer
{
    public string Render(IMagickImage<float> image, Percentage? widthScale = null)
    {
        byte[] sixels;

        if (widthScale is null || widthScale == new Percentage(100))
        {
            sixels = image.ToByteArray(MagickFormat.Sixel);
        }
        else
        {
            using var copy = image.Clone();
            copy.Scale(widthScale.Value);
            sixels = copy.ToByteArray(MagickFormat.Sixel);
        }

        return Encoding.ASCII.GetString(sixels);
    }
}
