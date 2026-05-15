using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Tests;

/// <summary>
/// Process-wide cached <see cref="ICelestialObjectDB"/> for tests. Tycho-2
/// bulk load is ~3 s of one-off cost, so every test class that needs a
/// catalog (plate solving, SPCC, etc.) shares this single instance.
/// </summary>
public static class SharedCatalogDB
{
    private static ICelestialObjectDB? _cached;
    private static readonly SemaphoreSlim _sem = new(1, 1);

    /// <summary>
    /// Returns a fully-initialised celestial object DB (Tycho-2 bulk loaded).
    /// First call pays the load cost; subsequent calls return the cached instance.
    /// </summary>
    public static async Task<ICelestialObjectDB> InitAsync(CancellationToken ct = default)
    {
        if (_cached is { } db) return db;
        await _sem.WaitAsync(ct);
        try
        {
            if (_cached is { } db2) return db2;
            var newDb = new CelestialObjectDB();
            await newDb.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            _cached = newDb;
            return newDb;
        }
        finally
        {
            _sem.Release();
        }
    }
}
