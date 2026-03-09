using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace TianWen.Lib;

/// <summary>Provides functionality for encoding data into the Base-91 (formerly written basE91) text representation and back.</summary>
/// <remarks>See more: <seealso href="http://base91.sourceforge.net/"/>.</remarks>
public static class Base91
{
    /// <summary>Encodes the specified sequence of bytes.</summary>
    /// <param name="bytes">The sequence of bytes to encode.</param>
    /// <returns>A string that contains the result of encoding the specified sequence of bytes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static string EncodeBytes(ReadOnlySpan<byte> bytes)
    {
        // Worst case: each byte produces ~1.23 output chars, use 2x as safe upper bound.
        // Safety check for large bytes by stackalloc'ing small outputs. This should not happen in practice.
        Span<char> output = bytes.Length < 100 ? stackalloc char[bytes.Length * 2 + 2] : new char[bytes.Length * 2 + 2];
        var pos = 0;
        int ebVal = 0, ebBits = 0;

        foreach (var b in bytes)
        {
            ebVal |= b << ebBits;
            ebBits += 8;
            if (ebBits < 14)
                continue;

            int v = ebVal & 8191;
            if (v > 88)
            {
                ebBits -= 13;
                ebVal >>= 13;
            }
            else
            {
                v = ebVal & 16383;
                ebBits -= 14;
                ebVal >>= 14;
            }
            output[pos++] = (char)CharacterTable91[v % 91];
            output[pos++] = (char)CharacterTable91[v / 91];
        }

        if (ebBits > 0)
        {
            output[pos++] = (char)CharacterTable91[ebVal % 91];
            if (ebBits >= 8 || ebVal >= 91)
                output[pos++] = (char)CharacterTable91[ebVal / 91];
        }

        return new string(output[..pos]);
    }

    /// <summary>Decodes the specified string into a sequence of bytes.</summary>
    /// <param name="code">The string to decode.</param>
    /// <exception cref="ArgumentNullException">code is null.</exception>
    /// <exception cref="DecoderFallbackException">code contains invalid characters.</exception>
    /// <returns>A sequence of bytes that contains the results of decoding the specified string.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static byte[] DecodeBytes(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        // Decoded output is at most ~81% of encoded length
        // Safety check for large strings by stackalloc'ing small outputs. This should not happen in practice.
        Span<byte> output = code.Length < 100 ? stackalloc byte[code.Length] : new byte[code.Length];
        var pos = 0;
        int dbVal = 0, dbBits = 0, dbPrev = -1;

        foreach (var ch in code)
        {
            if (ch is '\0' or '\t' or '\n' or '\r' or ' ')
                continue;

            var idx = ch < 128 ? ReverseTable91[ch] : -1;
            if (idx < 0)
                throw new DecoderFallbackException($"Character {ch} is invalid!");

            if (dbPrev < 0)
            {
                dbPrev = idx;
                continue;
            }

            dbPrev += idx * 91;
            dbVal |= dbPrev << dbBits;
            dbBits += (dbPrev & 8191) > 88 ? 13 : 14;

            do
            {
                output[pos++] = (byte)(dbVal & byte.MaxValue);
                dbVal >>= 8;
                dbBits -= 8;
            }
            while (dbBits > 7);

            dbPrev = -1;
        }

        if (dbPrev != -1)
            output[pos++] = (byte)((dbVal | (dbPrev << dbBits)) & byte.MaxValue);

        return output[..pos].ToArray();
    }

    /// <summary>Standard 91-character set:
    ///     <para><code>
    ///             ABCDEFGHIJKLMNOPQRSTUVWXYZ<br/>
    ///             abcdefghijklmnopqrstuvwxyz<br/>
    ///             0123456789!#$%&amp;()*+,-.:;&lt;=<br/>
    ///             &gt;?@[]^_`{|}~&quot;<br/>
    ///         </code></para>
    /// </summary>
    private static readonly byte[] CharacterTable91 =
    [
        0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f, 0x50, 0x51, 0x52,
        0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x61,
        0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a,
        0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71, 0x72, 0x73,
        0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x30, 0x31,
        0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x21,
        0x23, 0x24, 0x25, 0x26, 0x28, 0x29, 0x2a, 0x2b, 0x2c,
        0x2d, 0x2e, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x40,
        0x5b, 0x5d, 0x5e, 0x5f, 0x60, 0x7b, 0x7c, 0x7d, 0x7e,
        0x22
    ];

    /// <summary>Reverse lookup table: ASCII byte value → index in <see cref="CharacterTable91"/>, or -1 if not a valid Base91 character.</summary>
    private static readonly sbyte[] ReverseTable91 = BuildReverseTable();

    private static sbyte[] BuildReverseTable()
    {
        var table = new sbyte[128];
        Array.Fill(table, (sbyte)-1);
        for (int i = 0; i < CharacterTable91.Length; i++)
            table[CharacterTable91[i]] = (sbyte)i;
        return table;
    }
}
