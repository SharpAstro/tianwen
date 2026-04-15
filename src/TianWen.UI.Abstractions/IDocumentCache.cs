using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Cache for <see cref="AstroImageDocument"/> instances.
/// Returns a previously loaded document if still alive, otherwise reloads from disk.
/// </summary>
public interface IDocumentCache
{
    /// <summary>
    /// Returns a cached document if still alive, otherwise loads from disk and caches the result.
    /// </summary>
    Task<AstroImageDocument?> GetOrLoadAsync(
        string filePath,
        DebayerAlgorithm algorithm = DebayerAlgorithm.AHD,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Purges entries whose targets have been collected.
    /// </summary>
    void Scavenge();
}
