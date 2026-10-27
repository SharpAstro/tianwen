using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A file's own samples, answered one at a time for a viewer's cursor readout, so the float planes need not be
/// resident (#368, #1856). One implementation per file format (<see cref="ReadoutTileSources.TryOpen"/> picks it);
/// <see cref="ReadoutTileSourceBase"/> holds the bounded tile cache and the staleness check they share.
/// </summary>
/// <remarks>
/// A source answers the same number as <c>image[c, y, x]</c> of the image the format's own reader produces and the
/// import scaled to [0, 1]. It also reads the WHOLE image back through that reader (<see cref="TryReadImage"/>),
/// which is how a document whose planes were released gets them again: the readout and the reload always agree on
/// the format, and a new format adds a source and nothing else.
/// </remarks>
public interface IReadoutTileSource
{
    /// <summary>Samples per row.</summary>
    int Width { get; }

    /// <summary>Rows per plane.</summary>
    int Height { get; }

    /// <summary>Planes (channels).</summary>
    int ChannelCount { get; }

    /// <summary>Tiles read from the file so far: what a test asks to prove a move inside a tile reads nothing.</summary>
    long TilesRead { get; }

    /// <summary>Whether the file has changed since it was opened, after which nothing is read from it.</summary>
    bool IsStale { get; }

    /// <summary>
    /// The sample at (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="channel"/>. False for a position
    /// outside the image, a changed file or a failed read, and <paramref name="value"/> is then meaningless.
    /// </summary>
    bool TryRead(int channel, int x, int y, out float value);

    /// <summary>
    /// Reads the whole image through the format's own reader, as the document's open did and before the import's
    /// scaling to [0, 1], for <c>Image.TryReloadPlanes</c>. False when the file will not read.
    /// </summary>
    bool TryReadImage([NotNullWhen(true)] out Image? image);
}
