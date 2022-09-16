using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Astap.Lib.Astrometry;

public static class Utils
{
    public static double HMSToDegree(string? hms)
    {
        const double minToDeg = 15 / 60.0;
        const double secToDeg = 15 / 3600.0;

        var split = hms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var hours)
            && double.TryParse(split[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return (15 * hours) + (min * minToDeg) + (sec * secToDeg);
        }
        else
        {
            return double.NaN;
        }
    }

    public static double DMSToDegree(string dms)
    {
        const double minToDeg = 15 / 60.0;
        const double secToDeg = 15 / 3600.0;

        var split = dms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var deg)
            && double.TryParse(split[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return deg + (min * minToDeg) + (sec * secToDeg);
        }
        else
        {
            return double.NaN;
        }
    }

    const RegexOptions CommonOpts = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace;

    static readonly Regex ExtendedCatalogEntryPattern = new(@"^(N|I|NGC|IC) ([0-9]{1,4}) (?:(N(?:ED)? ([0-9]{1,2})) | [_]?([A-Z]{1,2}))$", CommonOpts);

    static readonly Regex PSRDesignationPattern = new(@"^(?:PSR) ([BJ]) ([0-9]){4}([-+])([0-9]){2,4}$", CommonOpts);

    static readonly string PSRPrefix = EnumHelper.EnumValueToAbbreviation((ulong)Catalog.PSR);

