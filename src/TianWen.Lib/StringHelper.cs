using System.Text;
using System.Text.RegularExpressions;

namespace TianWen.Lib;

public static partial class StringHelper
{
    public static string? ReplaceNonPrintableWithHex(this string str)
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

    static readonly Regex PascalSplitter = PascalSplitterPattern();

    public static string PascalCaseStringToName(this string str) => PascalSplitter.Replace(str, " $1$2").TrimStart();

    [GeneratedRegex(@"([A-Z][(?:)a-z])|([0-9]+)", RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    internal static partial Regex PascalSplitterPattern();
}