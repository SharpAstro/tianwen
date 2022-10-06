using System;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry;

public static class Utils
{
    public static double HMSToDegree(string? hms)
    {
        const double minToDeg = 15.0 / 60.0;
        const double secToDeg = 15.0 / 3600.0;

        var split = hms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec)
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
        const double minToDeg = 1.0 / 60.0;
        const double secToDeg = 1.0 / 3600.0;

        var split = dms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var deg)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return Math.Sign(deg) * (Math.Abs(deg) + (min * minToDeg) + (sec * secToDeg));
        }
        else
        {
            return double.NaN;
        }
    }

    const RegexOptions CommonOpts = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace;

    static readonly Regex ExtendedCatalogEntryPattern = new(@"^(N|I|NGC|IC) ([0-9]{1,4}) (?:(N(?:ED)? ([0-9]{1,2})) | [_]?([A-Z]{1,2}))$", CommonOpts);

    static readonly Regex PSRDesignationPattern = new(@"^(?:PSR) ([BJ]) ([0-9]{4}) ([-+]) ([0-9]{2,4})$", CommonOpts);

    static readonly Regex TwoMassAnd2MassXPattern = new(@"^(?:2MAS[SX]) ([J]) ([0-9]{8}) ([-+]) ([0-9]{7})$", CommonOpts);

    internal const int PSRRaMask = 0xfff;
    internal const int PSRRaShift = 14;
    internal const int PSRDecMask = 0x3fff;

    internal const int TwoMassRaMask = 0x1_fff_fff;
    internal const int TwoMassRaShift = 24;
    internal const int TwoMassDecMask = 0xfff_fff;

    public static bool TryGetCleanedUpCatalogName(string? input, out CatalogIndex catalogIndex)
    {
        var (chars, digits, maybeCatalog) = GuessCatalogFormat(input, out var trimmedInput);

        if (digits <= 0 || !maybeCatalog.HasValue)
        {
            catalogIndex = 0;
            return false;
        }

        var catalog = maybeCatalog.Value;

        string? cleanedUp;
        bool isBase91Encoded;
        // test for slow path
        var firstDigit = trimmedInput.IndexOfAny(new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
        if (catalog is Catalog.NGC or Catalog.IC
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
            isBase91Encoded = false;
        }
        else if (catalog is Catalog.TwoMass or Catalog.TwoMassX)
        {
            cleanedUp = CleanupRADecBasedCatalogIndex(TwoMassAnd2MassXPattern, trimmedInput, catalog, TwoMassRaMask, TwoMassRaShift, TwoMassDecMask, false);
            isBase91Encoded = true;
        }
        else if (catalog is Catalog.PSR)
        {
            cleanedUp = CleanupRADecBasedCatalogIndex(PSRDesignationPattern, trimmedInput, catalog, PSRRaMask, PSRRaShift, PSRDecMask, true);
            isBase91Encoded = true;
        }
        else if (chars.Length <= MaxLenInASCII)
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
            isBase91Encoded = false;
        }
        else
        {
            isBase91Encoded = false;
            cleanedUp = null;
        }

        if (cleanedUp is not null && cleanedUp.Length <= MaxLenInASCII)
        {
            catalogIndex = (CatalogIndex)(isBase91Encoded ? (1L << 63) : 0L) | AbbreviationToEnumMember<CatalogIndex>(cleanedUp);
            return true;
        }
        else
        {
            catalogIndex = 0;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    static string? CleanupRADecBasedCatalogIndex(Regex pattern, string trimmedInput, Catalog catalog, long raMask, int raShift, long decMask, bool supportEpoch)
    {
        var match = pattern.Match(trimmedInput);

        if (!match.Success || match.Groups.Count != 5)
        {
            return null;
        }

        var isJ2000 = match.Groups[1].ValueSpan[0] == 'J';
        var ra = match.Groups[2].ValueSpan;
        var decIsNeg = match.Groups[3].ValueSpan[0] == '-';
        var dec = match.Groups[4].ValueSpan;
        if (long.TryParse(ra, NumberStyles.None, CultureInfo.InvariantCulture, out var raVal)
            && long.TryParse(dec, NumberStyles.None, CultureInfo.InvariantCulture, out var decVal))
        {
            const int signShift = ASCIIBits;
            var epochShift = signShift + (supportEpoch ? 1 : 0);
            var decShift = epochShift + 1;
            var idAsLongH =
                  (raVal & raMask) << (raShift + decShift)
                | (decVal & decMask) << decShift
                | (isJ2000 && supportEpoch ? 1L : 0L) << epochShift
                | (decIsNeg ? 1L : 0L) << signShift
                | ((long)catalog & ASCIIMask);
            var idAsLongN = IPAddress.HostToNetworkOrder(idAsLongH);
            var bytesN = BitConverter.GetBytes(idAsLongN);
            var asBase91N = Base91Encoder.EncodeBytes(bytesN[1..]);
            return asBase91N;
        }

        return null;
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
            '2' => trimmedInput.Length > 5 && trimmedInput[4] == 'S'
                ? (Array.Empty<char>(), 15, Catalog.TwoMass)
                : trimmedInput.Length > 5 && trimmedInput[4] == 'X'
                    ? (Array.Empty<char>(), 15, Catalog.TwoMassX)
                    : (Array.Empty<char>(), 0, null),
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
            'I' => trimmedInput[1] is >= '0' and <= '9' || trimmedInput[1] is 'C' or 'c'
                ? (new char[5] { 'I', '0', '0', '0', '0' }, 4, Catalog.IC)
                : (Array.Empty<char>(), 0, null),
            'M' => trimmedInput[1] == 'e' && trimmedInput.Length > 2 && trimmedInput[2] == 'l'
                ? (new char[6] { 'M', 'e', 'l', '0', '0', '0' }, 3, Catalog.Melotte)
                : (new char[4] { 'M', '0', '0', '0' }, 3, Catalog.Messier),
            'N' => (new char[5] { 'N', '0', '0', '0', '0' }, 4, Catalog.NGC),
            'P' => trimmedInput[1] == 'S' && trimmedInput.Length > 2 && trimmedInput[2] == 'R'
                ? (Array.Empty<char>(), 8, Catalog.PSR)
                : (Array.Empty<char>(), 0, null),
            'S' => (new char[7] { 'S', 'h', '2', '-', '0', '0', '0'}, 3, Catalog.Sharpless),
            'T' => (new char[6] { 'T', 'r', 'E', 'S', '0', '0' }, 2, Catalog.TrES),
            'U' => (new char[6] { 'U', '0', '0', '0', '0', '0' }, 5, Catalog.UGC),
            'W' => (new char[7] { 'W', 'A', 'S', 'P', '0', '0', '0'}, 3, Catalog.WASP),
            'X' => (new char[6] { 'X', 'O', '0', '0', '0', '*' }, 4, Catalog.XO),
            _ => (Array.Empty<char>(), 0, null)
        };
    }
}
