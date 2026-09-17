using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Jpeg;
using SharpAstro.Png;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>A decoded object picture: 8-bit RGBA, row-major, top row first.</summary>
public sealed record ObjectPicture(int Width, int Height, byte[] Rgba);

/// <summary>
/// Fetches an article's picture at a standard width, from a disk cache or Wikimedia, and decodes it. A desktop
/// service: the browser build asks the browser for the same <see cref="ObjectArticleImage.ThumbnailUrl"/>, which
/// fetches, caches and decodes natively.
/// </summary>
public interface IObjectPictureStore
{
    /// <summary>
    /// The picture at the standard width covering <paramref name="pixels"/>, or null when Wikimedia refused it
    /// or it is not a format this store decodes (PNG and JPEG are; every lead image the bake keeps is one of
    /// the two once a TIFF is asked for as its JPEG rendering). A network failure faults the task instead:
    /// what to do about it, and for how long, is the caller's decision.
    /// </summary>
    Task<ObjectPicture?> GetAsync(ObjectArticleImage image, int pixels, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IObjectPictureStore"/> over <c>%LOCALAPPDATA%/TianWen/ObjectImages</c>. A picture is fetched once
/// per standard width and kept: the files change rarely upstream, a thumbnail is about 60 KB (median, measured
/// over the baked set) and the large view about 370 KB, and the cache only grows with what someone looked at.
/// </summary>
internal sealed class ObjectPictureStore : IObjectPictureStore
{
    // Wikimedia's policy asks a client to name itself. Shared and static, as the comet sources do: the
    // fetches are few and small, and a per-call client would only churn sockets.
    private static readonly HttpClient SharedHttp = CreateHttpClient();

    private readonly HttpClient _http;
    private readonly Func<DirectoryInfo> _cacheDirectory;
    private readonly ILogger _logger;

    public ObjectPictureStore(IExternal external, ILogger<ObjectPictureStore> logger)
        : this(SharedHttp, () => external.CreateSubDirectoryInAppDataFolder("ObjectImages"), logger)
    {
    }

    /// <summary>Test seam: a canned-response client and a temporary directory.</summary>
    internal ObjectPictureStore(HttpClient http, Func<DirectoryInfo> cacheDirectory, ILogger logger)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _logger = logger;
    }

    private static HttpClient CreateHttpClient()
    {
        // Shared for the process, so its pooled connections need a lifetime: the default is infinite, under
        // which a connection is reused until the server drops it and DNS is consulted only for a new one,
        // pinning a long session to one Wikimedia edge address. Two minutes re-resolves and rotates.
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TianWen (https://github.com/SharpAstro/tianwen)");
        return http;
    }

    public async Task<ObjectPicture?> GetAsync(ObjectArticleImage image, int pixels, CancellationToken cancellationToken = default)
    {
        var url = image.ThumbnailUrl(pixels);
        var path = CachePath(url);

        byte[] encoded;
        if (File.Exists(path))
        {
            encoded = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        else
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Object picture {Url} answered {Status}", url, (int)response.StatusCode);
                return null;
            }

            encoded = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await WriteAtomicallyAsync(path, encoded, cancellationToken);
        }

        return Decode(encoded);
    }

    /// <summary>PNG or JPEG by signature; anything else is null. A corrupt file of either kind throws.</summary>
    internal static ObjectPicture? Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
        {
            var png = PngReader.Decode(encoded);
            return new ObjectPicture(png.Width, png.Height, png.ToRgba8());
        }

        if (encoded.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
        {
            var jpeg = JpegDecoder.Decode(encoded);
            return new ObjectPicture(jpeg.Width, jpeg.Height, jpeg.Pixels);
        }

        return null;
    }

    /// <summary>Keyed by the URL, which names the file AND the width, so each width is its own entry.</summary>
    private string CachePath(string url)
    {
        var extension = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
        return Path.Combine(_cacheDirectory().FullName, key + extension);
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }
}
