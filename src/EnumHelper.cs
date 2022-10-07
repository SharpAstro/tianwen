using Roydl.Text.BinaryToText;
using System;
using System.Runtime.CompilerServices;

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
    public static T AbbreviationToEnumMember<T>(string name)
        where T : struct, Enum
    {
        var len = Math.Min(MaxLenInASCII, name.Length);

        if (len is <= 0)
        {
            return default;
        }

        var msbMask = len == MaxLenInASCII ? ByteMask : ASCIIMask;
        ulong val = (ulong)(name[0] & msbMask) << (len - 1) * ASCIIBits;
        for (var i = 1; i < len; i++)
        {
            val |= (ulong)(name[i] & ASCIIMask) << (len - i - 1) * ASCIIBits;
        }

        return (T)Enum.ToObject(typeof(T), val);
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
        }

        return new string(chars.Slice(MaxLenInASCII - i, i));
    }
}
