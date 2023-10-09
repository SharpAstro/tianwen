using Roydl.Text.BinaryToText;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Astap.Lib;

public static class EnumHelper
{
    internal const uint ASCIIMask = 0x7f;
    internal const uint ByteMask = 0xff;
    internal const int ASCIIBits = 7;
    internal const int BitsInUlong = 64;
    internal const int BytesInUlong = BitsInUlong / 8;
    internal const int MaxLenInASCII = BitsInUlong / ASCIIBits;
    internal const ulong MSBUlongMask = 1ul << (BitsInUlong - 1);

    internal static readonly Base91 Base91Encoder = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static T AbbreviationToEnumMember<T>(ReadOnlySpan<char> name)
        where T : struct, Enum
    {
        if (name.Length is <= 0)
        {
            return default;
        }

        return (T)Enum.ToObject(typeof(T), AbbreviationToASCIIPackedInt(name));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static T PrefixedNumericToASCIIPackedInt<T>(ulong prefix, int number, int digits)
        where T : struct, Enum
    {
        if (digits > MaxLenInASCII)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), digits, $"Must not exceed {MaxLenInASCII} digits");
        }

        var val = prefix << (digits * ASCIIBits);
        val |= AbbreviationToASCIIPackedInt(number.ToString(new string('0', digits), CultureInfo.InvariantCulture));
        return (T)Enum.ToObject(typeof(T), val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static ulong AbbreviationToASCIIPackedInt(ReadOnlySpan<char> name)
    {
        var len = Math.Min(MaxLenInASCII, name.Length);
        var msbMask = len == MaxLenInASCII ? ByteMask : ASCIIMask;
        ulong val = (name[0] & msbMask);
        for (var i = 1; i < len; i++)
        {
            val <<= ASCIIBits;
            val |= (name[i] & ASCIIMask);
        }
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static string EnumValueToAbbreviation(ulong value)
    {
        var msb = (value & MSBUlongMask) == MSBUlongMask;

        Span<char> chars = stackalloc char[MaxLenInASCII];
        int i;
        for (i = 0; i < MaxLenInASCII; i++)
        {
            var current = (char)(value & ASCIIMask);
            if (current == 0)
            {
                break;
            }
            value >>= ASCIIBits;
            chars[MaxLenInASCII - i - 1] = current;
        }

        if (msb)
        {
            chars[0] |= (char)(1 << ASCIIBits);
            return new string(chars);
        }
        else
        {
            return new string(chars.Slice(MaxLenInASCII - i, i));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static ulong EnumValueToNumeric(ulong enumValue)
    {
        var msb = (enumValue & MSBUlongMask) == MSBUlongMask;

        if (msb)
        {
            throw new ArgumentException("Enum values with MSB set are not supported", nameof(enumValue));
        }

        var numeric = 0ul;
        var factor = 1;
        for (var i = 0; i < MaxLenInASCII; i++)
        {
            var current = (char)(enumValue & ASCIIMask);
            if (current == 0)
            {
                break;
            }

            numeric += (ulong)(factor * (current - '0'));
            factor *= 10;

            enumValue >>= ASCIIBits;
        }

        return numeric;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool FindCharInValue(ulong value, char @char)
    {
        if ((value & MSBUlongMask) == MSBUlongMask)
        {
            return false;
        }

        for (var i = 0; i < MaxLenInASCII; i++)
        {
            var masked = (value & ASCIIMask);
            if (masked == @char)
            {
                return true;
            }
            else if (masked == 0)
            {
                return false;
            }
            value >>= ASCIIBits;
        }

        return false;
    }

    const RegexOptions CommonOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace;
    static readonly Regex PascalSplitter = new(@"([A-Z][(?:)a-z])|([0-9]+)", CommonOptions);

    public static string PascalCaseStringToName<T>(this T @enum) where T : struct, Enum => PascalSplitter.Replace(@enum.ToString(), " $1$2").TrimStart();
}
