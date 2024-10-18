using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TianWen.Lib;

/// <summary>Provides functionality for encoding data into the Base-91 (formerly written basE91) text representation and back.</summary>
/// <remarks>See more: <seealso href="http://base91.sourceforge.net/"/>.</remarks>
public static class Base91
{
    /// <summary>Gets <see cref="Environment.NewLine"/> as a sequence of bytes used as a line separator within the <see cref="WriteLine(Stream, Span{byte}, int, int, ref int)"/>
    /// and <see cref="WriteLine(Stream, byte, int, ref int)"/> methods.</summary>
    private static readonly ReadOnlyMemory<byte> Separator = Encoding.UTF8.GetBytes(Environment.NewLine);

    /// <inheritdoc cref="EncodeStream(Stream, Stream, int, bool)"/>
    public static void EncodeStream(Stream inputStream, Stream outputStream, bool dispose) =>
        EncodeStream(inputStream, outputStream, 0, dispose);

    /// <summary>Encodes the specified sequence of bytes.</summary>
    /// <param name="bytes">The sequence of bytes to encode.</param>
    /// <param name="lineLength">The length of lines.</param>
    /// <exception cref="ArgumentNullException">bytes is null.</exception>
    /// <returns>A string that contains the result of encoding the specified sequence of bytes.</returns>
    public static string EncodeBytes(byte[] bytes, int lineLength = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        using var msi = new MemoryStream(bytes);
        using var mso = new MemoryStream();
        EncodeStream(msi, mso, lineLength);
        return Encoding.UTF8.GetString(mso.ToArray());
    }

    /// <summary>Encodes the specified string.</summary>
    /// <param name="text">The string to encode.</param>
    /// <param name="lineLength">The length of lines.</param>
    /// <exception cref="ArgumentNullException">text is null.</exception>
    /// <returns>A string that contains the result of encoding the specified string.</returns>
    public static string EncodeString(string text, int lineLength = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        
        var ba = Encoding.UTF8.GetBytes(text);
        return EncodeBytes(ba, lineLength);
    }

    /// <summary>Decodes the specified string into a sequence of bytes.</summary>
    /// <param name="code">The string to decode.</param>
    /// <exception cref="ArgumentNullException">code is null.</exception>
    /// <exception cref="DecoderFallbackException">code contains invalid characters.</exception>
    /// <returns>A sequence of bytes that contains the results of decoding the specified string.</returns>
    public static byte[] DecodeBytes(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        
        var ba = Encoding.UTF8.GetBytes(code);
        using var msi = new MemoryStream(ba);
        using var mso = new MemoryStream();
        DecodeStream(msi, mso);
        return mso.ToArray();
    }

    /// <summary>Decodes the specified string into a string.</summary>
    /// <param name="code">The string to decode.</param>
    /// <exception cref="ArgumentNullException">code is null.</exception>
    /// <exception cref="DecoderFallbackException">code contains invalid characters.</exception>
    /// <returns>A string that contains the result of decoding the specified string.</returns>
    public static string DecodeString(string code)
    {
        var ba = DecodeBytes(code) ?? throw new NullReferenceException();
 
        return Encoding.UTF8.GetString(ba);
    }

    /// <summary>Determines whether the specified value can be ignored.</summary>
    /// <param name="value">The character to check.</param>
    /// <param name="additional">Additional characters to be skipped.</param>
    /// <returns><see langword="true"/> if the byte number matches one of the characters; otherwise, <see langword="false"/>.</returns>
    private static bool IsSkippable(int value, params int[] additional) =>
        value is '\0' or '\t' or '\n' or '\r' or ' ' || additional?.Any(i => value == i) == true;

    /// <summary>Write the specified sequence of bytes into the stream and add a line separator depending on the specified line length.</summary>
    /// <param name="stream">The stream in which to write the single byte.</param>
    /// <param name="bytes">A sequence of bytes.</param>
    /// <param name="count">The number of bytes to be written to the current stream.</param>
    /// <param name="lineLength">The length of lines.</param>
    /// <param name="linePos">The current position in the line.</param>
    /// <exception cref="ArgumentNullException">stream is null.</exception>
    private static void WriteLine(Stream stream, Span<byte> bytes, int count, int lineLength, ref int linePos)
    {
        ArgumentNullException.ThrowIfNull(stream);
        
        if (count < 1 || count > bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(count), count, null);

        for (var i = 0; i < count; i++)
        {
            WriteLine(stream, bytes[i], lineLength, ref linePos);
        }
    }

    /// <inheritdoc cref="WriteLine(Stream, Span{byte}, int, int, ref int)"/>
    private static void WriteLine(Stream stream, Span<byte> bytes, int lineLength, ref int linePos) =>
        WriteLine(stream, bytes, bytes.Length, lineLength, ref linePos);

