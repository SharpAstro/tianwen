using System.Text;

namespace TianWen.Lib;

public static class StringHelper
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
}
