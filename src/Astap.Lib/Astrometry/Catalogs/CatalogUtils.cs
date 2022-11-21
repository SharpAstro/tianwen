using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry.Catalogs;

public static class CatalogUtils
{
    const RegexOptions CommonOpts = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace;

    static readonly Regex BDPattern = new(@"(?:BD) \s* ([-+]) ([0-9]{1,2}) (?:\s+|[-_]) ([0-9]{1,5})", CommonOpts);

    static readonly Regex ExtendedCatalogEntryPattern = new(@"^(N|I|NGC|IC) ([0-9]{1,4}) (?:(N(?:ED)? ([0-9]{1,2})) | [_]?([A-Z]{1,2}))$", CommonOpts);

    static readonly Regex PSRDesignationPattern = new(@"^(?:PSR) ([BJ]) ([0-9]{4}) ([-+]) ([0-9]{2,4})$", CommonOpts);

    static readonly Regex TwoMassAnd2MassXPattern = new(@"^(?:2MAS[SX]) ([J]) ([0-9]{8}) ([-+]) ([0-9]{7})$", CommonOpts);

    static readonly Regex WDSPattern = new(@"^(?:WDS) ([J]) ([0-9]{5}) ([-+]) ([0-9]{4})$", CommonOpts);

    internal const uint PSRRaMask = 0xf_ff;
    internal const int PSRRaShift = 14;
    internal const uint PSRDecMask = 0x3f_ff;
    internal const Base91EncRADecOptions PSREpochSupport = Base91EncRADecOptions.None;

    internal const uint TwoMassRaMask = 0x1_ff_ff_ff;
    internal const int TwoMassRaShift = 24;
    internal const uint TwoMassDecMask = 0xff_ff_ff;
    internal const Base91EncRADecOptions TwoMassEncOptions = Base91EncRADecOptions.ImpliedJ2000;

    internal const uint WDSRaMask = 0x7f_ff;
    internal const int WDSRaShift = 14;
    internal const uint WDSDecMask = 0x3f_ff;
    internal const Base91EncRADecOptions WDSEncOptions = Base91EncRADecOptions.ImpliedJ2000;

