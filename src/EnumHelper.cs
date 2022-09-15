using System;
using System.Text.RegularExpressions;

namespace Astap.Lib;

public static class EnumHelper
{
    const int ASCIIMask = 0x7f;
    const int ASCIIBits = 7;
    const int MaxLenInASCII = 64 / ASCIIBits;

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


    static readonly Regex PascalSplitter = new("([A-Z])|([0-9]+)", RegexOptions.Compiled);

    public static string ToName<T>(this T constellation) where T : Enum => PascalSplitter.Replace(constellation.ToString(), " $1$2").TrimStart();
}
