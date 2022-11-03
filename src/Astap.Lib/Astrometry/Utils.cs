using System;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry;

public static class Utils
{
    public static double HMSToHours(string? hms)
    {
        const double minToHours = 1.0 / 60.0;
        const double secToHours = 1.0 / 3600.0;

        var split = hms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return Math.FusedMultiplyAdd(sec, secToHours, Math.FusedMultiplyAdd(min, minToHours, hours));
        }
        else
        {
            return double.NaN;
        }
    }

    public static double HMSToDegree(string? hms)
    {
        const double minToDegs = 15.0 / 60.0;
        const double secToDegs = 15.0 / 3600.0;

        var split = hms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return Math.FusedMultiplyAdd(sec, secToDegs, Math.FusedMultiplyAdd(min, minToDegs, hours * 15.0));
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

    static readonly Regex WDSPattern = new(@"^(?:WDS) ([J]) ([0-9]{5}) ([-+]) ([0-9]{4})$", CommonOpts);

    internal const uint PSRRaMask = 0xfff;
    internal const int PSRRaShift = 14;
    internal const uint PSRDecMask = 0x3fff;
    internal const Base91EncRADecOptions PSREpochSupport = Base91EncRADecOptions.None;

    internal const uint TwoMassRaMask = 0x1_fff_fff;
    internal const int TwoMassRaShift = 24;
    internal const uint TwoMassDecMask = 0xfff_fff;
    internal const Base91EncRADecOptions TwoMassEncOptions = Base91EncRADecOptions.ImpliedJ2000;

    internal const uint WDSRaMask = 0x7fff;
    internal const int WDSRaShift = 14;
    internal const uint WDSDecMask = 0x3fff;
    internal const Base91EncRADecOptions WDSEncOptions = Base91EncRADecOptions.ImpliedJ2000;

    public static bool TryGetCleanedUpCatalogName(string? input, out CatalogIndex catalogIndex)
    {
        var (template, digits, maybeCatalog) = GuessCatalogFormat(input, out var trimmedInput);

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
            cleanedUp = CleanupRADecBasedCatalogIndex(TwoMassAnd2MassXPattern, trimmedInput, catalog, TwoMassRaMask, TwoMassRaShift, TwoMassDecMask, TwoMassEncOptions);
            isBase91Encoded = true;
        }
        else if (catalog is Catalog.PSR)
        {
            cleanedUp = CleanupRADecBasedCatalogIndex(PSRDesignationPattern, trimmedInput, catalog, PSRRaMask, PSRRaShift, PSRDecMask, PSREpochSupport);
            isBase91Encoded = true;
        }
        else if (catalog is Catalog.WDS)
        {
            cleanedUp = CleanupRADecBasedCatalogIndex(WDSPattern, trimmedInput, catalog, WDSRaMask, WDSRaShift, WDSDecMask, WDSEncOptions);
            isBase91Encoded = true;
        }
        else
        {
            Span<char> chars = stackalloc char[template.Length];
            template.CopyTo(chars);
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
    static string? CleanupRADecBasedCatalogIndex(Regex pattern, string trimmedInput, Catalog catalog, ulong raMask, int raShift, ulong decMask, Base91EncRADecOptions epochSupport)
    {
        var match = pattern.Match(trimmedInput);

        var j2000implied = epochSupport.HasFlag(Base91EncRADecOptions.ImpliedJ2000);

        if (!match.Success || match.Groups.Count < 5)
        {
            return null;
        }

        var isJ2000 = match.Groups[1].ValueSpan[0] == 'J';
        var ra = match.Groups[2].ValueSpan;
        var decIsNeg = match.Groups[3].ValueSpan[0] == '-';
        var dec = match.Groups[4].ValueSpan;
        if (ulong.TryParse(ra, NumberStyles.None, CultureInfo.InvariantCulture, out var raVal)
            && ulong.TryParse(dec, NumberStyles.None, CultureInfo.InvariantCulture, out var decVal))
        {
            const int signShift = ASCIIBits;
            var epochShift = signShift + (!j2000implied ? 1 : 0);
            var decShift = epochShift + 1;
            var idAsLongH =
                  (raVal & raMask) << (raShift + decShift)
                | (decVal & decMask) << decShift
                | (isJ2000 && !j2000implied ? 1ul : 0ul) << epochShift
                | (decIsNeg ? 1ul : 0ul) << signShift
                | ((ulong)catalog & ASCIIMask);
            var idAsLongN = IPAddress.HostToNetworkOrder((long)idAsLongH);
            var bytesN = BitConverter.GetBytes(idAsLongN);

            return Base91Encoder.EncodeBytes(bytesN[1..]);
        }

        return null;
    }

    /// <summary>
    /// Tries to guess the <see cref="Catalog"/> and format from user input.
    /// </summary>
    /// <param name="input">input to guess catalog format from</param>
    /// <param name="trimmedInput"></param>
    /// <returns>(catalog index template, number of free digits, guessed <see cref="Catalog"/>)</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (string template, int digits, Catalog? catalog) GuessCatalogFormat(string? input, out string trimmedInput)
    {
        trimmedInput = input?.Replace(" ", "") ?? "";
        if (string.IsNullOrEmpty(trimmedInput) || trimmedInput.Length < 2)
        {
            return ("", 0, null);
        }

        return trimmedInput[0] switch
        {
            '2' => trimmedInput.Length > 5 && trimmedInput[4] == 'S'
                ? ("", 15, Catalog.TwoMass)
                : trimmedInput.Length > 5 && trimmedInput[4] == 'X'
                    ? ("", 15, Catalog.TwoMassX)
                    : ("", 0, null),
            'A' => ("ACO0000", 4, Catalog.Abell),
            'B' => ("B000", 3, Catalog.Barnard),
            'C' => trimmedInput[1] == 'l' || trimmedInput[1] == 'r'
                ? ("Cr000", 3, Catalog.Collinder)
                : ("C000", 3, Catalog.Caldwell),
            'E' => ("E000-000", 7, Catalog.ESO),
            'G' => trimmedInput[1] == 'U'
                ? ("GUM000", 3, Catalog.GUM)
                : ("GJ0000", 4, Catalog.GJ),
            'H' => trimmedInput[1] switch
            {
                'A' => trimmedInput.Length > 4 && trimmedInput[3] == 'S'
                    ? ("HATS000", 3, Catalog.HATS)
                    : ("HAT-P000", 3, Catalog.HAT_P),
                'C' => ("HCG0000", 4, Catalog.HCG),
                'R' => ("HR0000", 4, Catalog.HR),
                'D' => ("HD000000", 6, Catalog.HD),
                'I' => ("HI000000", 6, Catalog.HIP),
                _ => ("H00", 2, Catalog.H)
            },
            'I' => trimmedInput[1] is >= '0' and <= '9' || trimmedInput[1] is 'C' or 'c'
                ? ("I0000", 4, Catalog.IC)
                : ("", 0, null),
            'M' => trimmedInput[1] == 'e' && trimmedInput.Length > 2 && trimmedInput[2] == 'l'
                ? ("Mel000", 3, Catalog.Melotte)
                : ("M000", 3, Catalog.Messier),
            'N' => ("N0000", 4, Catalog.NGC),
            'P' => trimmedInput[1] == 'S' && trimmedInput.Length > 2 && trimmedInput[2] == 'R'
                ? ("", 8, Catalog.PSR)
                : ("", 0, null),
            'S' => ("Sh2-000", 3, Catalog.Sharpless),
            'T' => ("TrES00", 2, Catalog.TrES),
            'U' => ("U00000", 5, Catalog.UGC),
            'W' => trimmedInput[1] == 'D' && trimmedInput.Length > 2 && trimmedInput[2] == 'S'
                ? ("", 10, Catalog.WDS)
                : ("WASP000", 3, Catalog.WASP),
            'X' => ("XO000*", 4, Catalog.XO),
            _ => ("", 0, null)
        };
    }
}
