using Astap.Lib.Astrometry;
using Astap.Lib.Imaging;
using CommunityToolkit.HighPerformance;
using nom.tam.fits;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Astap.Lib.Tests;

public static class SharedTestData
{
    internal const ulong Base91Enc = 1UL << 63;

    internal const CatalogIndex C041 = (CatalogIndex)((ulong)'C' << 21 | '0' << 14 | '4' << 7 | '1');
    internal const CatalogIndex C099 = (CatalogIndex)((ulong)'C' << 21 | '0' << 14 | '9' << 7 | '9');
    internal const CatalogIndex HR0897 = (CatalogIndex)((ulong)'H' << 35 | (ulong)'R' << 28 | '0' << 21 | '8' << 14 | '9' << 7 | '7');
    internal const CatalogIndex IC0458 = (CatalogIndex)((ulong)'I' << 28 | '0' << 21 | (ulong)'4' << 14 | (ulong)'5' << 7 | '8');
    internal const CatalogIndex IC0715NW = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'1' << 28 | '5' << 21 | '_' << 14 | 'N' << 7 | 'W');
    internal const CatalogIndex IC0720_NED02 = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'2' << 28 | '0' << 21 | 'N' << 14 | '0' << 7 | '2');
    internal const CatalogIndex IC0048 = (CatalogIndex)((ulong)'I' << 28 | '0' << 21 | '0' << 14 | '4' << 7 | '8');
    internal const CatalogIndex IC0049 = (CatalogIndex)((ulong)'I' << 28 | '0' << 21 | '0' << 14 | '4' << 7 | '9');
    internal const CatalogIndex IC1000 = (CatalogIndex)((ulong)'I' << 28 | '1' << 21 | '0' << 14 | '0' << 7 | '0');
    internal const CatalogIndex IC1577 = (CatalogIndex)((ulong)'I' << 28 | '1' << 21 | '5' << 14 | '7' << 7 | '7');
    internal const CatalogIndex IC4703 = (CatalogIndex)((ulong)'I' << 28 | '4' << 21 | '7' << 14 | '0' << 7 | '3');
    internal const CatalogIndex M040 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '0');
    internal const CatalogIndex M042 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '2');
    internal const CatalogIndex M045 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '5');
    internal const CatalogIndex M051 = (CatalogIndex)('M' << 21 | '0' << 14 | '5' << 7 | '1');
    internal const CatalogIndex M102 = (CatalogIndex)('M' << 21 | '1' << 14 | '0' << 7 | '2');
    internal const CatalogIndex Mel022 = (CatalogIndex)((ulong)'M' << 35 | (ulong)'e' << 28 | 'l' << 21 | '0' << 14 | '2' << 7 | '2');
    internal const CatalogIndex NGC0056 = (CatalogIndex)((ulong)'N' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '6');
    internal const CatalogIndex NGC0526_B = (CatalogIndex)((ulong)'N' << 42 | (ulong)'0' << 35 | (ulong)'5' << 28 | '2' << 21 | '6' << 14 | '_' << 7 | 'B');
    internal const CatalogIndex NGC1530_A = (CatalogIndex)((ulong)'N' << 42 | (ulong)'1' << 35 | (ulong)'5' << 28 | '3' << 21 | '0' << 14 | '_' << 7 | 'A');
    internal const CatalogIndex NGC1976 = (CatalogIndex)((ulong)'N' << 28 | '1' << 21 | '9' << 14 | '7' << 7 | '6');
    internal const CatalogIndex NGC2070 = (CatalogIndex)((ulong)'N' << 28 | '2' << 21 | '0' << 14 | '7' << 7 | '0');
    internal const CatalogIndex NGC4038 = (CatalogIndex)((ulong)'N' << 28 | '4' << 21 | '0' << 14 | '3' << 7 | '8');
    internal const CatalogIndex NGC4039 = (CatalogIndex)((ulong)'N' << 28 | '4' << 21 | '0' << 14 | '3' << 7 | '9');
    internal const CatalogIndex NGC4913 = (CatalogIndex)((ulong)'N' << 28 | '4' << 21 | '9' << 14 | '1' << 7 | '3');
    internal const CatalogIndex NGC5194 = (CatalogIndex)((ulong)'N' << 28 | '5' << 21 | '1' << 14 | '9' << 7 | '4');
    internal const CatalogIndex NGC5457 = (CatalogIndex)((ulong)'N' << 28 | '5' << 21 | '4' << 14 | '5' << 7 | '7');
    internal const CatalogIndex NGC6205 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '2' << 14 | '0' << 7 | '5');
    internal const CatalogIndex NGC6611 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '6' << 14 | '1' << 7 | '1');
    internal const CatalogIndex NGC7293 = (CatalogIndex)((ulong)'N' << 28 | '7' << 21 | '2' << 14 | '9' << 7 | '3');
    internal const CatalogIndex NGC7331 = (CatalogIndex)((ulong)'N' << 28 | '7' << 21 | '3' << 14 | '3' << 7 | '1');
    internal const CatalogIndex ESO056_115 = (CatalogIndex)((ulong)'E' << 49 | (ulong)'0' << 42 | (ulong)'5' << 35 | (ulong)'6' << 28 | '-' << 21 | '1' << 14 | '1' << 7 | '5');
    internal const CatalogIndex PSR_J2144_3933s = (CatalogIndex)(Base91Enc | (ulong)'F' << 42 | (ulong)'t' << 35 | (ulong)'P' << 28 | '+' << 21 | '+' << 14 | '4' << 7 | 'B');
    internal const CatalogIndex PSR_B0633_17n = (CatalogIndex)(Base91Enc | (ulong)',' << 42 | (ulong)'>' << 35 | (ulong)'Z' << 28 | '4' << 21 | 'g' << 14 | '4' << 7 | 'A');
    internal const CatalogIndex PSR_J0002_6216n = (CatalogIndex)((ulong)'Á' << 56 | (ulong)'A' << 49 | (ulong)'X' << 42 | (ulong)'L' << 35 | (ulong)'@' << 28 | 'Q' << 21 | '3' << 14 | 'u' << 7 | 'C');
    internal const CatalogIndex Sh2_006 = (CatalogIndex)((ulong)'S' << 42 | (ulong)'h' << 35 | (ulong)'2' << 28 | '-' << 21 | '0' << 14 | '0' << 7 | '6');
    internal const CatalogIndex TrES03 = (CatalogIndex)((ulong)'T' << 35 | (ulong)'r' << 28 | 'E' << 21 | 'S' << 14 | '0' << 7 | '3');
    internal const CatalogIndex TwoM_J11400198_3152397n = (CatalogIndex)(Base91Enc | (ulong)'P' << 56 | (ulong)'6' << 49 | (ulong)'3' << 42 | (ulong)'A' << 35 | (ulong)'T' << 28 | 'J' << 21| ',' << 14 | 'y' << 7 | 'B');
    internal const CatalogIndex TwoM_J12015301_1852034s = (CatalogIndex)(Base91Enc | (ulong)']' << 56 | (ulong)'#' << 49 | (ulong)'f' << 42 | (ulong)'R' << 35 | (ulong)'t' << 28 | 'u' << 21 | 'K' << 14 | 'O' << 7 | 'L');
    internal const CatalogIndex TwoMX_J00185316_1035410n = (CatalogIndex)(Base91Enc | (ulong)'r' << 56 | (ulong)'1' << 49 | (ulong)'5' << 42 | (ulong)'|' << 35 | (ulong)'s' << 28 | '1' << 21 | 'V' << 14 | 'w' << 7 | 'H');
    internal const CatalogIndex TwoMX_J11380904_0936257s = (CatalogIndex)(Base91Enc | (ulong)'l' << 56 | (ulong)'Y' << 49 | (ulong)'<' << 42 | (ulong)'7' << 35 | (ulong)'i' << 28 | 'z' << 21 | 'o' << 14| 'u' << 7 | 'P');
    internal const CatalogIndex XO0003 = (CatalogIndex)((ulong)'X' << 35 | (ulong)'O' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '3');
    internal const CatalogIndex XO002N = (CatalogIndex)((ulong)'X' << 35 | (ulong)'O' << 28 | '0' << 21 | '0' << 14 | '2' << 7 | 'N');

    internal static Image ExtractGZippedFitsImage(string name)
    {
        var assembly = typeof(SharedTestData).Assembly;
        var gzippedTestFile = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith($".{name}.fits.gz"));

        if (gzippedTestFile is not null && assembly.GetManifestResourceStream(gzippedTestFile) is Stream inStream)
        {
            using (inStream)
            {
                if (Image.TryReadFitsFile(new Fits(inStream, true), out var image))
                {
                    return image;
                }
            }
        }

        throw new ArgumentException($"Missing test data {name}", nameof(name));
    }

    internal static readonly IReadOnlyDictionary<string, (ImageDim imageDim, double ra, double dec)> TestFileImageDimAndCoords =
        new Dictionary<string, (ImageDim imageDim, double ra, double dec)>
        {
            ["PlateSolveTestFile"] = (new ImageDim(4.38934f, 1280, 960), 1.7632d, -31.5193d),
            ["image_file-snr-20_stars-28_1280x960x16"] = (new ImageDim(5.6f, 1280, 960), 337.264d, -22.918d)
        };

    internal static async Task<string> ExtractGZippedFitsFileAsync(string name)
    {
        var assembly = typeof(SharedTestData).Assembly;
        var gzippedTestFile = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith($".{name}.fits.gz"));

        if (gzippedTestFile is not null
            && assembly.GetManifestResourceStream(gzippedTestFile) is Stream inStream)
        {
            var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D") + ".fits");
            using var outStream = new FileStream(fileName, new FileStreamOptions
            {
                Options = FileOptions.Asynchronous,
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Share = FileShare.None
            });
            using var gzipStream = new GZipStream(inStream, CompressionMode.Decompress, false);
            await gzipStream.CopyToAsync(outStream, 1024 * 10);

            return fileName;
        }

        throw new ArgumentException($"Missing test data {name}", nameof(name));
    }

    internal static async Task<int[,]> ExtractGZippedImageData(string name, int width, int height)
    {
        var assembly = typeof(SharedTestData).Assembly;
        var gzippedImageData = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith($".{name}_{width}x{height}.raw.gz"));

        if (gzippedImageData is not null
            && assembly.GetManifestResourceStream(gzippedImageData) is Stream inStream)
        {
            var output = new int[width, height];
            using var gzipStream = new GZipStream(inStream, CompressionMode.Decompress, false);
            await gzipStream.CopyToAsync(output.AsMemory().Cast<int, byte>().AsStream());

            return output;
        }

        throw new ArgumentException($"Missing test data {name}", nameof(name));
    }
}