    /// <summary>Write the specified byte into the stream and add a line separator depending on the specified line length.</summary>
    /// <param name="stream">The stream in which to write the single byte.</param>
    /// <param name="value">The byte to write to the stream.</param>
    /// <param name="lineLength">The length of lines.</param>
    /// <param name="linePos">The position in the line.</param>
    /// <exception cref="ArgumentNullException">stream is null.</exception>
    private static void WriteLine(Stream stream, byte value, int lineLength, ref int linePos)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.WriteByte(value);
        if (Separator.IsEmpty || lineLength < 1 || ++linePos < lineLength)
            return;
        linePos = 0;
        stream.Write(Separator.Span);
    }

    internal static BufferedStream GetBufferedStream(Stream stream, int size = 0) =>
        stream switch
        {
            null => throw new ArgumentNullException(nameof(stream)),
            BufferedStream bs => bs,
            _ => new BufferedStream(stream, size > 0 ? size : GetBufferSize(stream))
        };

    internal static int GetBufferSize(Stream stream)
    {
        const int kb128 = 0x20000;
        const int kb64 = 0x10000;
        const int kb32 = 0x8000;
        const int kb16 = 0x4000;
        const int kb8 = 0x2000;
        const int kb4 = 0x1000;
        return (int)Math.Floor((stream?.Length ?? 0) / 1.5d) switch
        {
            > kb128 => kb128,
            > kb64 => kb64,
            > kb32 => kb32,
            > kb16 => kb16,
            > kb8 => kb8,
            _ => kb4
        };
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

    /// <summary>Encodes the specified input stream into the specified output stream.</summary>
    /// <param name="inputStream">The input stream to encode.</param>
    /// <param name="outputStream">The output stream for encoding.</param>
    /// <param name="lineLength">The length of lines.</param>
    /// <param name="dispose"><see langword="true"/> to release all resources used by the input and output <see cref="Stream"/>; otherwise, <see langword="false"/>.</param>
    /// <exception cref="ArgumentNullException">inputStream or outputStream is null.</exception>
    /// <exception cref="ArgumentException">inputStream or outputStream is invalid.</exception>
    /// <exception cref="NotSupportedException">inputStream is not readable -or- outputStream is not writable.</exception>
    /// <exception cref="IOException">An I/O error occurred, such as the specified file cannot be found.</exception>
    /// <exception cref="ObjectDisposedException">Methods were called after the inputStream or outputStream was closed.</exception>
    public static void EncodeStream(Stream inputStream, Stream outputStream, int lineLength = 0, bool dispose = false)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        var bsi = GetBufferedStream(inputStream);
        var bso = GetBufferedStream(outputStream, bsi.BufferSize);
        try
        {
            Span<int> eb = [0, 0, 0];
            var pos = 0;
            int i;
            while ((i = bsi.ReadByte()) != -1)
            {
                eb[0] |= i << eb[1];
                eb[1] += 8;
                if (eb[1] < 14)
                    continue;
                eb[2] = eb[0] & 8191;
                if (eb[2] > 88)
                {
                    eb[1] -= 13;
                    eb[0] >>= 13;
                }
                else
                {
                    eb[2] = eb[0] & 16383;
                    eb[1] -= 14;
                    eb[0] >>= 14;
                }
                WriteLine(bso, CharacterTable91[eb[2] % 91], lineLength, ref pos);
                WriteLine(bso, CharacterTable91[eb[2] / 91], lineLength, ref pos);
            }
            if (eb[1] == 0)
                return;
            WriteLine(bso, CharacterTable91[eb[0] % 91], lineLength, ref pos);
            if (eb[1] >= 8 || eb[0] >= 91)
                WriteLine(bso, CharacterTable91[eb[0] / 91], lineLength, ref pos);
        }
        finally
        {
            if (dispose)
            {
                bsi.Dispose();
                bso.Dispose();
            }
            else
            {
                bsi.Flush();
                bso.Flush();
            }
        }
    }

    /// <summary>Decodes the specified input stream into the specified output stream.</summary>
    /// <param name="inputStream">The input stream to decode.</param>
    /// <param name="outputStream">The output stream for decoding.</param>
    /// <param name="dispose"><see langword="true"/> to release all resources used by the input and output <see cref="Stream"/>; otherwise, <see langword="false"/>.</param>
    /// <exception cref="ArgumentNullException">inputStream or outputStream is null.</exception>
    /// <exception cref="ArgumentException">inputStream or outputStream is invalid.</exception>
    /// <exception cref="DecoderFallbackException">inputStream contains invalid characters.</exception>
    /// <exception cref="NotSupportedException">inputStream is not readable -or- outputStream is not writable.</exception>
    /// <exception cref="IOException">An I/O error occurred, such as the specified file cannot be found.</exception>
    /// <exception cref="ObjectDisposedException">Methods were called after the inputStream or outputStream was closed.</exception>
    public static void DecodeStream(Stream inputStream, Stream outputStream, bool dispose = false)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(outputStream);
        
        var bsi = GetBufferedStream(inputStream);
        var bso = GetBufferedStream(outputStream, bsi.BufferSize);
        try
        {
            var db = new[] { 0, -1, 0, 0 }.AsSpan();
            int i;
            while ((i = bsi.ReadByte()) != -1)
            {
                if (IsSkippable(i))
                    continue;

                var table91Idx = Array.IndexOf(CharacterTable91, (byte)i);
                if (table91Idx < 0)
                    throw new DecoderFallbackException($"Character {(char)i} is invalid!");
                db[0] = table91Idx;
                if (db[0] == -1)
                    continue;
                if (db[1] < 0)
                {
                    db[1] = db[0];
                    continue;
                }
                db[1] += db[0] * 91;
                db[2] |= db[1] << db[3];
                db[3] += (db[1] & 8191) > 88 ? 13 : 14;
                do
                {
                    bso.WriteByte((byte)(db[2] & byte.MaxValue));
                    db[2] >>= 8;
                    db[3] -= 8;
                }
                while (db[3] > 7);
                db[1] = -1;
            }
            if (db[1] != -1)
                bso.WriteByte((byte)((db[2] | (db[1] << db[3])) & byte.MaxValue));
        }
        finally
        {
            if (dispose)
            {
                bsi.Dispose();
                bso.Dispose();
            }
            else
            {
                bsi.Flush();
                bso.Flush();
            }
        }
    }
}