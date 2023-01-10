using Astap.Lib.Astrometry.Catalogs;
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

    internal const string BD_16_1591s_Enc = "ÁAv(fTnLH";
    internal const CatalogIndex Barnard_22 = (CatalogIndex)('B' << 21 | '0' << 14 | '2' << 7 | '2');
    internal const CatalogIndex BD_16_1591s = (CatalogIndex)(Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'v' << 42 | (ulong)'(' << 35 | (ulong)'f' << 28 | 'T' << 21 | 'n' << 14 | 'L' << 7 | 'H');
    internal const CatalogIndex C009 = (CatalogIndex)((ulong)'C' << 21 | '0' << 14 | '0' << 7 | '9');
    internal const CatalogIndex C041 = (CatalogIndex)((ulong)'C' << 21 | '0' << 14 | '4' << 7 | '1');
    internal const CatalogIndex C069 = (CatalogIndex)((ulong)'C' << 21 | '0' << 14 | '6' << 7 | '9');
    internal const CatalogIndex C092 = (CatalogIndex)((ulong)'C' << 21 | '0' << 14 | '9' << 7 | '2');
    internal const CatalogIndex C099 = (CatalogIndex)((ulong)'C' << 21 | '0' << 14 | '9' << 7 | '9');
    internal const CatalogIndex Cr024 = (CatalogIndex)((ulong)'C' << 28 | 'r' << 21 | '0' << 14 | '2' << 7 | '4');
    internal const CatalogIndex Cr050 = (CatalogIndex)((ulong)'C' << 28 | 'r' << 21 | '0' << 14 | '5' << 7 | '0');
    internal const CatalogIndex Cr360 = (CatalogIndex)((ulong)'C' << 28 | 'r' << 21 | '3' << 14 | '6' << 7 | '0');
    internal const CatalogIndex Cr399 = (CatalogIndex)((ulong)'C' << 28 | 'r' << 21 | '3' << 14 | '9' << 7 | '9');
    internal const CatalogIndex Ced0014 = (CatalogIndex)((ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '4');
    internal const CatalogIndex Ced0016 = (CatalogIndex)((ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '6');
    internal const CatalogIndex Ced0201 = (CatalogIndex)((ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '0' << 21 | '2' << 14 | '0' << 7 | '1');
    internal const CatalogIndex Ced135a = (CatalogIndex)((ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '1' << 21 | '3' << 14 | '5' << 7 | 'a');
    internal const CatalogIndex Ced135b = (CatalogIndex)((ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '1' << 21 | '3' << 14 | '5' << 7 | 'b');
    internal const CatalogIndex CG0004 = (CatalogIndex)((ulong)'C' << 35 | (ulong)'G' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '4');
    internal const CatalogIndex CG22B1 = (CatalogIndex)((ulong)'C' << 35 | (ulong)'G' << 28 | '2' << 21 | '2' << 14 | 'B' << 7 | '1');
    internal const CatalogIndex DG0017 = (CatalogIndex)((ulong)'D' << 35 | (ulong)'G' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '7');
    internal const CatalogIndex DG0018 = (CatalogIndex)((ulong)'D' << 35 | (ulong)'G' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '8');
    internal const CatalogIndex DOBASHI_0222 = (CatalogIndex)((ulong)'D' << 42 | (ulong)'o' << 35 | (ulong)'0' << 28 | '0' << 21 | '2' << 14 | '2' << 7 | '2');
    internal const CatalogIndex GUM016 = (CatalogIndex)((ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '1' << 7 | '6');
    internal const CatalogIndex GUM020 = (CatalogIndex)((ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '2' << 7 | '0');
    internal const CatalogIndex GUM033 = (CatalogIndex)((ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '3' << 7 | '3');
    internal const CatalogIndex GUM052 = (CatalogIndex)((ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '5' << 7 | '2');
    internal const CatalogIndex GUM060 = (CatalogIndex)((ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '6' << 7 | '0');
    internal const CatalogIndex HR0897 = (CatalogIndex)((ulong)'H' << 35 | (ulong)'R' << 28 | '0' << 21 | '8' << 14 | '9' << 7 | '7');
    internal const CatalogIndex HR0264 = (CatalogIndex)((ulong)'H' << 35 | (ulong)'R' << 28 | '0' << 21 | '2' << 14 | '6' << 7 | '4');
    internal const CatalogIndex HR1084 = (CatalogIndex)((ulong)'H' << 35 | (ulong)'R' << 28 | '1' << 21 | '0' << 14 | '8' << 7 | '4');
    internal const CatalogIndex HR1142 = (CatalogIndex)((ulong)'H' << 35 | (ulong)'R' << 28 | '1' << 21 | '1' << 14 | '4' << 7 | '2');
    internal const CatalogIndex IC0458 = (CatalogIndex)((ulong)'I' << 28 | '0' << 21 | (ulong)'4' << 14 | (ulong)'5' << 7 | '8');
    internal const CatalogIndex IC0715NW = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'1' << 28 | '5' << 21 | '_' << 14 | 'N' << 7 | 'W');
    internal const CatalogIndex IC0720_NED02 = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'2' << 28 | '0' << 21 | 'N' << 14 | '0' << 7 | '2');
    internal const CatalogIndex IC0048 = (CatalogIndex)((ulong)'I' << 28 | '0' << 21 | '0' << 14 | '4' << 7 | '8');
    internal const CatalogIndex IC0049 = (CatalogIndex)((ulong)'I' << 28 | '0' << 21 | '0' << 14 | '4' << 7 | '9');
    internal const CatalogIndex IC1000 = (CatalogIndex)((ulong)'I' << 28 | '1' << 21 | '0' << 14 | '0' << 7 | '0');
    internal const CatalogIndex IC1577 = (CatalogIndex)((ulong)'I' << 28 | '1' << 21 | '5' << 14 | '7' << 7 | '7');
    internal const CatalogIndex IC4703 = (CatalogIndex)((ulong)'I' << 28 | '4' << 21 | '7' << 14 | '0' << 7 | '3');
    internal const CatalogIndex LDN00146 = (CatalogIndex)((ulong)'L' << 49 | (ulong)'D' << 42 | (ulong)'N' << 35 | (ulong)'0' << 28 | '0' << 21 | '1' << 14 | '4' << 7 | '6');
    internal const CatalogIndex M020 = (CatalogIndex)('M' << 21 | '0' << 14 | '2' << 7 | '0');
    internal const CatalogIndex M040 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '0');
    internal const CatalogIndex M042 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '2');
    internal const CatalogIndex M045 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '5');
    internal const CatalogIndex M051 = (CatalogIndex)('M' << 21 | '0' << 14 | '5' << 7 | '1');
    internal const CatalogIndex M054 = (CatalogIndex)('M' << 21 | '0' << 14 | '5' << 7 | '4');
    internal const CatalogIndex M102 = (CatalogIndex)('M' << 21 | '1' << 14 | '0' << 7 | '2');
    internal const CatalogIndex Mel013 = (CatalogIndex)((ulong)'M' << 35 | (ulong)'e' << 28 | 'l' << 21 | '0' << 14 | '1' << 7 | '3');
    internal const CatalogIndex Mel022 = (CatalogIndex)((ulong)'M' << 35 | (ulong)'e' << 28 | 'l' << 21 | '0' << 14 | '2' << 7 | '2');
    internal const CatalogIndex Mel025 = (CatalogIndex)((ulong)'M' << 35 | (ulong)'e' << 28 | 'l' << 21 | '0' << 14 | '2' << 7 | '5');
    internal const CatalogIndex NGC0056 = (CatalogIndex)((ulong)'N' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '6');
    internal const CatalogIndex NGC0526_B = (CatalogIndex)((ulong)'N' << 42 | (ulong)'0' << 35 | (ulong)'5' << 28 | '2' << 21 | '6' << 14 | '_' << 7 | 'B');
    internal const CatalogIndex NGC0869 = (CatalogIndex)((ulong)'N' << 28 | '0' << 21 | '8' << 14 | '6' << 7 | '9');
    internal const CatalogIndex NGC1530_A = (CatalogIndex)((ulong)'N' << 42 | (ulong)'1' << 35 | (ulong)'5' << 28 | '3' << 21 | '0' << 14 | '_' << 7 | 'A');
    internal const CatalogIndex NGC1333 = (CatalogIndex)((ulong)'N' << 28 | '1' << 21 | '3' << 14 | '3' << 7 | '3');
    internal const CatalogIndex NGC1976 = (CatalogIndex)((ulong)'N' << 28 | '1' << 21 | '9' << 14 | '7' << 7 | '6');
    internal const CatalogIndex NGC2070 = (CatalogIndex)((ulong)'N' << 28 | '2' << 21 | '0' << 14 | '7' << 7 | '0');
    internal const CatalogIndex NGC3372 = (CatalogIndex)((ulong)'N' << 28 | '3' << 21 | '3' << 14 | '7' << 7 | '2');
    internal const CatalogIndex NGC4038 = (CatalogIndex)((ulong)'N' << 28 | '4' << 21 | '0' << 14 | '3' << 7 | '8');
    internal const CatalogIndex NGC4039 = (CatalogIndex)((ulong)'N' << 28 | '4' << 21 | '0' << 14 | '3' << 7 | '9');
    internal const CatalogIndex NGC4913 = (CatalogIndex)((ulong)'N' << 28 | '4' << 21 | '9' << 14 | '1' << 7 | '3');
    internal const CatalogIndex NGC5194 = (CatalogIndex)((ulong)'N' << 28 | '5' << 21 | '1' << 14 | '9' << 7 | '4');
    internal const CatalogIndex NGC5457 = (CatalogIndex)((ulong)'N' << 28 | '5' << 21 | '4' << 14 | '5' << 7 | '7');
    internal const CatalogIndex NGC6164 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '1' << 14 | '6' << 7 | '4');
    internal const CatalogIndex NGC6165 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '1' << 14 | '6' << 7 | '5');
    internal const CatalogIndex NGC6205 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '2' << 14 | '0' << 7 | '5');
    internal const CatalogIndex NGC6302 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '3' << 14 | '0' << 7 | '2');
    internal const CatalogIndex NGC6514 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '5' << 14 | '1' << 7 | '4');
    internal const CatalogIndex NGC6611 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '6' << 14 | '1' << 7 | '1');
    internal const CatalogIndex NGC6715 = (CatalogIndex)((ulong)'N' << 28 | '6' << 21 | '7' << 14 | '1' << 7 | '5');
    internal const CatalogIndex NGC7293 = (CatalogIndex)((ulong)'N' << 28 | '7' << 21 | '2' << 14 | '9' << 7 | '3');
    internal const CatalogIndex NGC7331 = (CatalogIndex)((ulong)'N' << 28 | '7' << 21 | '3' << 14 | '3' << 7 | '1');
    internal const CatalogIndex ESO056_115 = (CatalogIndex)((ulong)'E' << 49 | (ulong)'0' << 42 | (ulong)'5' << 35 | (ulong)'6' << 28 | '-' << 21 | '1' << 14 | '1' << 7 | '5');
    internal const CatalogIndex PSR_J2144_3933s = (CatalogIndex)(Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'Q' << 42 | (ulong)'A' << 35 | (ulong)'d' << 28 | 'y' << 21 | 'w' << 14 | 'X' << 7 | 'D');
    internal const string PSR_J2144_3933s_Enc = "ÁAQAdywXD";
    internal const CatalogIndex PSR_B0633_17n = (CatalogIndex)(Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'F' << 42 | (ulong)'t' << 35 | (ulong)'I' << 28 | 't' << 21 | 'j' << 14 | 't' << 7 | 'C');
    internal const string PSR_B0633_17n_Enc = "ÁAFtItjtC";
    internal const CatalogIndex PSR_J0002_6216n = (CatalogIndex)(Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'X' << 42 | (ulong)'L' << 35 | (ulong)'@' << 28 | 'Q' << 21 | '3' << 14 | 'u' << 7 | 'C');
    internal const string PSR_J0002_6216n_Enc = "ÁAXL@Q3uC";
    internal const CatalogIndex RCW_0036 = (CatalogIndex)((ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '0' << 14 | '3' << 7 | '6');
    internal const CatalogIndex RCW_0053 = (CatalogIndex)((ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '3');
    internal const CatalogIndex RCW_0107 = (CatalogIndex)((ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '1' << 14 | '0' << 7 | '7');
    internal const CatalogIndex RCW_0124 = (CatalogIndex)((ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '1' << 14 | '2' << 7 | '4');
    internal const CatalogIndex Sh2_0006 = (CatalogIndex)((ulong)'S' << 49 | (ulong)'h' << 42 | (ulong)'2' << 35 | (ulong)'-' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '6');
    internal const CatalogIndex Sh2_0155 = (CatalogIndex)((ulong)'S' << 49 | (ulong)'h' << 42 | (ulong)'2' << 35 | (ulong)'-' << 28 | '0' << 21 | '1' << 14 | '5' << 7 | '5');
    internal const CatalogIndex TrES03 = (CatalogIndex)((ulong)'T' << 35 | (ulong)'r' << 28 | 'E' << 21 | 'S' << 14 | '0' << 7 | '3');
    internal const CatalogIndex TwoM_J11400198_3152397n = (CatalogIndex)(Base91Enc | (ulong)'P' << 56 | (ulong)'6' << 49 | (ulong)'3' << 42 | (ulong)'A' << 35 | (ulong)'T' << 28 | 'J' << 21| ',' << 14 | 'y' << 7 | 'B');
    internal const CatalogIndex TwoM_J12015301_1852034s = (CatalogIndex)(Base91Enc | (ulong)']' << 56 | (ulong)'#' << 49 | (ulong)'f' << 42 | (ulong)'R' << 35 | (ulong)'t' << 28 | 'u' << 21 | 'K' << 14 | 'O' << 7 | 'L');
    internal const CatalogIndex TwoMX_J00185316_1035410n = (CatalogIndex)(Base91Enc | (ulong)'r' << 56 | (ulong)'1' << 49 | (ulong)'5' << 42 | (ulong)'|' << 35 | (ulong)'s' << 28 | '1' << 21 | 'V' << 14 | 'w' << 7 | 'H');
    internal const CatalogIndex TwoMX_J11380904_0936257s = (CatalogIndex)(Base91Enc | (ulong)'l' << 56 | (ulong)'Y' << 49 | (ulong)'<' << 42 | (ulong)'7' << 35 | (ulong)'i' << 28 | 'z' << 21 | 'o' << 14| 'u' << 7 | 'P');
    internal const CatalogIndex vdB0005 = (CatalogIndex)((ulong)'v' << 42 | (ulong)'d' << 35 | (ulong)'B' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '5');
    internal const CatalogIndex vdB0020 = (CatalogIndex)((ulong)'v' << 42 | (ulong)'d' << 35 | (ulong)'B' << 28 | '0' << 21 | '0' << 14 | '2' << 7 | '0');
    internal const CatalogIndex WDS_02583_4018s = (CatalogIndex)(Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'g' << 42 | (ulong)'4' << 35 | (ulong)'}' << 28 | '-' << 21 | '8' << 14 | '&' << 7 | 'G');
    internal const CatalogIndex WDS_23599_3112s = (CatalogIndex)(Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'+' << 42 | (ulong)'i' << 35 | (ulong)')' << 28 | ',' << 21 | 'N' << 14 | '%' << 7 | 'G');
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
