using System;
using System.Drawing;
using System.IO;
using BenchmarkDotNet.Attributes;
using nom.tam.fits.IO;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Benchmarks;

/// <summary>
/// Full-read comparison: <see cref="PartialFitsReader"/> (memory-mapped, lazy
/// decode of a sub-rectangle) vs <see cref="Image.TryReadFitsFile"/>
/// (FITS.Lib full HDU load + per-pixel byte-swap + scaled into a
/// <c>float[,]</c>). Both readers must touch every pixel of the same
/// fixture; <c>PartialFitsReader.ReadRegion</c> is called with
/// <c>new Rectangle(0, 0, Width, Height)</c> so the comparison is
/// apples-to-apples (same pixel work, same physical pixel format).
///
/// <para>Expected: mmap path is markedly faster, especially in steady state
/// when the OS page cache is hot, because (a) the row-major access skips
/// the FITS.Lib HDU-tree machinery and (b) we don't allocate an
/// intermediate <see cref="Image"/> / <see cref="ImageMeta"/>. Cold-cache
/// reads should still be competitive since the bottleneck there is disk
/// throughput, not decode CPU.</para>
///
/// <para>Fixture is a synthetic 3008x3008 16-bit BZERO=32768 BSCALE=1
/// IMX533-shaped image written at <see cref="GlobalSetup"/> -- same shape
/// as the production light frame the tile-pipelined integrator pulls.</para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PartialFitsReaderBenchmarks
{
    private string _fitsPath = null!;
    private float[] _destBuffer = null!;
    private float[] _tileBuffer = null!;
    private int _width;
    private int _height;

    private const int Width = 3008;
    private const int Height = 3008;
    private const int TileSide = 256;

    [GlobalSetup]
    public void Setup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.UI.Benchmarks", "PartialFitsReader");
        Directory.CreateDirectory(dir);
        _fitsPath = Path.Combine(dir, $"synthetic_{Width}x{Height}_int16.fits");
        if (!File.Exists(_fitsPath))
        {
            WriteSyntheticFits(_fitsPath, Width, Height);
        }
        _width = Width;
        _height = Height;
        _destBuffer = new float[Width * Height];
        _tileBuffer = new float[TileSide * TileSide];
    }

    /// <summary>Writes a deterministic 16-bit mono FITS via the production
    /// <see cref="Image.WriteToFitsFile"/> writer so the readers see exactly
    /// the file shape they would in production (BITPIX=16, BZERO=32768,
    /// BSCALE=1, big-endian on-disk).</summary>
    private static void WriteSyntheticFits(string path, int width, int height)
    {
        var pixels = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Same ramp+xor pattern as ImageReadBenchmarks for parity.
                pixels[y, x] = ((x * 17) ^ (y * 31)) & 0xFFFF;
            }
        }
        // Write as BITPIX=16 (the production IMX533 camera format -- 16-bit
        // unsigned via BZERO=32768/BSCALE=1, big-endian signed int16 on disk).
        // PartialFitsReader's ReadRegion16BE is the path that runs in
        // TilePipelined, so the bench should match.
        var int16Image = new Image(
            data: new[] { pixels },
            bitDepth: BitDepth.Int16,
            maxValue: 65535f,
            minValue: 0f,
            pedestal: 0f,
            imageMeta: new ImageMeta { SensorType = SensorType.Monochrome });
        int16Image.WriteToFitsFile(path);
    }

    [Benchmark(Description = "Full read via FITS.Lib (Image.TryReadFitsFile)", Baseline = true)]
    public Image? FullRead_FitsLib()
    {
        Image.TryReadFitsFile(_fitsPath, out var image);
        return image;
    }

    [Benchmark(Description = "Full read via PartialFitsReader.ReadRegion(whole image)")]
    public int FullRead_PartialFitsReader()
    {
        using var reader = new PartialFitsReader(_fitsPath);
        reader.ReadRegion(new Rectangle(0, 0, _width, _height), _destBuffer);
        // Return a synthesised sum so the read isn't dead-code-eliminated;
        // we can't return a `float[]` (BDN warns on unstable identity).
        // Sum is cheap relative to the read so it doesn't skew the result.
        var sum = 0f;
        for (var i = 0; i < _destBuffer.Length; i++) sum += _destBuffer[i];
        return (int)sum;
    }

    /// <summary>
    /// The use-case PartialFitsReader was designed for: read a single
    /// 256x256 sub-tile from the centre of the canvas. FITS.Lib has no
    /// equivalent -- it always allocates + decodes the full 36 MB plane.
    /// This is the read pattern TilePipelinedStrategy's strip warp drives:
    /// many small reads per frame, never a full-frame read.
    /// </summary>
    [Benchmark(Description = "Tile 256x256 read via PartialFitsReader (mmap, designed use case)")]
    public int TileRead_PartialFitsReader()
    {
        using var reader = new PartialFitsReader(_fitsPath);
        var rect = new Rectangle(_width / 2 - TileSide / 2, _height / 2 - TileSide / 2, TileSide, TileSide);
        reader.ReadRegion(rect, _tileBuffer);
        var sum = 0f;
        for (var i = 0; i < _tileBuffer.Length; i++) sum += _tileBuffer[i];
        return (int)sum;
    }

    /// <summary>
    /// For reference: the FITS.Lib equivalent of a 256x256 tile read.
    /// There is no sub-rectangle API in CSharpFITS -- callers have to read
    /// the whole HDU and crop, paying the full decode + allocation. This
    /// number is the floor PartialFitsReader has to beat on the tile read.
    /// </summary>
    [Benchmark(Description = "Tile 256x256 read via FITS.Lib (full read + crop)")]
    public Image? TileRead_FitsLib()
    {
        Image.TryReadFitsFile(_fitsPath, out var image);
        // No crop -- FITS.Lib has no sub-region API, the whole HDU is in
        // memory anyway. Returning the Image keeps the result live.
        return image;
    }
}