    internal const uint BDRaMask = 0x1_ff_ff;
    internal const int BDRaShift = 8;
    internal const uint BDDecMask = 0xff;
    internal const Base91EncRADecOptions BDEncOptions = Base91EncRADecOptions.ImpliedJ2000;

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
                cleanedUp = CleanupBDCatalogIndex(BDPattern, trimmedInput, catalog, BDRaMask, BDRaShift, BDDecMask, BDEncOptions);
                isBase91Encoded = true;
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
                    var isDigit = char.IsDigit(inputChar);
                    if (tmplChar == '*')
                    {
                        // treat * as a wildcard either meaning 0-9 or A-Z or a-z
                        if (!isDigit && !char.IsLetter(inputChar))
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static string? CleanupRADecBasedCatalogIndex(Regex pattern, string trimmedInput, Catalog catalog, ulong raMask, int raShift, ulong decMask, Base91EncRADecOptions epochSupport)
    {
        var match = pattern.Match(trimmedInput);

        if (!match.Success || match.Groups.Count < 5)
        {
            return null;
        }

        var isJ2000 = match.Groups[1].ValueSpan[0] == 'J';
        var ra = match.Groups[2].ValueSpan;
        var decIsNeg = match.Groups[3].ValueSpan[0] == '-';
        var dec = match.Groups[4].ValueSpan;

        return EncodeRADecBasedCatalogIndex(catalog, raMask, raShift, decMask, isJ2000, ra, decIsNeg, dec, epochSupport);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static string? CleanupBDCatalogIndex(Regex pattern, string trimmedInput, Catalog catalog, ulong raMask, int raShift, ulong decMask, Base91EncRADecOptions epochSupport)
    {
        var match = pattern.Match(trimmedInput);

        if (!match.Success)
        {
            return null;
        }

        var ra = match.Groups[3].ValueSpan;
        var decIsNeg = match.Groups[1].Value[0] == '-';
        var dec = match.Groups[2].ValueSpan;

        return EncodeRADecBasedCatalogIndex(catalog, raMask, raShift, decMask, false, ra, decIsNeg, dec, epochSupport);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static string? EncodeRADecBasedCatalogIndex(Catalog catalog, ulong raMask, int raShift, ulong decMask, bool isJ2000, in ReadOnlySpan<char> ra, bool decIsNeg, in ReadOnlySpan<char> dec, Base91EncRADecOptions epochSupport)
    {
        if (ulong.TryParse(ra, NumberStyles.None, CultureInfo.InvariantCulture, out var raVal)
            && ulong.TryParse(dec, NumberStyles.None, CultureInfo.InvariantCulture, out var decVal))
        {
            var j2000implied = epochSupport.HasFlag(Base91EncRADecOptions.ImpliedJ2000);

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
        var secondChar = noSpaces[1];
        var secondIsDigit = char.IsDigit(secondChar);

        (template, digits, catalog) = noSpaces[0] switch
        {
            '2' when noSpaces.Length > 5 && noSpaces[4] == 'S' => ("", 15, Catalog.TwoMass),
            '2' when noSpaces.Length > 5 && noSpaces[4] == 'X' => ("", 15, Catalog.TwoMassX),
            'A' when secondChar == 'C' => ("ACO0000", 4, Catalog.Abell),
            'B' when secondChar == 'D' => ("BD+00 0000", 6, Catalog.BonnerDurchmusterung),
            'B' when secondIsDigit || secondChar is 'A' or 'a' => ("B000", 3, Catalog.Barnard),
            'C' when secondChar == 'l' || secondChar == 'r' => ("Cr000", 3, Catalog.Collinder),
            'C' when secondIsDigit => ("C000", 3, Catalog.Caldwell),
            'E' => ("E000-000", 7, Catalog.ESO),
            'G' when secondChar is 'U' or 'u' => ("GUM00*", 3, Catalog.GUM),
            'G' when secondChar == 'J' => ("GJ0000", 4, Catalog.GJ),
            'H' => secondChar switch
            {
                'A' when noSpaces.Length > 4 && noSpaces[3] == 'S' => ("HATS000", 3, Catalog.HATS),
                'A' when noSpaces.Length > 5 && noSpaces[4] == 'P' => ("HAT-P000", 3, Catalog.HAT_P),
                'C' => ("HCG0000", 4, Catalog.HCG),
                'R' => ("HR0000", 4, Catalog.HR),
                'D' => ("HD000000", 6, Catalog.HD),
                'I' => ("HI000000", 6, Catalog.HIP),
                _ when secondIsDigit => ("H00", 2, Catalog.H),
                _ => ("", 0, 0)
            },
            'I' when secondIsDigit || secondChar is 'C' or 'c' => ("I0000", 4, Catalog.IC),
            'L' when secondChar == 'D' => ("LDN0000*", 5, Catalog.LDN),
            'M' => secondChar switch
            {
                'e' when noSpaces.Length > 2 && noSpaces[2] == 'l' => ("Mel000", 3, Catalog.Melotte),
                'e' when noSpaces.Length > 6 && noSpaces[0..7] == "Messier" => ("M00*", 3, Catalog.Messier),
                _ when secondIsDigit => ("M00*", 3, Catalog.Messier),
                _ => ("", 0, 0)
            },
            // be more lenient with NGC as its typed a lot
            'N' when secondIsDigit || secondChar is 'G' or 'g' or 'C' or 'c' => ("N0000", 4, Catalog.NGC),
            'P' when secondChar == 'S' && noSpaces.Length > 2 && noSpaces[2] == 'R' => ("", 8, Catalog.PSR),
            'R' when secondChar == 'C' => ("RCW000*", 4, Catalog.RCW),
            'S' when secondIsDigit || secondChar == 'h' => ("Sh2-000", 3, Catalog.Sharpless),
            'T' when secondIsDigit || secondChar == 'r' => ("TrES00", 2, Catalog.TrES),
            'U' when secondIsDigit || secondChar == 'G' => ("U00000", 5, Catalog.UGC),
            'W' when secondChar == 'D' && noSpaces.Length > 2 && noSpaces[2] == 'S' => ("", 10, Catalog.WDS),
            'W' when secondIsDigit || secondChar == 'A' => ("WASP000", 3, Catalog.WASP),
            'X' when secondIsDigit || secondChar == 'O' => ("XO000*", 4, Catalog.XO),
            _ => ("", 0, (Catalog)0)
        };

        if (catalog != Catalog.BonnerDurchmusterung)
        {
            trimmedInput = noSpaces;
        }

        return digits > 0 && catalog != 0;
    }
}
