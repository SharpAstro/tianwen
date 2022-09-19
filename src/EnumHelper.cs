using System;
using System.Runtime.CompilerServices;

namespace Astap.Lib;

public static class EnumHelper
{
    internal const int ASCIIMask = 0x7f;
    internal const int ASCIIBits = 7;
    internal const int BitsInUlong = 64;
    internal const int MaxLenInASCII = BitsInUlong / ASCIIBits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T AbbreviationToEnumMember<T>(string name)
        where T : Enum
    {
        var len = Math.Min(MaxLenInASCII, name.Length);
        ulong val = 0;
        for (var i = 0; i < len; i++)
        {
            val |= (ulong)(name[i] & ASCIIMask) << (len - i - 1) * ASCIIBits;
        }
        return (T)Enum.ToObject(typeof(T), val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string EnumValueToAbbreviation(ulong value)
    {
        var chars = new char[MaxLenInASCII];
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

        return new string(chars, MaxLenInASCII - i, i);
    }
}
