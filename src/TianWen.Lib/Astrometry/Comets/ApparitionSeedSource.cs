using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// Reads the current-apparition overlay baked at publish time, when the host has one.
/// </summary>
internal interface IApparitionSeedSource
{
    /// <summary>The baked overlay, or <c>null</c> when this host has none (the desktop default, and a
    /// dev server serving the app without the CI-baked asset).</summary>
    ValueTask<ApparitionCacheFile?> TryFetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fetches the publish-time apparition overlay from a same-origin URL, mirroring how
/// <see cref="SbdbCometSource"/> takes an endpoint override: a browser cannot reach JPL Horizons at all
/// (it sends no <c>Access-Control-Allow-Origin</c>), so the CI deploy resolves the per-object element
/// sets on a machine that can and bakes them as a static asset beside the bulk snapshot.
///
/// <para><b>A null URI is the whole desktop configuration.</b> Rather than a second implementation and a
/// branch at the repository, "this host has no seed" is just the absent URI -- the same shape as
/// <see cref="SbdbCometSource"/>, where null means the live API.</para>
///
/// <para><b>Every failure is a miss, deliberately.</b> A dev server without the CI-baked asset 404s here
/// exactly as it already does for <c>comets-sbdb.json</c>, and the app must behave the same way it would
/// on a host that never had a seed: keep the bulk elements, keep flagging positions approximate, and
/// leave the per-object fetch enabled. That is what keeps this asset-driven rather than host-sniffed --
/// nothing here asks WHERE it is running, so dev and production degrade along the same path.</para>
/// </summary>
internal sealed class ApparitionSeedSource : IApparitionSeedSource
{
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly HttpClient _httpClient;
    private readonly Uri? _seedUri;
    private readonly ILogger _logger;

    public ApparitionSeedSource(Uri? seedUri, ILogger<ApparitionSeedSource> logger)
        : this(s_httpClient, seedUri, logger)
    {
    }

    // Test seam, mirroring SbdbCometSource: lets a suite point at a stub handler.
    internal ApparitionSeedSource(HttpClient httpClient, Uri? seedUri, ILogger logger)
    {
        _httpClient = httpClient;
        _seedUri = seedUri;
        _logger = logger;
    }

    public async ValueTask<ApparitionCacheFile?> TryFetchAsync(CancellationToken cancellationToken)
    {
        if (_seedUri is null)
        {
            return null;
        }

        try
        {
            await using var stream = await _httpClient.GetStreamAsync(_seedUri, cancellationToken);
            return await JsonSerializer.DeserializeAsync(stream, SbdbJsonContext.Default.ApparitionCacheFile, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "No comet apparition seed at {Uri}; per-object Horizons refresh stays enabled", _seedUri);
            return null;
        }
    }
}
