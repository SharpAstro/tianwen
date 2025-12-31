using ImageMagick;

namespace TianWen.Lib.CLI;

public interface IConsoleImageRenderer
{
    string Render(IMagickImage<float> image, Percentage? widthScale = null);
}
