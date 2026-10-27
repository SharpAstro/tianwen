using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The part of a readout source every format shares: the bounded cache of 64 x 64 tiles, and the check that the
/// file is still the one that was opened. A format supplies <see cref="TryReadTile"/> and <see cref="TryReadImage"/>.
/// </summary>
/// <remarks>
/// <para>The cache holds <see cref="MaxTiles"/> tiles and is emptied when full, so its memory is bounded at about
/// 1 MB whatever the file's size, and a hit allocates nothing. A ConcurrentDictionary because the readout is the
/// render thread's but an export or a test may ask from another: nothing here is worth a lock.</para>
/// <para>A file that changed is not read: its length and write time are compared with the ones taken at open on
/// every miss, and once they differ every read answers false.</para>
/// </remarks>
public abstract class ReadoutTileSourceBase : IReadoutTileSource
{
    /// <summary>Side of a tile in samples.</summary>
    public const int TileSize = 64;

    /// <summary>The most tiles held before the cache is emptied (3 channels x 4 x 4 tiles around the cursor, and then some).</summary>
    public const int MaxTiles = 64;

    private readonly long _length;
    private readonly DateTime _writeTimeUtc;
    private readonly int _tilesAcross;
    private readonly int _tilesDown;

    // Key: ((channel * tilesDown) + tileRow) * tilesAcross + tileColumn.
    private readonly ConcurrentDictionary<long, float[]> _tiles = new();
    private long _tilesRead;
    private volatile bool _stale;

    /// <param name="path">The file.</param>
    /// <param name="width">Samples per row.</param>
    /// <param name="height">Rows per plane.</param>
    /// <param name="channelCount">Planes.</param>
    /// <param name="length">The file's length when it was opened.</param>
    /// <param name="writeTimeUtc">The file's write time when it was opened.</param>
    protected ReadoutTileSourceBase(string path, int width, int height, int channelCount, long length, DateTime writeTimeUtc)
    {
        Path = path;
        Width = width;
        Height = height;
        ChannelCount = channelCount;
        _length = length;
        _writeTimeUtc = writeTimeUtc;
        _tilesAcross = (width + TileSize - 1) / TileSize;
        _tilesDown = (height + TileSize - 1) / TileSize;
    }

    /// <summary>The file the source reads.</summary>
    protected string Path { get; }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public int ChannelCount { get; }

    /// <inheritdoc/>
    public long TilesRead => Interlocked.Read(ref _tilesRead);

    /// <inheritdoc/>
    public bool IsStale => _stale;

    /// <inheritdoc/>
    public abstract bool TryReadImage([NotNullWhen(true)] out Image? image);

    /// <summary>
    /// Reads one tile of <paramref name="width"/> x <paramref name="height"/> samples with its top-left at
    /// (<paramref name="x0"/>, <paramref name="y0"/>), row-major, converted and scaled as the whole-image read is.
    /// Called only while the file is unchanged.
    /// </summary>
    protected abstract bool TryReadTile(int channel, int x0, int y0, int width, int height, [NotNullWhen(true)] out float[]? tile);

    /// <inheritdoc/>
    public bool TryRead(int channel, int x, int y, out float value)
    {
        value = 0f;
        if ((uint)channel >= (uint)ChannelCount || (uint)x >= (uint)Width || (uint)y >= (uint)Height || _stale)
        {
            return false;
        }

        var tileColumn = x / TileSize;
        var tileRow = y / TileSize;
        var key = ((((long)channel * _tilesDown) + tileRow) * _tilesAcross) + tileColumn;
        if (!_tiles.TryGetValue(key, out var tile))
        {
            if (!TryLoadTile(channel, tileColumn, tileRow, out tile))
            {
                return false;
            }

            if (_tiles.Count >= MaxTiles)
            {
                _tiles.Clear();
            }

            _tiles[key] = tile;
        }

        var tileWidth = Math.Min(TileSize, Width - (tileColumn * TileSize));
        value = tile[((y - (tileRow * TileSize)) * tileWidth) + (x - (tileColumn * TileSize))];
        return true;
    }

    private bool TryLoadTile(int channel, int tileColumn, int tileRow, [NotNullWhen(true)] out float[]? tile)
    {
        tile = null;
        var x0 = tileColumn * TileSize;
        var y0 = tileRow * TileSize;
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.LastWriteTimeUtc != _writeTimeUtc || info.Length != _length)
            {
                _stale = true;
                return false;
            }

            if (!TryReadTile(channel, x0, y0, Math.Min(TileSize, Width - x0), Math.Min(TileSize, Height - y0), out tile))
            {
                return false;
            }

            Interlocked.Increment(ref _tilesRead);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
