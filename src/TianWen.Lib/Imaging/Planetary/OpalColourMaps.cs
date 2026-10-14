using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Tiff;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>One OPAL colour composite global map: which body, which apparition, where MAST serves it.</summary>
/// <param name="Body">The planet.</param>
/// <param name="Apparition">OPAL's name for the rotation set, such as <c>2025b</c>.</param>
/// <param name="Url">The <c>globalmap-small</c> TIFF: about 450 by 225, 8-bit RGB, uncompressed, served with
/// <c>Access-Control-Allow-Origin: *</c>, so the browser fetches the same URL.</param>
public readonly record struct OpalColourMap(CatalogIndex Body, string Apparition, string Url)
{
    /// <summary>The credit a texture from this map carries: its filters are not the eye's, so a colour rendering.</summary>
    public string Credit => $"OPAL (HST) {Apparition}, colour rendering";
}

/// <summary>
/// The OPAL colour composites the sky atlas upgrades its giants to (docs/plans/atlas-planet-detail.md, "A4: the textured disc"): a
/// small hard-coded table, never a directory listing, and the one decoder. OPAL's maps are in WEST longitude (System III for Jupiter
/// and Saturn), their left edge at 0 degrees and longitude falling to the right, as <see cref="PlanetMap"/> is; where Hubble never saw
/// the planet (a pole turned away, Saturn's ring shadow) the composite is black, and <see cref="PlanetColourMap.FromRgb8"/>'s fill
/// fills it.
/// </summary>
public static class OpalColourMaps
{
    private const string Root = "https://archive.stsci.edu/hlsps/opal/";

    /// <summary>The newest colour composite of <paramref name="body"/>, or false for a body OPAL does not observe.</summary>
    public static bool TryFor(CatalogIndex body, out OpalColourMap map)
    {
        map = body switch
        {
            CatalogIndex.Jupiter => new OpalColourMap(body, "2025b", Root + "cycle32/jupiter/hlsp_opal_hst_wfc3-uvis_jupiter-2025b_f395n-f502n-f631n_v1_globalmap-small.tif"),
            CatalogIndex.Saturn => new OpalColourMap(body, "2025b", Root + "cycle32/saturn/hlsp_opal_hst_wfc3-uvis_saturn-2025b_f395n-f502n-f631n_v1_globalmap-small.tif"),
            CatalogIndex.Uranus => new OpalColourMap(body, "2025a", Root + "cycle33/uranus/hlsp_opal_hst_wfc3-uvis_uranus-2025a_f657n-f547m-f467m_v1_globalmap-small.tif"),
            CatalogIndex.Neptune => new OpalColourMap(body, "2025c", Root + "cycle32/neptune/hlsp_opal_hst_wfc3-uvis_neptune-2025c_f467m-f547m-f657n_v1_globalmap-small.tif"),
            _ => default,
        };
        return map.Url is not null;
    }

    /// <summary>
    /// The map in a <c>globalmap-small</c> TIFF's bytes, its holes filled; null for a TIFF that is not 8-bit RGB (a changed product is
    /// a refusal, never a wrong texture). A corrupt file throws.
    /// </summary>
    public static PlanetColourMap? Decode(ReadOnlySpan<byte> tiff)
    {
        var document = TiffReader.Read(tiff);
        if (document.Pages.Count == 0 || document.Pages[0] is not { BitsPerSample: 8, SamplesPerPixel: >= 3 } page
            || page.Pixels.Length < page.Width * page.Height * page.SamplesPerPixel)
        {
            return null;
        }
        return PlanetColourMap.FromRgb8(page.Pixels, page.Width, page.Height, page.SamplesPerPixel, fillHoles: true);
    }
}

/// <summary>
/// Where the atlas's OPAL maps come from: the TIFF's bytes for a map, or null when the server refused it. A network failure faults the
/// task; how long to wait before asking again is the caller's decision (<c>PlanetTextures</c> cools it down).
/// </summary>
public interface IPlanetMapStore
{
    /// <summary>The map's TIFF bytes.</summary>
    Task<byte[]?> GetAsync(OpalColourMap map, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IPlanetMapStore"/> over <c>%LOCALAPPDATA%/TianWen/PlanetMaps</c>: a map is fetched once and kept (four files of about
/// 300 KB; OPAL never rewrites an apparition's map).
/// </summary>
internal sealed class PlanetMapStore : IPlanetMapStore
{
    private static readonly SocketsHttpHandler SharedHandler =
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    private static readonly HttpClient SharedHttp = CreateHttpClient();

    private readonly HttpClient _http;
    private readonly Func<DirectoryInfo> _cacheDirectory;
    private readonly ILogger _logger;

    public PlanetMapStore(IExternal external, ILogger<PlanetMapStore> logger)
        : this(SharedHttp, () => external.CreateSubDirectoryInAppDataFolder("PlanetMaps"), logger)
    {
    }

    /// <summary>Test seam: a canned-response client and a temporary directory.</summary>
    internal PlanetMapStore(HttpClient http, Func<DirectoryInfo> cacheDirectory, ILogger logger)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _logger = logger;
    }

    // Whether the bytes decode as an OPAL colour map, the one test of what is kept.
    private static bool IsMap(byte[] bytes)
    {
        try
        {
            return OpalColourMaps.Decode(bytes) is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TianWen (https://github.com/SharpAstro/tianwen)");
        return http;
    }

    public async Task<byte[]?> GetAsync(OpalColourMap map, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_cacheDirectory().FullName,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(map.Url)))[..32] + ".tif");
        // Every host on this machine shares the folder, so the file is read and written through SharedFile (CLAUDE.md, Runtime Data).
        if (await SharedFile.TryOpenReadAsync(path, cancellationToken) is { } cached)
        {
            byte[] kept;
            await using (cached)
            {
                using var memory = new MemoryStream();
                await cached.CopyToAsync(memory, cancellationToken);
                kept = memory.ToArray();
            }
            if (IsMap(kept))
            {
                return kept;
            }
            // Not a map (what an older build cached from a 200 page): deleted, so the ask after the caller's cooldown fetches again.
            _logger.LogInformation("Cached OPAL map {Path} does not decode; deleted", path);
            File.Delete(path);
            return null;
        }

        using var response = await _http.GetAsync(map.Url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("OPAL map {Url} answered {Status}", map.Url, (int)response.StatusCode);
            return null;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        // Only a map is kept: a 200 that is something else (a maintenance page) written here would be served for good.
        if (!IsMap(bytes))
        {
            _logger.LogInformation("OPAL map {Url} answered 200 with something that is not a map", map.Url);
            return null;
        }
        await SharedFile.WriteAsync(path, (stream, ct) => stream.WriteAsync(bytes, ct).AsTask(), cancellationToken);
        return bytes;
    }
}
