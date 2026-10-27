using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Weak-reference cache for <see cref="AstroImageDocument"/> instances.
/// Previously loaded documents may be returned instantly if the GC
/// has not yet reclaimed them; otherwise they are reloaded from disk.
/// </summary>
/// <remarks>
/// It is also the one place that knows every open document, so it holds the TOTAL float-plane budget
/// (<see cref="TotalBudgetBytes"/>, #1853): a document that opens when the planes already resident would pass it releases
/// the least recently used documents the file can give back (<see cref="AstroImageDocument.TryReleaseForBudget"/>), skipping
/// any that a consumer has pinned (<see cref="AstroImageDocument.EnsurePlanesAsync"/>), until the total fits or nothing is left to release.
/// No operating-system memory signal: the budget is a number, so identical input behaves identically.
/// </remarks>
public sealed class DocumentCache : IDocumentCache
{
    private readonly Dictionary<string, WeakReference<AstroImageDocument>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most float-plane bytes every open document may hold together: three of the per-document budget's worth
    /// (<see cref="EnhanceRevertPolicy.RetainBudgetBytes"/>), so a few ordinary masters stay resident and a folder walked
    /// through a hundred large ones does not.
    /// </summary>
    public long TotalBudgetBytes { get; set; } = 3 * EnhanceRevertPolicy.RetainBudgetBytes;

    /// <summary>
    /// Returns a cached document if still alive, otherwise loads from disk and caches the result.
    /// </summary>
    public async Task<AstroImageDocument?> GetOrLoadAsync(
        string filePath,
        DebayerAlgorithm algorithm = DebayerAlgorithm.VNG,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(filePath, out var weakRef) && weakRef.TryGetTarget(out var cached))
        {
            cached.Touch();
            return cached;
        }

        var doc = await AstroImageDocument.OpenAsync(filePath, algorithm, cancellationToken);
        if (doc is not null)
        {
            doc.Touch();
            _cache[filePath] = new WeakReference<AstroImageDocument>(doc);
            Admit(doc);
        }

        return doc;
    }

    /// <summary>
    /// Brings the planes resident across every open document within <see cref="TotalBudgetBytes"/>, releasing
    /// the least recently used documents that can be read back, never <paramref name="opened"/> and never one a consumer has pinned.
    /// </summary>
    /// <returns>The documents released, in the order they were.</returns>
    internal IReadOnlyList<AstroImageDocument> Admit(AstroImageDocument opened)
    {
        var live = new List<AstroImageDocument>();
        foreach (var weakRef in _cache.Values)
        {
            if (weakRef.TryGetTarget(out var doc) && !ReferenceEquals(doc, opened))
            {
                live.Add(doc);
            }
        }

        var total = opened.ResidentPlaneBytes + live.Sum(d => d.ResidentPlaneBytes);
        var released = new List<AstroImageDocument>();
        if (total <= TotalBudgetBytes)
        {
            return released;
        }

        foreach (var doc in live.Where(d => d.ResidentPlaneBytes > 0).OrderBy(d => d.LastUsedTick))
        {
            var bytes = doc.ResidentPlaneBytes;
            if (doc.TryReleaseForBudget())
            {
                released.Add(doc);
                total -= bytes;
                if (total <= TotalBudgetBytes)
                {
                    break;
                }
            }
        }

        return released;
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
