using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using Rectangle = System.Drawing.Rectangle;

namespace TianWen.Lib.IO;

/// <summary>
/// Memory-mapped partial FITS reader. Maps a single-HDU 2D image FITS file
/// once at construction, parses the header to cache geometry + scaling
/// parameters (<c>BITPIX</c>, <c>NAXIS1/2</c>, <c>BZERO</c>, <c>BSCALE</c>),
/// then serves arbitrary sub-rectangle reads via <see cref="ReadRegion"/> --
/// each one a single seek + bulk copy + per-pixel decode, no full-image
/// allocation.
///
/// <para>Built for the stacking pipeline's Phase 8 tile-pipelined integrator
/// (PLAN-stacking.md): the integrator iterates output canvas tiles and pulls
/// just the source region under each frame's inverse transform. With this
/// reader peak RAM is bounded by the tile-column working set rather than
/// <c>N x debayered</c>.</para>
///
/// <para>v1 scope: physical-axis 2D image HDU. Supports
/// <c>BITPIX in {8, 16, 32, -32, -64}</c>; FITS is big-endian on disk and we
/// byte-swap to host order on read. Per-row decoders for the three common
/// formats (16-bit int, 32-bit int, 32-bit float) have a Vector128 prologue
/// + scalar tail and run ~4x faster than the naive scalar loop on the
/// full-image bench; the BITPIX=8 path needs no swap and BITPIX=-64 is rare
/// in science FITS so both stay scalar. Lives in <c>TianWen.Lib.IO</c>
/// rather than the FITS.Lib NuGet so the stacking pipeline can iterate
/// without a cross-repo release dance; promotion to FITS.Lib happens once
/// the API stabilises.</para>
/// </summary>
public sealed class PartialFitsReader : IDisposable
{
    /// <summary>FITS header card size in bytes.</summary>
    public const int CardSize = 80;

    /// <summary>FITS storage block size in bytes -- header + data are both
    /// padded to multiples of this.</summary>
    public const int BlockSize = 2880;

    /// <summary>Number of header cards in one block.</summary>
    public const int CardsPerBlock = BlockSize / CardSize;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    // Cached pointer into the mapped file; valid as long as _view is alive.
    private readonly unsafe byte* _basePtr;
    private readonly long _fileLength;

    /// <summary>Image width in pixels (<c>NAXIS1</c>).</summary>
    public int Width { get; }

    /// <summary>Image height in pixels (<c>NAXIS2</c>).</summary>
    public int Height { get; }

    /// <summary>FITS pixel-format code from the <c>BITPIX</c> card: 8 = byte,
    /// 16 = signed short, 32 = signed int, -32 = float32, -64 = float64.</summary>
    public int BitPix { get; }

    /// <summary>Bytes per pixel = <c>|BITPIX| / 8</c>.</summary>
    public int BytesPerPixel { get; }

    /// <summary>FITS scaling offset (<c>BZERO</c>; default 0). Applied as
    /// <c>physical = BZERO + BSCALE * stored</c>.</summary>
    public double BZero { get; }

    /// <summary>FITS scaling slope (<c>BSCALE</c>; default 1).</summary>
    public double BScale { get; }

    /// <summary>File byte offset where the pixel data begins (after the
    /// header's last 2880-block boundary).</summary>
    public long DataOffset { get; }

