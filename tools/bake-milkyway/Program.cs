using System;
using System.Diagnostics;
using System.IO;
using SharpAstro.Png;
using TianWen.Lib.Astrometry.Catalogs;

namespace BakeMilkyWay;

/// <summary>
/// Turns <c>milkyway.bgra.lz</c> into the opaque, alpha-premultiplied PNG the browser sky map loads.
/// See BakeMilkyWay.csproj for why it is derived from the committed file, and
/// <see cref="MilkyWayTextureFile.ToPremultipliedRgb"/> for why the PNG has no alpha channel.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: BakeMilkyWay <milkyway.bgra.lz> <output.png>");
            return 2;
        }

        var input = args[0];
        var output = args[1];
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"[bake-milkyway] input not found: {input}");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var texture = MilkyWayTextureFile.Decode(File.ReadAllBytes(input));
        var rgb = texture.ToPremultipliedRgb();
        var png = PngWriter.EncodeRgb8(rgb, texture.Width, texture.Height);

        // Verify before writing: the file the browser decodes must be these samples exactly, or the web
        // sky is a second, silently different source of truth.
        var check = PngReader.Decode(png);
        if (check.Width != texture.Width || check.Height != texture.Height || check.BitDepth != 8
            || check.ColorType != 2 || !check.Pixels.AsSpan().SequenceEqual(rgb))
        {
            Console.Error.WriteLine(
                $"[bake-milkyway] the encoded PNG does not decode back to the texture "
                + $"({check.Width}x{check.Height}, depth {check.BitDepth}, colour type {check.ColorType}); not writing");
            return 1;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(output, png);

        Console.WriteLine(
            $"[bake-milkyway] {input} ({texture.Width}x{texture.Height}) -> {output}: "
            + $"{png.Length:N0} bytes, verified, {sw.ElapsedMilliseconds} ms");
        return 0;
    }
}
