using System;
using System.Text;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

public class Base91Tests
{
    [Fact]
    public void EncodeBytes_EmptyInput_ReturnsEmptyString()
    {
        Base91.EncodeBytes(ReadOnlySpan<byte>.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void DecodeBytes_EmptyString_ReturnsEmptyArray()
    {
        Base91.DecodeBytes("").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })]
    [InlineData(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F })] // "Hello"
    public void RoundTrip_SmallInputs(byte[] input)
    {
        var encoded = Base91.EncodeBytes(input);
        var decoded = Base91.DecodeBytes(encoded);
        decoded.ShouldBe(input);
    }

    [Fact]
    public void RoundTrip_AllByteValues()
    {
        var input = new byte[256];
        for (int i = 0; i < 256; i++)
            input[i] = (byte)i;

        var encoded = Base91.EncodeBytes(input);
        var decoded = Base91.DecodeBytes(encoded);
        decoded.ShouldBe(input);
    }

    [Fact]
    public void RoundTrip_LargerPayload()
    {
        var rng = new Random(42);
        var input = new byte[1024];
        rng.NextBytes(input);

        var encoded = Base91.EncodeBytes(input);
        var decoded = Base91.DecodeBytes(encoded);
        decoded.ShouldBe(input);
    }

    [Fact]
    public void EncodeBytes_ProducesOnlyValidCharacters()
    {
        var input = new byte[256];
        for (int i = 0; i < 256; i++)
            input[i] = (byte)i;

        var encoded = Base91.EncodeBytes(input);

        const string validChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$%&()*+,-./:;<=>?@[]^_`{|}~\"";
        foreach (var ch in encoded)
        {
            validChars.ShouldContain(ch.ToString());
        }
    }

    [Fact]
    public void DecodeBytes_SkipsWhitespace()
    {
        var input = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var encoded = Base91.EncodeBytes(input);

        // Insert whitespace
        var withWhitespace = $" \t{encoded[0]}\n{encoded[1..]}\r ";
        var decoded = Base91.DecodeBytes(withWhitespace);
        decoded.ShouldBe(input);
    }

    [Fact]
    public void DecodeBytes_ThrowsOnInvalidCharacter()
    {
        Should.Throw<DecoderFallbackException>(() => Base91.DecodeBytes("AB\x01CD"));
    }

    [Fact]
    public void DecodeBytes_ThrowsOnNull()
    {
        Should.Throw<ArgumentNullException>(() => Base91.DecodeBytes(null!));
    }

    [Fact]
    public void RoundTrip_SingleByte()
    {
        for (int b = 0; b < 256; b++)
        {
            var input = new byte[] { (byte)b };
            var decoded = Base91.DecodeBytes(Base91.EncodeBytes(input));
            decoded.ShouldBe(input, $"Failed round-trip for byte {b}");
        }
    }

    [Fact]
    public void EncodeBytes_IsShorterThanBase64ForLargeInputs()
    {
        var rng = new Random(123);
        var input = new byte[512];
        rng.NextBytes(input);

        var base91Encoded = Base91.EncodeBytes(input);
        var base64Encoded = Convert.ToBase64String(input);

        base91Encoded.Length.ShouldBeLessThan(base64Encoded.Length);
    }
}
