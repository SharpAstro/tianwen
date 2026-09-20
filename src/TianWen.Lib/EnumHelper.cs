using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace TianWen.Lib;

public static class EnumHelper
{
    internal const uint ASCIIMask = 0x7f;
    internal const uint ByteMask = 0xff;
    internal const int ASCIIBits = 7;
    internal const int BitsInUlong = BytesInUlong * 8;
    internal const int BytesInUlong = sizeof(ulong);
    internal const int MaxLenInASCII = BitsInUlong / ASCIIBits;
    internal const ulong MSBUlongMask = 1ul << (BitsInUlong - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static T AbbreviationToEnumMember<T>(in ReadOnlySpan<char> name)
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
    public static ulong AbbreviationToASCIIPackedInt(in ReadOnlySpan<char> name)
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
        Span<char> chars = stackalloc char[MaxLenInASCII];
        var len = EnumValueToAbbreviation(value, chars);
        return new string(chars[..len]);
    }

    /// <summary>
    /// Writes the abbreviation into a caller-supplied buffer of at least <see cref="MaxLenInASCII"/>
    /// chars and returns the number written, so a caller in a loop can <c>stackalloc</c> once instead
    /// of allocating a string per item. This IS the string overload's body, so the two cannot drift.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int EnumValueToAbbreviation(ulong value, Span<char> output)
    {
        var msb = (value & MSBUlongMask) == MSBUlongMask;

        var chars = output[..MaxLenInASCII];
        chars.Clear();
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
            return MaxLenInASCII;
        }

        // Span.CopyTo is defined for overlapping ranges, so the trailing chars slide to the front.
        chars.Slice(MaxLenInASCII - i, i).CopyTo(chars);
        return i;
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

    public static string PascalCaseStringToName<T>(this T @enum) where T : struct, Enum => @enum.ToString().PascalCaseStringToName();
}
