using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Weak-reference cache for <see cref="AstroImageDocument"/> instances.
/// Previously loaded documents may be returned instantly if the GC
/// has not yet reclaimed them; otherwise they are reloaded from disk.
/// </summary>
public sealed class DocumentCache
{
    private readonly Dictionary<string, WeakReference<AstroImageDocument>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a cached document if still alive, otherwise loads from disk and caches the result.
    /// </summary>
    public async Task<AstroImageDocument?> GetOrLoadAsync(
        string filePath,
        DebayerAlgorithm algorithm = DebayerAlgorithm.AHD,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(filePath, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            return cached;
        }

        var doc = await AstroImageDocument.OpenAsync(filePath, algorithm, cancellationToken);
        if (doc is not null)
        {
            _cache[filePath] = new WeakReference<AstroImageDocument>(doc);
        }

        return doc;
    }

    /// <summary>
    /// Purges entries whose targets have been collected.
    /// </summary>
    public void Scavenge()
    {
        List<string>? dead = null;
        foreach (var (key, weakRef) in _cache)
        {
            if (!weakRef.TryGetTarget(out _))
            {
                (dead ??= []).Add(key);
            }
        }

        if (dead is not null)
        {
            foreach (var key in dead)
            {
                _cache.Remove(key);
            }
        }
    }
}
