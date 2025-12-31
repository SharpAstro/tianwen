using ImageMagick;
using Pastel;
using System.Text;

namespace TianWen.Lib.CLI;

class AsciiBlockRender : IConsoleImageRenderer
{
    public string Render(IMagickImage<float> image, Percentage? widthScale = null)
    {
        var consoleWidth = Console.WindowWidth;
        var outputWidth = consoleWidth * (widthScale ?? new Percentage(100));

        // divides quantum depth color space into usable rgb values
        var correctionFactor = Quantum.Depth switch
        {
            16 => 1.0f/257f,// divides quantum depth color space into usable rgb values
            8 => 1f,
            var d => throw new NotSupportedException($"Quantum of size {d} is not supported")
        };

        using var copy = image.Clone();
        copy.Scale(new Percentage(100.0 * (outputWidth / (double)copy.Width)));
        copy.Scale(new Percentage(100), new Percentage(50));

        var sb = new StringBuilder();
        using var pixels = copy.GetPixels();
        var channels = pixels.Channels;
        for (var h = 0; h < copy.Height; h++)
        {
            var maxWidth = Math.Min(consoleWidth, copy.Width);
            for (var w = 0; w < maxWidth; w++)
            {
                var pixel = pixels.GetPixel(w, h);
                int r, g, b;
                if (channels == 3)
                {
                    r = (int)Math.Round(pixel.GetChannel(0) * correctionFactor);
                    g = (int)Math.Round(pixel.GetChannel(1) * correctionFactor);
                    b = (int)Math.Round(pixel.GetChannel(2) * correctionFactor);
                }
                else if (channels == 1)
                {
                    r = g = b = (int)Math.Round(pixel.GetChannel(0) * correctionFactor);
                }
                else
                {
                    throw new NotSupportedException($"Number of channels {channels} is not supported");
                }

                sb.Append(" ".PastelBg($"#{r:X2}{g:X2}{b:X2}"));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
