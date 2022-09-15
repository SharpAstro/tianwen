using System;
using System.Text.RegularExpressions;

namespace Astap.Lib;

public static class EnumHelper
{
    const int Mask = 0x7f;
    const int Bits = 7;
    const int MaxLen = 64 / Bits;

    public static T AbbreviationToEnumMember<T>(string name)
        where T : Enum
    {
        var len = Math.Min(MaxLen, name.Length);
        ulong val = 0;
        for (var i = 0; i < name.Length; i++)
        {
            val |= (ulong)(name[i] & Mask) << (len - i - 1) * Bits;
        }
        return (T)Enum.ToObject(typeof(T), val);
    }

    public static string EnumValueToAbbreviation(ulong value)
    {
        var chars = new char[MaxLen];
        int i;
        for (i = 0; i < MaxLen; i++)
        {
            var current = (char)(value & Mask);
            if (current == 0)
            {
                break;
            }
            value >>= Bits;
            chars[MaxLen - i - 1] = current;
        }

        return new string(chars, MaxLen - i, i);
    }


    static readonly Regex PascalSplitter = new("([A-Z])|([0-9]+)", RegexOptions.Compiled);

    public static string ToName<T>(this T constellation) where T : Enum => PascalSplitter.Replace(constellation.ToString(), " $1$2").TrimStart();
}
