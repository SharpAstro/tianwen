using CommunityToolkit.HighPerformance;
using nom.tam.fits;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Tests;

public static class SharedTestData
{
    internal const string BD_16_1591s_Enc = "\u00C1Av(fTnLH";
    internal const string PSR_J2144_3933s_Enc = "\u00C1AQAdywXD";
    internal const string PSR_B0633_17n_Enc = "\u00C1AFtItjtC";
    internal const string PSR_J0002_6216n_Enc = "\u00C1AXL@Q3uC";

    private static readonly ConcurrentDictionary<string, Image> _imageCache = [];

    internal static async Task<Image> ExtractGZippedFitsImageAsync(string name, bool isReadOnly = true)
    {
        if (isReadOnly)
        {
            if (_imageCache.TryGetValue(name, out var image))
            {
                return image;
            }


            var imageFile = await WriteEphemeralUseTempFileAsync($"{name}.tianwen-image", async tempFile =>
            {
                image = ReadImageFromEmbeddedResourceStream(name);
                using var outStream = File.OpenWrite(tempFile);
                await image.WriteStreamAsync(outStream);
            });

            if (image is null)
            {
                using var inStream = File.OpenRead(imageFile);
                image = await Image.FromStreamAsync(inStream);

                if (image is not null)
                {
                    _imageCache.TryAdd(name, image);
                }
                else
                {
                    throw new ArgumentException($"Failed to read image from {imageFile}", nameof(name));
                }
            }

            return image;
        }
        else
        {
            return ReadImageFromEmbeddedResourceStream(name);
        }
    }

    private static Image ReadImageFromEmbeddedResourceStream(string name)
    {
        if (OpenGZippedFitsFileStream(name) is { } inStream)
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

    internal static readonly IReadOnlyDictionary<string, (ImageDim ImageDim, WCS WCS)> TestFileImageDimAndCoords =
        new Dictionary<string, (ImageDim imageDim, WCS WCS)>
        {
            ["PlateSolveTestFile"] = (new ImageDim(4.38934f, 1280, 960), new WCS(1.7632d  / 15.0d, -31.5193d)),
            ["image_file-snr-20_stars-28_1280x960x16"] = (new ImageDim(5.6f, 1280, 960), new WCS(337.264d / 15.0d, -22.918d))
        };

    internal static async Task<string> ExtractGZippedFitsFileAsync(string name)
    {
        if (OpenGZippedFitsFileStream(name) is { } inStream)
        {
            var fileName = $"{name}_{inStream.Length}.fits";
            return await WriteEphemeralUseTempFileAsync(fileName, async (tempFile) =>
            {
                using var outStream = new FileStream(tempFile, new FileStreamOptions
                {
                    Options = FileOptions.Asynchronous,
                    Access = FileAccess.Write,
                    Mode = FileMode.Create,
                    Share = FileShare.None
                });
                using var gzipStream = new GZipStream(inStream, CompressionMode.Decompress, false);
                var length = inStream.Length;
                await gzipStream.CopyToAsync(outStream, 1024 * 10);
            });
        }

        throw new ArgumentException($"Missing test data {name}", nameof(name));
    }

    private static async Task<string> WriteEphemeralUseTempFileAsync(string fileName, Func<string, ValueTask> fileOperation)
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            AssemblyName(typeof(SharedTestData)),
            nameof(SharedTestData),
            $"{DateTimeOffset.Now.Date:yyyyMMdd}"
        ));

        var fullPath = Path.Combine(dir.FullName, fileName);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }
        else
        {
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            var tempFile = $"{fullPath}_{Guid.NewGuid():D}.tmp";

            await fileOperation(tempFile);

            if (!File.Exists(fullPath))
            {
                try
                {
                    File.Move(tempFile, fullPath);
                }
                // see https://learn.microsoft.com/en-us/dotnet/standard/io/handling-io-errors
                catch (IOException iox) when ((iox.HResult & 0x0000FFFF) is 0x80 or 0xB7)
                {
                    return fullPath;
                }
                catch (IOException) when (File.Exists(fullPath))
                {
                    return fullPath;
                }
                catch (IOException iox)
                {
                    throw new Exception($"Failed to move file {tempFile} to {fullPath}, code: {(iox.HResult & 0x0000FFFF):X}: {iox.Message}", iox);
                }
            }

            return fullPath;
        }
    }

    internal static string AssemblyName(Type type)
    {
        var name = type.Assembly.GetName();

        if (!string.IsNullOrEmpty(name.Name))
        {
            return name.Name;
        }
        else if (name.FullName.IndexOf(',') is { } comma and > 0)
        {
            return name.FullName[..comma];
        }
        else
        {
            return type.Name;
        }
    }

    private static Stream? OpenGZippedFitsFileStream(string name)
    {
        var assembly = typeof(SharedTestData).Assembly;
        var gzippedTestFile = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith($".{name}.fits.gz"));
        return gzippedTestFile is not null ? assembly.GetManifestResourceStream(gzippedTestFile) : null;
    }

    internal static async Task<int[,]> ExtractGZippedImageData(string name, int width, int height)
    {
        var assembly = typeof(SharedTestData).Assembly;
        var gzippedImageData = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith($".{name}_{width}x{height}.raw.gz"));

        if (gzippedImageData is not null && assembly.GetManifestResourceStream(gzippedImageData) is Stream inStream)
        {
            var output = new int[width, height];
            using var gzipStream = new GZipStream(inStream, CompressionMode.Decompress, false);
            await gzipStream.CopyToAsync(output.AsMemory().Cast<int, byte>().AsStream());

            return output;
        }

        throw new ArgumentException($"Missing test data {name}", nameof(name));
    }
}
