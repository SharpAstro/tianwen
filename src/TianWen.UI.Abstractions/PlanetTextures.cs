using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The colour maps the sky atlas draws its planet discs from (docs/plans/atlas-planet-detail.md, "A4: the textured disc"): the offline
/// atlas (<see cref="PlanetTextureAtlas"/>) for every body, upgraded at runtime to OPAL's colour composite for the four giants when a
/// <see cref="Store"/> is given. Read on the render thread, written from background tasks: the maps are one immutable dictionary
/// replaced whole, and <see cref="Version"/> counts the replacements, so a sprite keyed on it is rebuilt when its texture changes.
/// Nothing here blocks: the atlas file and every OPAL fetch run on the thread pool, and <see cref="RequestRedraw"/> asks for the frame
/// that shows what landed.
/// </summary>
public sealed class PlanetTextures(Func<DateTimeOffset> utcNow, ILogger? logger = null)
{
    /// <summary>How long a failed OPAL fetch waits before it is tried again, as the comets' Horizons fetch does.</summary>
    public static readonly TimeSpan RetryCooldown = TimeSpan.FromHours(1);

    private ImmutableDictionary<CatalogIndex, (PlanetColourMap Map, string Credit)> _maps =
        ImmutableDictionary<CatalogIndex, (PlanetColourMap Map, string Credit)>.Empty;
    private readonly ConcurrentDictionary<CatalogIndex, DateTimeOffset> _failedAt = new();
    private readonly ConcurrentDictionary<CatalogIndex, byte> _inFlight = new();
    private readonly ConcurrentDictionary<CatalogIndex, byte> _upgraded = new();
    private int _version;
    private int _atlasRequested;
    private int _upgradeAttempts;

    /// <summary>Where OPAL's maps come from; null keeps the atlas, which is the host with no network service and every test.</summary>
    public IPlanetMapStore? Store { get; set; }

    /// <summary>Asks the host for a frame, once a texture has landed.</summary>
    public Action? RequestRedraw { get; set; }

    /// <summary>
    /// The atlas file a desktop host reads on first use; null for a host that hands the atlas over itself (<see cref="SetAtlas"/>), as
    /// the browser does after fetching it beside the page.
    /// </summary>
    public string? AtlasPath { get; set; } = Path.Combine(AppContext.BaseDirectory, PlanetTextureAtlas.FileName);

    /// <summary>Counts every change to the maps: part of a sprite's key.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>The OPAL fetch started last, for a test that waits on its outcome.</summary>
    internal Task? LastUpgrade { get; private set; }

    /// <summary>How many OPAL fetches have been started, for the cooldown's test.</summary>
    internal int UpgradeAttempts => Volatile.Read(ref _upgradeAttempts);

    /// <summary>
    /// Adopts the atlas from its PNG's bytes. A giant already upgraded to OPAL keeps its upgrade. Bytes that are not the atlas change
    /// nothing.
    /// </summary>
    public void SetAtlas(ReadOnlySpan<byte> png)
    {
        var atlas = PlanetTextureAtlas.Decode(png);
        if (atlas.IsEmpty)
        {
            logger?.LogWarning("Planet texture atlas did not decode; planets stay dots");
            return;
        }
        ImmutableInterlocked.Update(ref _maps, maps =>
        {
            var builder = maps.ToBuilder();
            foreach (var (body, map) in atlas)
            {
                if (!_upgraded.ContainsKey(body))
                {
                    builder[body] = (map, PlanetTextureAtlas.Credit(body));
                }
            }
            return builder.ToImmutable();
        });
        Interlocked.Increment(ref _version);
        RequestRedraw?.Invoke();
    }

    /// <summary>
    /// The map to draw <paramref name="body"/> from and its credit, or false until one has landed. Asks, without waiting, for the
    /// atlas on first use and for the body's OPAL upgrade when there is one to ask for.
    /// </summary>
    public bool TryGet(CatalogIndex body, [MaybeNullWhen(false)] out PlanetColourMap map, out string credit)
    {
        RequestAtlas();
        RequestUpgrade(body);
        if (Volatile.Read(ref _maps).TryGetValue(body, out var entry))
        {
            (map, credit) = entry;
            return true;
        }
        map = null;
        credit = "";
        return false;
    }

    private void RequestAtlas()
    {
        if (AtlasPath is not { } path || Interlocked.CompareExchange(ref _atlasRequested, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (File.Exists(path))
                {
                    SetAtlas(await File.ReadAllBytesAsync(path));
                }
                else
                {
                    logger?.LogInformation("No planet texture atlas at {Path}; planets stay dots", path);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Planet texture atlas {Path} could not be read; planets stay dots", path);
            }
        });
    }

    private void RequestUpgrade(CatalogIndex body)
    {
        if (Store is not { } store || _upgraded.ContainsKey(body) || !OpalColourMaps.TryFor(body, out var opal)
            || (_failedAt.TryGetValue(body, out var failedAt) && utcNow() - failedAt < RetryCooldown)
            || !_inFlight.TryAdd(body, 0))
        {
            return;
        }
        Interlocked.Increment(ref _upgradeAttempts);
        LastUpgrade = Task.Run(async () =>
        {
            try
            {
                if (await store.GetAsync(opal) is { } bytes && OpalColourMaps.Decode(bytes) is { } map)
                {
                    _upgraded.TryAdd(body, 0);
                    _failedAt.TryRemove(body, out _);
                    ImmutableInterlocked.Update(ref _maps, maps => maps.SetItem(body, (map, opal.Credit)));
                    Interlocked.Increment(ref _version);
                    RequestRedraw?.Invoke();
                }
                else
                {
                    _failedAt[body] = utcNow();
                }
            }
            catch (Exception ex)
            {
                // Logged, the atlas kept, and not asked again until the cooldown has passed.
                logger?.LogInformation(ex, "OPAL map for {Body} unavailable; keeping the atlas", body);
                _failedAt[body] = utcNow();
            }
            finally
            {
                _inFlight.TryRemove(body, out _);
            }
        });
    }
}
