using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry.Catalogs;

public static class CatalogUtils
{
    const RegexOptions CommonOpts = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace;

    static readonly Regex BDPattern = new(@"(?:BD) \s* ([-+]) ([0-9]{1,2}) (?:\s*|[-_]) ([0-9]{1,4})", CommonOpts);

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

    static readonly char[] Digit = new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
    static readonly char[] NGCExt = new[] { 'A', 'B', 'C', 'D', 'E', 'F', 'S', 'W', 'N' };

    public static bool TryGetCleanedUpCatalogName(string? input, out CatalogIndex catalogIndex)
    {
        if (!TryGuessCatalogFormat(input, out var trimmedInput, out var template, out var digits, out var catalog))
        {
            catalogIndex = 0;
            return false;
        }

        string? cleanedUp;
        bool isBase91Encoded;
        // test for slow path
        var firstDigit = trimmedInput.IndexOfAny(Digit);

        switch (catalog)
        {
            case Catalog.NGC or Catalog.IC when firstDigit > 0 && trimmedInput.IndexOfAny(NGCExt, firstDigit) > firstDigit:
                {
                    var extendedNgcMatch = ExtendedCatalogEntryPattern.Match(trimmedInput);
                    if (extendedNgcMatch.Success && extendedNgcMatch.Groups.Count == 6)
                    {
                        var NorI = extendedNgcMatch.Groups[1].ValueSpan[0..1];
                        var number = extendedNgcMatch.Groups[2].ValueSpan;
                        if (extendedNgcMatch.Groups[5].Length == 0)
                        {
                            var nedGroupSuffix = extendedNgcMatch.Groups[4].ValueSpan;
                            cleanedUp = string.Concat(NorI, number, "N", nedGroupSuffix);
                        }
                        else
                        {
                            var letterSuffix = extendedNgcMatch.Groups[5].ValueSpan;
                            cleanedUp = string.Concat(NorI, number, "_", letterSuffix);
                        }
                    }
                    else
                    {
                        cleanedUp = null;
                    }
                    isBase91Encoded = false;
                }
                break;

            case Catalog.TwoMass:
            case Catalog.TwoMassX:
                cleanedUp = CleanupRADecBasedCatalogIndex(TwoMassAnd2MassXPattern, trimmedInput, catalog, TwoMassRaMask, TwoMassRaShift, TwoMassDecMask, TwoMassEncOptions);
                isBase91Encoded = true;
                break;

            case Catalog.PSR:
                cleanedUp = CleanupRADecBasedCatalogIndex(PSRDesignationPattern, trimmedInput, catalog, PSRRaMask, PSRRaShift, PSRDecMask, PSREpochSupport);
                isBase91Encoded = true;
                break;

            case Catalog.WDS:
                cleanedUp = CleanupRADecBasedCatalogIndex(WDSPattern, trimmedInput, catalog, WDSRaMask, WDSRaShift, WDSDecMask, WDSEncOptions);
                isBase91Encoded = true;
                break;

            case Catalog.BonnerDurchmusterung:
                var bdMatch = BDPattern.Match(trimmedInput);
                // TODO temporary, should use B91 encoding
                cleanedUp = bdMatch.Success
                    ? string.Concat(Catalog.BonnerDurchmusterung.ToAbbreviation(), bdMatch.Groups[3].Value, bdMatch.Groups[1].Value, bdMatch.Groups[2].Value)
                    : null;
                isBase91Encoded = false;
                break;

            default:
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
                break;
        }

        if (cleanedUp is not null && cleanedUp.Length <= MaxLenInASCII)
        {
            catalogIndex = (CatalogIndex)(isBase91Encoded ? 1L << 63 : 0L) | AbbreviationToEnumMember<CatalogIndex>(cleanedUp);
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
                  (raVal & raMask) << raShift + decShift
                | (decVal & decMask) << decShift
                | (isJ2000 && !j2000implied ? 1ul : 0ul) << epochShift
                | (decIsNeg ? 1ul : 0ul) << signShift
                | (ulong)catalog & ASCIIMask;

            Span<byte> bytesN = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytesN, idAsLongH);

            // TODO update lib to accept spans
            return Base91Encoder.EncodeBytes(bytesN[1..].ToArray());
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
    public static bool TryGuessCatalogFormat(string? input, out string trimmedInput, out string template, out int digits, out Catalog catalog)
    {
        trimmedInput = input?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmedInput) || trimmedInput.Length < 2)
        {
            template = "";
            digits = 0;
            catalog = 0;
            return false;
        }

        var noSpaces = trimmedInput.Replace(" ", "");

        (template, digits, catalog) = noSpaces[0] switch
        {
            '2' => noSpaces.Length > 5 && noSpaces[4] == 'S'
                ? ("", 15, Catalog.TwoMass)
                : noSpaces.Length > 5 && noSpaces[4] == 'X'
                    ? ("", 15, Catalog.TwoMassX)
                    : ("", 0, 0),
            'A' => ("ACO0000", 4, Catalog.Abell),
            'B' => noSpaces[1] == 'D'
                ? ("BD+00 0000", 6, Catalog.BonnerDurchmusterung)
                : ("B000", 3, Catalog.Barnard),
            'C' => noSpaces[1] == 'l' || noSpaces[1] == 'r'
                ? ("Cr000", 3, Catalog.Collinder)
                : ("C000", 3, Catalog.Caldwell),
            'E' => ("E000-000", 7, Catalog.ESO),
            'G' => noSpaces[1] == 'U'
                ? ("GUM000", 3, Catalog.GUM)
                : ("GJ0000", 4, Catalog.GJ),
            'H' => noSpaces[1] switch
            {
                'A' => noSpaces.Length > 4 && noSpaces[3] == 'S'
                    ? ("HATS000", 3, Catalog.HATS)
                    : ("HAT-P000", 3, Catalog.HAT_P),
                'C' => ("HCG0000", 4, Catalog.HCG),
                'R' => ("HR0000", 4, Catalog.HR),
                'D' => ("HD000000", 6, Catalog.HD),
                'I' => ("HI000000", 6, Catalog.HIP),
                _ => ("H00", 2, Catalog.H)
            },
            'I' => noSpaces[1] is >= '0' and <= '9' || noSpaces[1] is 'C' or 'c'
                ? ("I0000", 4, Catalog.IC)
                : ("", 0, 0),
            'M' => noSpaces[1] == 'e' && noSpaces.Length > 2 && noSpaces[2] == 'l'
                ? ("Mel000", 3, Catalog.Melotte)
                : ("M000", 3, Catalog.Messier),
            'N' => ("N0000", 4, Catalog.NGC),
            'P' => noSpaces[1] == 'S' && noSpaces.Length > 2 && noSpaces[2] == 'R'
                ? ("", 8, Catalog.PSR)
                : ("", 0, 0),
            'S' => ("Sh2-000", 3, Catalog.Sharpless),
            'T' => ("TrES00", 2, Catalog.TrES),
            'U' => ("U00000", 5, Catalog.UGC),
            'W' => noSpaces[1] == 'D' && noSpaces.Length > 2 && noSpaces[2] == 'S'
                ? ("", 10, Catalog.WDS)
                : ("WASP000", 3, Catalog.WASP),
            'X' => ("XO000*", 4, Catalog.XO),
            _ => ("", 0, (Catalog)0)
        };

        if (catalog != Catalog.BonnerDurchmusterung)
        {
            trimmedInput = noSpaces;
        }

        return digits > 0 && catalog != 0;
    }
}