    public PartialFitsReader(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _fileLength = _view.Capacity;

        unsafe
        {
            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _basePtr = ptr;
        }

        // Parse header. Cards are 80 ASCII bytes; END card terminates; header
        // pads up to next 2880-byte boundary.
        var bitpix = 0;
        var naxis = 0;
        var width = 0;
        var height = 0;
        var bzero = 0.0;
        var bscale = 1.0;
        var ended = false;
        long offset = 0;

        while (offset + CardSize <= _fileLength && !ended)
        {
            var blockStart = offset;
            for (var c = 0; c < CardsPerBlock; c++)
            {
                var card = ReadAsciiCard(offset);
                offset += CardSize;
                if (card.StartsWith("END") && (card.Length == 3 || card[3] == ' '))
                {
                    ended = true;
                    break;
                }
                // Cards look like "KEYWORD = value / comment" with KEYWORD in 1..8
                // and "= " at columns 9..10. We only need the typed scalars.
                if (card.Length < 10 || card[8] != '=') continue;
                var keyword = card.AsSpan(0, 8).TrimEnd().ToString();
                var valueStr = ParseValue(card.AsSpan(10));
                switch (keyword)
                {
                    case "BITPIX": bitpix = ParseInt(valueStr); break;
                    case "NAXIS":  naxis  = ParseInt(valueStr); break;
                    case "NAXIS1": width  = ParseInt(valueStr); break;
                    case "NAXIS2": height = ParseInt(valueStr); break;
                    case "BZERO":  bzero  = ParseDouble(valueStr); break;
                    case "BSCALE": bscale = ParseDouble(valueStr); break;
                }
            }
            // Pad to next 2880-block start before reading the next block of cards.
            offset = blockStart + BlockSize;
        }

        if (!ended) throw new InvalidDataException($"FITS header missing END card in {path}");
        if (naxis != 2) throw new NotSupportedException($"PartialFitsReader v1 only supports NAXIS=2 images (got NAXIS={naxis} in {path})");
        if (width <= 0 || height <= 0) throw new InvalidDataException($"Bad NAXIS1/NAXIS2 in {path}: {width}x{height}");
        if (bitpix is not (8 or 16 or 32 or -32 or -64))
            throw new NotSupportedException($"PartialFitsReader doesn't support BITPIX={bitpix} (in {path})");

        Width = width;
        Height = height;
        BitPix = bitpix;
        BytesPerPixel = Math.Abs(bitpix) / 8;
        BZero = bzero;
        BScale = bscale;
        DataOffset = offset;

        var dataBytes = (long)Width * Height * BytesPerPixel;
        if (DataOffset + dataBytes > _fileLength)
        {
            throw new InvalidDataException(
                $"FITS data region extends beyond file end in {path}: offset={DataOffset} + bytes={dataBytes} > length={_fileLength}");
        }
    }

    /// <summary>
    /// Read a sub-rectangle of pixels into <paramref name="dest"/> as
    /// physical values (after applying <c>BZERO</c> + <c>BSCALE</c>).
    /// Row-major in row-major order: <c>dest[r * src.Width + c]</c>
    /// corresponds to file pixel <c>(src.X + c, src.Y + r)</c>.
    ///
    /// <para><paramref name="src"/> must lie entirely within
    /// <c>[0, Width) x [0, Height)</c>. <paramref name="dest"/> must hold at
    /// least <c>src.Width * src.Height</c> floats.</para>
    /// </summary>
    public void ReadRegion(Rectangle src, Span<float> dest)
    {
        if (src.X < 0 || src.Y < 0 || src.Right > Width || src.Bottom > Height
            || src.Width <= 0 || src.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(src),
                $"Region {src} out of image bounds [0,0)-({Width},{Height}).");
        }
        var pixelCount = src.Width * src.Height;
        if (dest.Length < pixelCount)
        {
            throw new ArgumentException(
                $"Destination span too small: {dest.Length} < {pixelCount}.", nameof(dest));
        }

