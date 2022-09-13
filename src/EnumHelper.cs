using System;
using System.Text.RegularExpressions;

namespace Astap.Lib
{
    public static class EnumHelper
    {
        public static T AbbreviationToEnumMember<T>(string name)
            where T : Enum
        {
            var len = name.Length;
            ulong val = 0;
            for (var i = 0; i < name.Length; i++)
            {
                val |= (ulong)(name[i] & 0xff) << (len - i - 1) * 8;
            }
            return (T)Enum.ToObject(typeof(T), val);
        }

        public static string EnumValueToAbbreviation(ulong value)
        {
            var chars = new char[8];
            int i;
            const int len = 8;
            for (i = 0; i < len; i++)
            {
                var current = (char)(value & 0xff);
                if (current == 0)
                {
                    break;
                }
                value >>= 8;
                chars[len - i - 1] = current;
            }

            return new string(chars, len - i, i);
        }


        static readonly Regex PascalSplitter = new("([A-Z])|([0-9]+)", RegexOptions.Compiled);

        public static string ToName<T>(this T constellation) where T : Enum => PascalSplitter.Replace(constellation.ToString(), " $1$2").TrimStart();
    }
}