    public static bool TryGetCleanedUpCatalogName(string? input, out CatalogIndex catalogIndex)
    {
        var (chars, digits, catalog) = GuessCatalogFormat(input, out var trimmedInput);

        if (digits <= 0 || catalog is null)
        {
            catalogIndex = 0;
            return false;
        }

        string? cleanedUp;
        // test for slow path
        var firstDigit = trimmedInput.IndexOfAny(new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
        if ((catalog == Catalog.NGC || catalog == Catalog.IC)
            && firstDigit > 0 && trimmedInput.IndexOfAny(new[] { 'A', 'B', 'C', 'D', 'E', 'F', 'S', 'W', 'N' }, firstDigit) > firstDigit)
        {
            var match = ExtendedCatalogEntryPattern.Match(trimmedInput);
            if (match.Success && match.Groups.Count == 6)
            {
                var NorI = match.Groups[1].ValueSpan[0..1];
                var number = match.Groups[2].ValueSpan;
                if (match.Groups[5].Length == 0)
                {
                    var nedGroupSuffix = match.Groups[4].ValueSpan;
                    cleanedUp = string.Concat(NorI, number, "N", nedGroupSuffix);
                }
                else
                {
                    var letterSuffix = match.Groups[5].ValueSpan;
                    cleanedUp = string.Concat(NorI, number, "_", letterSuffix);
                }
            }
            else
            {
                cleanedUp = null;
            }
        }
        else if (catalog == Catalog.PSR)
        {
            var match = PSRDesignationPattern.Match(trimmedInput);
            if (match.Success && match.Groups.Count == 5)
            {
                var BorJ = match.Groups[1].ValueSpan;
                var ra = match.Groups[2].ValueSpan;
                var decIsNeg = match.Groups[3].ValueSpan[0] == '-';
                var dec = match.Groups[4].ValueSpan;
                if (short.TryParse(ra, NumberStyles.None, CultureInfo.InvariantCulture, out var raVal)
                    && short.TryParse(dec, NumberStyles.None, CultureInfo.InvariantCulture, out var decVal))
                {
                    var idAsInt = (raVal & 0xfff) << 15 | (decVal & 0x3fff) << 1 | (decIsNeg ? 1 : 0);
                    var idAsIntH = IPAddress.HostToNetworkOrder(idAsInt);
                    cleanedUp = string.Concat(PSRPrefix, BorJ, Convert.ToBase64String(BitConverter.GetBytes(idAsIntH), Base64FormattingOptions.None).TrimEnd('='));
                }
                else
                {
                    cleanedUp = null;
                }
            }
            else
            {
                cleanedUp = null;
            }
        }
        else if (chars.Length <= EnumHelper.MaxLenInASCII)
        {
            int foundDigits = 0;
            for (var i = 0; i < digits; i++)
            {
                var inputIndex = trimmedInput.Length - i - 1;
                if (inputIndex <= 0)
                {
                    break;
                }
                var tmplIndex = chars.Length - 1 - i;
                var tmplChar = chars[tmplIndex];
                var inputChar = trimmedInput[inputIndex];
                var isDigit = inputChar is >= '0' and <= '9';
                var isABC = inputChar is >= 'A' and <= 'Z';
                if (tmplChar == '*')
                {
                    // treat * as a wildcard either meaning 0-9 or A-Z
                    if (!isDigit && !isABC)
                    {
                        break;
                    }
                }
                else if (tmplChar == '0')
                {
                    if (!isDigit)
                    {
                        break;
                    }
                }
                else if (tmplChar != inputChar)
                {
                    break;
                }

                foundDigits++;
                chars[tmplIndex] = inputChar;
            }

            cleanedUp = foundDigits > 0 ? new string(chars) : null;
        }
        else
        {
            cleanedUp = null;
        }

        if (cleanedUp is not null)
        {
            catalogIndex = EnumHelper.AbbreviationToEnumMember<CatalogIndex>(cleanedUp);
            return true;
        }
        else
        {
            catalogIndex = 0;
            return false;
        }
    }

    /// <summary>
    /// Tries to guess the <see cref="Catalog"/> and format from user input.
    /// </summary>
    /// <param name="input">input to guess catalog format from</param>
    /// <param name="trimmedInput"></param>
    /// <returns>(catalog index template, number of free digits, guessed <see cref="Catalog"/>)</returns>
    public static (char[] template, int digits, Catalog? catalog) GuessCatalogFormat(string? input, out string trimmedInput)
    {
        trimmedInput = input?.Replace(" ", "") ?? "";
        if (string.IsNullOrEmpty(trimmedInput) || trimmedInput.Length < 2)
        {
            return (Array.Empty<char>(), 0, null);
        }

        return trimmedInput[0] switch
        {
            'A' => (new char[7] { 'A', 'C', 'O', '0', '0', '0', '0' }, 4, Catalog.Abell),
            'B' => (new char[4] { 'B', '0', '0', '0' }, 3, Catalog.Barnard),
            'C' => trimmedInput[1] == 'l' || trimmedInput[1] == 'r'
                ? (new char[5] { 'C', 'r', '0', '0', '0' }, 3, Catalog.Collinder)
                : (new char[4] { 'C', '0', '0', '0' }, 3, Catalog.Caldwell),
            'E' => (new char[8] { 'E', '0', '0', '0', '-', '0', '0', '0' }, 7, Catalog.ESO),
            'G' => trimmedInput[1] == 'U'
                ? (new char[6] { 'G', 'U', 'M', '0', '0', '0' }, 3, Catalog.GUM)
                : (new char[6] { 'G', 'J', '0', '0', '0', '0' }, 4, Catalog.GJ),
            'H' => trimmedInput[1] switch
            {
                'A' => trimmedInput.Length > 4 && trimmedInput[3] == 'S'
                    ? (new char[7] { 'H', 'A', 'T', 'S', '0', '0', '0' }, 3, Catalog.HATS)
                    : (new char[8] { 'H', 'A', 'T', '-', 'P', '0', '0', '0'}, 3, Catalog.HAT_P),
                'C' => (new char[7] { 'H', 'C', 'G', '0', '0', '0', '0' }, 4, Catalog.HCG),
                'R' => (new char[6] { 'H', 'R', '0', '0', '0', '0'}, 4, Catalog.HR),
                'D' => (new char[8] { 'H', 'D', '0', '0', '0', '0', '0', '0'}, 6, Catalog.HD),
                'I' => (new char[8] { 'H', 'I', '0', '0', '0', '0', '0', '0'}, 6, Catalog.HIP),
                _ => (new char[3] { 'H', '0', '0' }, 2, Catalog.H)
            },
            'I' => (new char[5] { 'I', '0', '0', '0', '0' }, 4, Catalog.IC),
            'M' => trimmedInput[1] == 'e' && trimmedInput.Length > 2 && trimmedInput[2] == 'l'
                ? (new char[6] { 'M', 'e', 'l', '0', '0', '0' }, 3, Catalog.Melotte)
                : (new char[4] { 'M', '0', '0', '0' }, 3, Catalog.Messier),
            'N' => (new char[5] { 'N', '0', '0', '0', '0' }, 4, Catalog.NGC),
            'P' => trimmedInput[1] == 'S' && trimmedInput.Length > 2 && trimmedInput[2] == 'R'
                ? (new char[12] { 'P', 'S', 'R', '$', '0', '0', '0', '#', '0', '0', '0', '0'}, 8, Catalog.PSR)
                : (Array.Empty<char>(), 0, null),
            'U' => (new char[5] { 'U', '0', '0', '0', '0' }, 5, Catalog.UGC),
            'W' => (new char[7] { 'W', 'A', 'S', 'P', '0', '0', '0'}, 4, Catalog.WASP),
            'X' => (new char[6] { 'X', 'O', '0', '0', '0', '*' }, 4, Catalog.XO),
            _ => (Array.Empty<char>(), 0, null)
        };
    }
}
