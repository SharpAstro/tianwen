using CommunityToolkit.HighPerformance;
using nom.tam.fits;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Tests;

public static class SharedTestData
{
    internal const string BD_16_1591s_Enc = "\u00C1Av(fTnLH";
    internal const string PSR_J2144_3933s_Enc = "\u00C1AQAdywXD";
    internal const string PSR_B0633_17n_Enc = "\u00C1AFtItjtC";
    internal const string PSR_J0002_6216n_Enc = "\u00C1AXL@Q3uC";

    private static readonly ConcurrentDictionary<string, Image> ImageCache = [];
    private static readonly Assembly SharedTestDataAssembly = typeof(SharedTestData).Assembly;

    internal static async Task<Image> ExtractGZippedFitsImageAsync(string name, bool isReadOnly = true, CancellationToken cancellationToken = default)
    {
        if (!isReadOnly)
        {
            return ReadImageFromEmbeddedResourceStream(name);
        }

        if (ImageCache.TryGetValue(name, out var image))
        {
            return image;
        }
        
        var imageFile = await WriteEphemeralUseTempFileAsync($"{name}.tianwen-image", 
            async (tempFile, ct) =>
            {
                image = ReadImageFromEmbeddedResourceStream(name);
                await using var outStream = File.OpenWrite(tempFile);
                await image.WriteStreamAsync(outStream, ct);
            },
            cancellationToken
        );

        if (image is null)
        {
            await using var inStream = File.OpenRead(imageFile);
            image = await Image.FromStreamAsync(inStream, cancellationToken);

            ImageCache.TryAdd(name, image);
        }

        return image;

    }

    private static Image ReadImageFromEmbeddedResourceStream(string name)
    {
        if (OpenGZippedFitsFileStream(name) is not { } inStream)
        {
            throw new ArgumentException($"Missing test data {name}", nameof(name));
        }

        using (inStream)
        {
            return Image.TryReadFitsFile(new Fits(inStream, true), out var image)
                ? image
                : throw new InvalidDataException($"Failed to read FITS image from test data {name}");
        }
    }

    internal static readonly IReadOnlyDictionary<string, (ImageDim ImageDim, WCS WCS)> TestFileImageDimAndCoords =
        new Dictionary<string, (ImageDim imageDim, WCS WCS)>
        {
            ["PlateSolveTestFile"] = (new ImageDim(4.38934f, 1280, 960), new WCS(1.7632d  / 15.0d, -31.5193d)),
            ["image_file-snr-20_stars-28_1280x960x16"] = (new ImageDim(5.6f, 1280, 960), new WCS(337.264d / 15.0d, -22.918d))
        };

    internal static async Task<string> ExtractGZippedFitsFileAsync(string name, CancellationToken cancellationToken = default)
    {
        if (OpenGZippedFitsFileStream(name) is not { } inStream)
        {
            throw new ArgumentException($"Missing test data {name}", nameof(name));
        }

        var fileName = $"{name}_{inStream.Length}.fits";
        return await WriteEphemeralUseTempFileAsync(fileName, 
            async (tempFile, ct) =>
            {
                await using var outStream = new FileStream(tempFile, new FileStreamOptions
                {
                    Options = FileOptions.Asynchronous,
                    Access = FileAccess.Write,
                    Mode = FileMode.Create,
                    Share = FileShare.None
                });
                await using var gzipStream = new GZipStream(inStream, CompressionMode.Decompress, false);
                await gzipStream.CopyToAsync(outStream, 1024 * 10, ct);
            },
            cancellationToken
        );
    }

    internal static string CreateTempTestOutputDir()
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            TestDataRoot
        ));

        return dir.FullName;
    }
    
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<string> WriteEphemeralUseTempFileAsync(string fileName, Func<string, CancellationToken, ValueTask> fileOperation, CancellationToken cancellationToken = default)
    {
        var tempOutputDir = CreateTempTestOutputDir();

        var fullPath = Path.Combine(tempOutputDir, fileName);

        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        using var @lock = await FileLocks.GetOrAdd(fileName, _ => new SemaphoreSlim(1, 1)).AcquireLockAsync(cancellationToken);
        
        // Double-check after acquiring the lock
        if (File.Exists(fullPath))
        {
            return fullPath;
        }
        
        var tempFile = $"{fullPath}_{Guid.NewGuid():D}.tmp";

        await fileOperation(tempFile, cancellationToken);
        
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

        return fullPath;
    }

    private static string TestDataRoot
    {
        get
        {
            var name = SharedTestDataAssembly.GetName();
            const string typeName = nameof(SharedTestData);

            var date = $"{DateTimeOffset.Now.Date:yyyyMMdd}";
                
            if (!string.IsNullOrEmpty(name.Name))
            {
                return Path.Combine(name.Name, date, typeName);
            }
            else if (name.FullName.IndexOf(',') is var comma and > 0)
            {
                return Path.Combine(name.FullName[..comma], date, typeName);
            }
            else
            {
                return Path.Combine(date, typeName);
            }
        }
    }

    private static Stream? OpenGZippedFitsFileStream(string name) => OpenEmbeddedFileStream(name + ".fits.gz");

    internal static Stream? OpenEmbeddedFileStream(string nameWithExt)
    {
        var embeddedFiles = SharedTestDataAssembly.GetManifestResourceNames();
        var embeddedTestFile = embeddedFiles.FirstOrDefault(p => p.EndsWith($".{nameWithExt}"));
        return embeddedTestFile is not null ? SharedTestDataAssembly.GetManifestResourceStream(embeddedTestFile) : null;
    }

    internal static async Task<int[,]> ExtractGZippedImageData(string name, int width, int height)
    {
        var gzippedImageData = SharedTestDataAssembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith($".{name}_{width}x{height}.raw.gz"));

        if (gzippedImageData is not null && SharedTestDataAssembly.GetManifestResourceStream(gzippedImageData) is { } inStream)
        {
            var output = new int[width, height];
            await using var gzipStream = new GZipStream(inStream, CompressionMode.Decompress, false);
            await gzipStream.CopyToAsync(output.AsMemory().Cast<int, byte>().AsStream());

            return output;
        }

        throw new ArgumentException($"Missing test data {name}", nameof(name));
    }
}
