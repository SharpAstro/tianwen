using System.Text;
using System.Text.RegularExpressions;

namespace TianWen.Lib;

public static partial class StringHelper
{
    extension(string str)
    {
        public string? ReplaceNonPrintableWithHex()
        {
            var sb = new StringBuilder(str.Length + 4);
            foreach (var c in str)
            {
                if (char.IsControl(c))
                {
                    sb.AppendFormat("<{0:X2}>", (int)c);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public string PascalCaseStringToName() => PascalSplitter.Replace(str, " $1$2").TrimStart();
    }

    private static readonly Regex PascalSplitter = PascalSplitterPattern();

    [GeneratedRegex(@"([A-Z][(?:)a-z])|([0-9]+)", RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    internal static partial Regex PascalSplitterPattern();
}