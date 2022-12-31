using System;
using System.Text;

namespace Astap.Lib;

public static class Base64UrlSafe
{
    private static readonly UTF8Encoding _utf8encoding = new(false);

    public static string Utf8Base64UrlEncode(ReadOnlySpan<char> chars)
    {
        var max = _utf8encoding.GetMaxByteCount(chars.Length);
        Span<byte> bytes = max > 128 ? new byte[max] : stackalloc byte[max];
        var actual = _utf8encoding.GetBytes(chars, bytes);

        return Base64UrlEncode(bytes[..actual]);
    }

    public static string Utf8Base64UrlDecode(string? encoded) => string.IsNullOrWhiteSpace(encoded) ? "" : _utf8encoding.GetString(Base64UrlDecode(encoded));

    /// <summary>
    /// 62nd char of encoding + replaced with -
    /// 63nd char of encoding / replaced with _
    /// Removes padding
    /// </summary>
    /// <param name="bytes">input bytes</param>
    /// <returns>url safe encoded base64 string</returns>
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string encoded)
        => Convert.FromBase64String(
            encoded.Replace('-', '+').Replace('_', '/') + (encoded.Length % 4) switch
            {
                0 => "",
                2 => "==",
                3 => "=",
                _ => throw new ArgumentException($"url base64 encoded string {encoded} is not valid (padding could not be calculated)", nameof(encoded))
            });
}