        switch (BitPix)
        {
            case 16:  ReadRegion16BE(src, dest); break;
            case 8:   ReadRegion8(src, dest); break;
            case 32:  ReadRegion32IntBE(src, dest); break;
            case -32: ReadRegion32FloatBE(src, dest); break;
            case -64: ReadRegion64FloatBE(src, dest); break;
            default: throw new NotSupportedException($"BITPIX={BitPix}");
        }
    }

    private unsafe void ReadRegion16BE(Rectangle src, Span<float> dest)
    {
        // FITS stores 16-bit pixels as big-endian signed int16. With BZERO=32768
        // BSCALE=1 (the standard unsigned-via-signed trick) the physical range
        // is [0, 65535]; otherwise it's [-32768, 32767] scaled by BSCALE.
        var bzero = (float)BZero;
        var bscale = (float)BScale;
        var rowBytes = (long)Width * 2;
        for (var r = 0; r < src.Height; r++)
        {
            var srcRowStart = _basePtr + DataOffset + (long)(src.Y + r) * rowBytes + (long)src.X * 2;
            var destRow = dest.Slice(r * src.Width, src.Width);
            DecodeRow16BE(srcRowStart, destRow, bzero, bscale);
        }
    }

    // Within-element byte-reverse permutations for Vector128.Shuffle. JITs to
    // PSHUFB (x86 SSSE3) / TBL1 (ARM NEON) -- one instruction per swap.
    private static readonly Vector128<byte> Swap16Mask = Vector128.Create(
        (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14);
    private static readonly Vector128<byte> Swap32Mask = Vector128.Create(
        (byte)3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12);

    // Vector128 prologue + scalar tail. 8 ushort lanes per iteration -> two
    // Vector128<float> stores. Bench (3008x3008 full-frame): scalar ~14 ms,
    // SIMD ~3.5 ms on AVX2 / NEON.
    private static unsafe void DecodeRow16BE(byte* src, Span<float> dest, float bzero, float bscale)
    {
        var c = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            const int Lanes = 8; // Vector128<ushort>.Count
            var bzeroV = Vector128.Create(bzero);
            var bscaleV = Vector128.Create(bscale);
            ref var srcRef = ref Unsafe.AsRef<ushort>(src);
            ref var destRef = ref MemoryMarshal.GetReference(dest);
            for (; c + Lanes <= dest.Length; c += Lanes)
            {
                var beVec = Vector128.LoadUnsafe(ref srcRef, (nuint)c);
                var leVec = Vector128.Shuffle(beVec.AsByte(), Swap16Mask).AsUInt16();
                // Reinterpret as signed int16 so BZERO=32768 BSCALE=1 maps
                // raw signed -> unsigned uint16 range.
                var (lowI, highI) = Vector128.Widen(leVec.AsInt16());
                var lowF = Vector128.ConvertToSingle(lowI) * bscaleV + bzeroV;
                var highF = Vector128.ConvertToSingle(highI) * bscaleV + bzeroV;
                lowF.StoreUnsafe(ref destRef, (nuint)c);
                highF.StoreUnsafe(ref destRef, (nuint)(c + 4));
            }
        }
        for (; c < dest.Length; c++)
        {
            var be = Unsafe.ReadUnaligned<ushort>(src + c * 2);
            var stored = (short)BinaryPrimitives.ReverseEndianness(be);
            dest[c] = bzero + bscale * stored;
        }
    }

    private unsafe void ReadRegion8(Rectangle src, Span<float> dest)
    {
        var bzero = (float)BZero;
        var bscale = (float)BScale;
        var rowBytes = (long)Width;
        for (var r = 0; r < src.Height; r++)
        {
            var srcRowStart = _basePtr + DataOffset + (long)(src.Y + r) * rowBytes + src.X;
            var destRow = dest.Slice(r * src.Width, src.Width);
            for (var c = 0; c < src.Width; c++)
            {
                destRow[c] = bzero + bscale * srcRowStart[c];
            }
        }
    }

    private unsafe void ReadRegion32IntBE(Rectangle src, Span<float> dest)
    {
        var bzero = (float)BZero;
        var bscale = (float)BScale;
        var rowBytes = (long)Width * 4;
        for (var r = 0; r < src.Height; r++)
        {
            var srcRowStart = _basePtr + DataOffset + (long)(src.Y + r) * rowBytes + (long)src.X * 4;
            var destRow = dest.Slice(r * src.Width, src.Width);
            DecodeRow32IntBE(srcRowStart, destRow, bzero, bscale);
        }
    }

    private static unsafe void DecodeRow32IntBE(byte* src, Span<float> dest, float bzero, float bscale)
    {
        var c = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            const int Lanes = 4; // Vector128<uint>.Count
            var bzeroV = Vector128.Create(bzero);
            var bscaleV = Vector128.Create(bscale);
            ref var srcRef = ref Unsafe.AsRef<uint>(src);
            ref var destRef = ref MemoryMarshal.GetReference(dest);
            for (; c + Lanes <= dest.Length; c += Lanes)
            {
                var beVec = Vector128.LoadUnsafe(ref srcRef, (nuint)c);
                var leVec = Vector128.Shuffle(beVec.AsByte(), Swap32Mask).AsInt32();
                var fVec = Vector128.ConvertToSingle(leVec) * bscaleV + bzeroV;
                fVec.StoreUnsafe(ref destRef, (nuint)c);
            }
        }
        for (; c < dest.Length; c++)
        {
            var be = Unsafe.ReadUnaligned<uint>(src + c * 4);
            var stored = (int)BinaryPrimitives.ReverseEndianness(be);
            dest[c] = bzero + bscale * stored;
        }
    }

    private unsafe void ReadRegion32FloatBE(Rectangle src, Span<float> dest)
    {
        var bzero = (float)BZero;
        var bscale = (float)BScale;
        var rowBytes = (long)Width * 4;
        for (var r = 0; r < src.Height; r++)
        {
            var srcRowStart = _basePtr + DataOffset + (long)(src.Y + r) * rowBytes + (long)src.X * 4;
            var destRow = dest.Slice(r * src.Width, src.Width);
            DecodeRow32FloatBE(srcRowStart, destRow, bzero, bscale);
        }
    }

    private static unsafe void DecodeRow32FloatBE(byte* src, Span<float> dest, float bzero, float bscale)
    {
        var c = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            const int Lanes = 4; // Vector128<float>.Count
            var bzeroV = Vector128.Create(bzero);
            var bscaleV = Vector128.Create(bscale);
            ref var srcRef = ref Unsafe.AsRef<uint>(src);
            ref var destRef = ref MemoryMarshal.GetReference(dest);
            for (; c + Lanes <= dest.Length; c += Lanes)
            {
                var beVec = Vector128.LoadUnsafe(ref srcRef, (nuint)c);
                // Float bits get the same 4-byte swap as ints, then reinterpret.
                var leVec = Vector128.Shuffle(beVec.AsByte(), Swap32Mask).AsSingle();
                var fVec = leVec * bscaleV + bzeroV;
                fVec.StoreUnsafe(ref destRef, (nuint)c);
            }
        }
        for (; c < dest.Length; c++)
        {
            var beBits = Unsafe.ReadUnaligned<uint>(src + c * 4);
            var leBits = BinaryPrimitives.ReverseEndianness(beBits);
            var stored = BitConverter.Int32BitsToSingle((int)leBits);
            dest[c] = bzero + bscale * stored;
        }
    }

    private unsafe void ReadRegion64FloatBE(Rectangle src, Span<float> dest)
    {
        var bzero = BZero;
        var bscale = BScale;
        var rowBytes = (long)Width * 8;
        for (var r = 0; r < src.Height; r++)
        {
            var srcRowStart = _basePtr + DataOffset + (long)(src.Y + r) * rowBytes + (long)src.X * 8;
            var destRow = dest.Slice(r * src.Width, src.Width);
            for (var c = 0; c < src.Width; c++)
            {
                var beBits = Unsafe.ReadUnaligned<ulong>(srcRowStart + c * 8);
                var leBits = BinaryPrimitives.ReverseEndianness(beBits);
                var stored = BitConverter.Int64BitsToDouble((long)leBits);
                destRow[c] = (float)(bzero + bscale * stored);
            }
        }
    }

    private unsafe string ReadAsciiCard(long offset)
    {
        var src = _basePtr + offset;
        var srcSpan = new ReadOnlySpan<byte>(src, CardSize);
        return Encoding.ASCII.GetString(srcSpan);
    }

    private static string ParseValue(ReadOnlySpan<char> afterEquals)
    {
        // Strip the optional `/ comment`, trim whitespace. Handles quoted
        // strings ('...') by preserving the quoted region intact.
        if (afterEquals.IsEmpty) return string.Empty;
        var slash = -1;
        var inQuote = false;
        for (var i = 0; i < afterEquals.Length; i++)
        {
            var ch = afterEquals[i];
            if (ch == '\'') inQuote = !inQuote;
            else if (ch == '/' && !inQuote) { slash = i; break; }
        }
        var valuePart = slash >= 0 ? afterEquals[..slash] : afterEquals;
        return valuePart.Trim().ToString();
    }

    private static int ParseInt(string value)
        => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double ParseDouble(string value)
        => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    public void Dispose()
    {
        unsafe
        {
            if (_basePtr is not null)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        _view.Dispose();
        _mmf.Dispose();
    }
}
