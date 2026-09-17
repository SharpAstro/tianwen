using System;
using System.Buffers.Binary;
using System.IO;
using SharpAstro.Lzip;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// The shipped Milky Way texture, <c>milkyway.bgra.lz</c>: lzip framing around an 8-byte header
/// (little-endian int32 width, then height) and <c>width * height</c> BGRA pixels, as
/// <see cref="MilkyWayTextureBaker.WriteRaw"/> writes it before compression.
/// </summary>
/// <remarks>
/// <para>The ONE reader of that format, for both of its consumers: the desktop sky map uploads
/// <see cref="Bgra"/> as a texture, and <c>tools/bake-milkyway</c> turns the same bytes into the PNG the
/// browser sky map loads. Two parsers of one header is how a desktop and a web sky drift apart.</para>
/// <para>The alpha channel is not coverage: the baker writes the pixel's BRIGHTNESS there, and the
/// desktop shader multiplies colour by it under a <c>SrcAlpha, One</c> additive blend.</para>
/// </remarks>
/// <param name="Raw">The decompressed file, header included.</param>
/// <param name="Width">Texture width in pixels (right ascension).</param>
/// <param name="Height">Texture height in pixels (declination).</param>
public readonly record struct MilkyWayTextureFile(byte[] Raw, int Width, int Height)
{
    /// <summary>Bytes of width/height header before the pixels.</summary>
    public const int HeaderBytes = 8;

    /// <summary>A bound on either dimension, so a corrupt header cannot overflow the size check.</summary>
    private const int MaxDimension = 1 << 15;

    /// <summary>The BGRA pixels, row-major, north at the top.</summary>
    public ReadOnlySpan<byte> Bgra => Raw.AsSpan(HeaderBytes, Width * Height * 4);

    /// <summary>
    /// Decompresses and validates a <c>milkyway.bgra.lz</c> file.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not lzip, is shorter than its header, or its
    /// header names dimensions its pixels do not fill.</exception>
    public static MilkyWayTextureFile Decode(byte[] compressed)
    {
        var raw = LzipDecoder.Decompress(compressed);
        if (raw.Length < HeaderBytes)
        {
            throw new InvalidDataException($"Milky Way texture is {raw.Length} bytes, shorter than its {HeaderBytes}-byte header.");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(raw);
        var height = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        if (width is <= 0 or > MaxDimension || height is <= 0 or > MaxDimension
            || raw.Length < HeaderBytes + (long)width * height * 4)
        {
            throw new InvalidDataException(
                $"Milky Way texture header says {width}x{height}, which a {raw.Length}-byte file does not hold.");
        }

        return new MilkyWayTextureFile(raw, width, height);
    }

    /// <summary>
    /// The texture as packed RGB with each pixel's colour already multiplied by its alpha, which is what
    /// the desktop blend computes per fragment. The browser sky map loads this as an opaque PNG.
    /// </summary>
    /// <remarks>
    /// Opaque on purpose. A PNG with a real alpha channel leaves the browser to decide whether to
    /// premultiply on decode, and un-premultiplying loses precision exactly where this texture lives, at
    /// low alpha on a dark sky. Folding alpha into the colour at bake time leaves nothing to decide; the
    /// web shader adds the colour under a <c>One, One</c> blend and gets the desktop's result to within
    /// one 8-bit rounding step.
    /// </remarks>
    public byte[] ToPremultipliedRgb()
    {
        var bgra = Bgra;
        var rgb = new byte[Width * Height * 3];
        for (int i = 0, o = 0; i < bgra.Length; i += 4, o += 3)
        {
            var a = bgra[i + 3];
            rgb[o] = Premultiply(bgra[i + 2], a);
            rgb[o + 1] = Premultiply(bgra[i + 1], a);
            rgb[o + 2] = Premultiply(bgra[i], a);
        }

        return rgb;
    }

    /// <summary><c>round(c * a / 255)</c> in integers.</summary>
    private static byte Premultiply(byte c, byte a) => (byte)((c * a + 127) / 255);
}
