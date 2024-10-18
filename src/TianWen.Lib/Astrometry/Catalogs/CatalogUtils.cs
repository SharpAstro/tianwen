using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static TianWen.Lib.EnumHelper;

namespace TianWen.Lib.Astrometry.Catalogs;

[Flags]
internal enum Base91EncRADecOptions
{
    None = 0,
    ImpliedJ2000 = 1
}

public static partial class CatalogUtils
{
    private static readonly System.Buffers.SearchValues<char> searchValueDigits = System.Buffers.SearchValues.Create("0123456789");

    [Flags]
    private enum Base91AlgoOptions
    {
        None = 0,
        ImpliedJ2000 = 1,
        DecIsNegative = 1 << 1,
        IsJ2000 = 1 << 2,
    }

    private const RegexOptions CommonOpts = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace;

    [GeneratedRegex("(?:BD) \\s* ([-+]) ([0-9]{1,2}) (?:\\s+|[-_]) ([0-9]{1,5})", CommonOpts)]
    private static partial Regex BDPatternGen();
    private static readonly Regex BDPattern = BDPatternGen();

    [GeneratedRegex(@"(?:CG|HH) ([0-9][A-Z0-9]*)", CommonOpts)]
    private static partial Regex CGorHHPatternGen();
    private static readonly Regex CGorHHPattern = CGorHHPatternGen();

    [GeneratedRegex(@"^(N|I|NGC|IC) ([0-9]{1,4}) (?:(N(?:ED)? ([0-9]{1,2})) | [_]?([A-Z]{1,2}))$", CommonOpts)]
    private static partial Regex ExtendedCatalogEntryPatternGen();
    private static readonly Regex ExtendedCatalogEntryPattern = ExtendedCatalogEntryPatternGen();

    [GeneratedRegex(@"^(?:PSR) ([BJ]) ([0-9]{4}) ([-+]) ([0-9]{2,4})$", CommonOpts)]
    private static partial Regex PSRDesignationPatternGen();
    private static readonly Regex PSRDesignationPattern = PSRDesignationPatternGen();

    [GeneratedRegex(@"^(?:2MAS[SX]) ([J]) ([0-9]{8}) ([-+]) ([0-9]{7})$", CommonOpts)]
    private static partial Regex TwoMassAnd2MassXPatternGen();
    private static readonly Regex TwoMassAnd2MassXPattern = TwoMassAnd2MassXPatternGen();

    [GeneratedRegex(@"^(?:WDS) ([J]) ([0-9]{5}) ([-+]) ([0-9]{4})$", CommonOpts)]
    private static partial Regex WDSPatternGen();
    private static readonly Regex WDSPattern = WDSPatternGen();

    [GeneratedRegex(@"(?:TYC) ([0-9]{1,4})-([0-9]{1,5})-([1-3])", CommonOpts)]
    private static partial Regex Tyc2PatternGen();
    private static readonly Regex Tyc2Pattern = Tyc2PatternGen();

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

    private const ushort TYC1_MASK = 0xff_ff;

    private const uint TYC2_MASK = 0xff_ff;
    private const int TYC2_SHIFT = 16;

    private const uint TYC3_MASK = 0b11;
    private const int TYC3_SHIFT = 2;
    static readonly char[] NGCExt = ['A', 'B', 'C', 'D', 'E', 'F', 'S', 'W', 'N'];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
        var firstDigit = trimmedInput.AsSpan().IndexOfAny(searchValueDigits);
        Span<char> chars = stackalloc char[template.Length];

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

            case Catalog.CG:
            case Catalog.HH:
                {
                    var cgOrHHMatch = CGorHHPattern.Match(trimmedInput);
                    if (cgOrHHMatch.Success && cgOrHHMatch.Groups.Count == 2 && cgOrHHMatch.Groups[1].Length <= digits)
                    {
                        template.CopyTo(chars);
                        cgOrHHMatch.Groups[1].ValueSpan.CopyTo(chars[^cgOrHHMatch.Groups[1].ValueSpan.Length..]);
                        for (int i = 0; i < chars.Length; i++)
                        {
                            if (chars[i] == '*')
                            {
                                chars[i] = '0';
                            }
                        }
                        cleanedUp = new string(chars);
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

            case Catalog.Tycho2:
                cleanedUp = CleanupTyc2CatalogIndex(Tyc2Pattern, trimmedInput, catalog);
                isBase91Encoded = true;
                break;

            default:
                template.CopyTo(chars);
                int foundDigits = 0;
                var maybeWildcardsLeft = false;
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
                            maybeWildcardsLeft = true;
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

                if (foundDigits > 0)
                {
                    cleanedUp = new string(chars);
                    if (maybeWildcardsLeft)
                    {
                        cleanedUp = cleanedUp.Replace("*", "");
                    }
                }
                else
                {
                    cleanedUp = null;
                }
                isBase91Encoded = false;
                break;
        }

        if (cleanedUp is { Length: <= MaxLenInASCII })
        {
            catalogIndex = AbbreviationToCatalogIndex(cleanedUp, isBase91Encoded);
            return true;
        }
        else
        {
            catalogIndex = 0;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static CatalogIndex AbbreviationToCatalogIndex(string cleanedUp, bool isBase91Encoded) => (isBase91Encoded ? CatalogIndex.Base91Enc : 0L) | AbbreviationToEnumMember<CatalogIndex>(cleanedUp);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static string? CleanupRADecBasedCatalogIndex(Regex pattern, string trimmedInput, Catalog catalog, ulong raMask, int raShift, ulong decMask, Base91EncRADecOptions base91EncOptions)
    {
        var match = pattern.Match(trimmedInput);

        if (!match.Success || match.Groups.Count < 5)
        {
            return null;
        }

        var algoOptions = base91EncOptions.ToAlgoOptions();
        algoOptions |= match.Groups[1].ValueSpan[0] == 'J' ? Base91AlgoOptions.IsJ2000 : Base91AlgoOptions.None;
        var ra = match.Groups[2].ValueSpan;
        algoOptions |= match.Groups[3].ValueSpan[0] == '-' ? Base91AlgoOptions.DecIsNegative : Base91AlgoOptions.None;
        var dec = match.Groups[4].ValueSpan;

        return EncodeRADecBasedCatalogIndex(catalog, raMask, raShift, decMask, ra, dec, algoOptions);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static string? CleanupBDCatalogIndex(Regex pattern, string trimmedInput, Catalog catalog, ulong raMask, int raShift, ulong decMask, Base91EncRADecOptions base91EncOptions)
    {
        var match = pattern.Match(trimmedInput);

        if (!match.Success)
        {
            return null;
        }

        var algoOptions = base91EncOptions.ToAlgoOptions();
        var ra = match.Groups[3].ValueSpan;
        algoOptions |= match.Groups[1].Value[0] == '-' ? Base91AlgoOptions.DecIsNegative : Base91AlgoOptions.None;
        var dec = match.Groups[2].ValueSpan;

        return EncodeRADecBasedCatalogIndex(catalog, raMask, raShift, decMask, ra, dec, algoOptions);
    }

    private static string? CleanupTyc2CatalogIndex(Regex pattern, string trimmedInput, Catalog catalog)
    {
        var match = pattern.Match(trimmedInput);

        if (!match.Success)
        {
            return null;
        }

        return EncodeTyc2CatalogIndex(catalog, match.Groups[1].ValueSpan, match.Groups[2].ValueSpan, match.Groups[3].ValueSpan);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static string? EncodeRADecBasedCatalogIndex(Catalog catalog, ulong raMask, int raShift, ulong decMask, in ReadOnlySpan<char> ra, in ReadOnlySpan<char> dec, Base91AlgoOptions options)
    {
        if (ulong.TryParse(ra, NumberStyles.None, CultureInfo.InvariantCulture, out var raVal)
            && ulong.TryParse(dec, NumberStyles.None, CultureInfo.InvariantCulture, out var decVal))
        {
            var isJ2000 = options.HasFlag(Base91AlgoOptions.IsJ2000);
            var j2000implied = options.HasFlag(Base91AlgoOptions.ImpliedJ2000);
            var decIsNeg = options.HasFlag(Base91AlgoOptions.DecIsNegative);

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
            return Base91.EncodeBytes(bytesN[1..].ToArray());
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static string? EncodeTyc2CatalogIndex(Catalog catalog, in ReadOnlySpan<char> tyc1Input, in ReadOnlySpan<char> tyc2Input, in ReadOnlySpan<char> tyc3Input)
    {
        if (ushort.TryParse(tyc1Input, NumberStyles.None, CultureInfo.InvariantCulture, out var tyc1)
            && ushort.TryParse(tyc2Input, NumberStyles.None, CultureInfo.InvariantCulture, out var tyc2)
            && byte.TryParse(tyc3Input, NumberStyles.None, CultureInfo.InvariantCulture, out var tyc3))
        {
            return EncodeTyc2CatalogIndex(catalog, tyc1, tyc2, tyc3);
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static string EncodeTyc2CatalogIndex(Catalog catalog, ushort tyc1, ushort tyc2, byte tyc3)
    {
        var idAsLongH = (ulong)(tyc1 & TYC1_MASK);
        idAsLongH <<= TYC2_SHIFT;
        idAsLongH |= tyc2 & TYC2_MASK;
        idAsLongH <<= TYC3_SHIFT;
        idAsLongH |= tyc3 & TYC3_MASK;
        idAsLongH <<= ASCIIBits;
        idAsLongH |= (ulong)catalog & ASCIIMask;

        Span<byte> bytesN = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytesN, idAsLongH);

        // TODO update lib to accept spans
        return Base91.EncodeBytes(bytesN[1..].ToArray());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (ushort Tyc1, uint Tyc2, byte Tyc3) DecodeTyc2CatalogIndex(ulong decoded)
    {
        byte tyc3 = (byte)(decoded & TYC3_MASK);
        decoded >>= TYC3_SHIFT;

        var tyc2 = (uint)(decoded & TYC2_MASK);
        decoded >>= TYC2_SHIFT;

        var tyc1 = (ushort)(decoded & TYC1_MASK);

        return (tyc1, tyc2, tyc3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static Base91AlgoOptions ToAlgoOptions(this Base91EncRADecOptions encodingOptions)
        => encodingOptions.HasFlag(Base91EncRADecOptions.ImpliedJ2000) ? Base91AlgoOptions.ImpliedJ2000 : Base91AlgoOptions.None;

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

        (template, catalog) = noSpaces[0] switch
        {
            '2' when noSpaces.Length > 5 && noSpaces[4] == 'S' => ("", Catalog.TwoMass),
            '2' when noSpaces.Length > 5 && noSpaces[4] == 'X' => ("", Catalog.TwoMassX),
            'A' when secondChar == 'C' => ("ACO0000", Catalog.Abell),
            'B' when secondChar == 'D' => ("BD+00 0000", Catalog.BonnerDurchmusterung),
            'B' when secondIsDigit || secondChar is 'A' or 'a' => ("B000", Catalog.Barnard),
            'C' when secondChar is 'l' or 'o' or 'r' => ("Cr000", Catalog.Collinder),
            'C' when secondChar == 'e' => ("Ced000*", Catalog.Ced),
            'C' when secondChar == 'G' => ("CG00**", Catalog.CG),
            'C' when secondIsDigit || secondChar is 'a' => ("C000", Catalog.Caldwell),
            'D' when secondChar is 'o' or 'O' => ("Do00000", Catalog.Dobashi),
            'D' when secondChar == 'G' => ("DG000*", Catalog.DG),
            'E' => ("E000-000", Catalog.ESO),
            'G' when secondChar is 'U' or 'u' => ("GUM00*", Catalog.GUM),
            'G' when secondChar == 'J' => ("GJ0000", Catalog.GJ),
            'H' => secondChar switch
            {
                'A' when noSpaces.Length > 4 && noSpaces[3] == 'S' => ("HATS000", Catalog.HATS),
                'A' when noSpaces.Length > 5 && noSpaces[4] == 'P' => ("HAT-P000", Catalog.HAT_P),
                'C' => ("HCG0000", Catalog.HCG),
                'R' => ("HR0000", Catalog.HR),
                'D' => ("HD000000", Catalog.HD),
                'H' => ("HH000**", Catalog.HH),
                'I' => ("HI000000", Catalog.HIP),
                _ when secondIsDigit => ("H00", Catalog.H),
                _ => ("", 0)
            },
            'I' when secondIsDigit || secondChar is 'C' or 'c' => ("I0000", Catalog.IC),
            'L' when secondChar == 'D' => ("LDN0000*", Catalog.LDN),
            'M' => secondChar switch
            {
                'e' when noSpaces.Length > 2 && noSpaces[2] == 'l' => ("Mel000", Catalog.Melotte),
                'e' when noSpaces.Length > 6 && noSpaces[0..7] == "Messier" => ("M00*", Catalog.Messier),
                'W' => ("MWSC0000", Catalog.MWSC),
                _ when secondIsDigit => ("M00*", Catalog.Messier),
                _ => ("", 0)
            },
            // be more lenient with NGC as its typed a lot
            'N' when secondIsDigit || secondChar is 'G' or 'g' or 'C' or 'c' => ("N0000", Catalog.NGC),
            // make - required as to due to large count of wildcards
            'P' when secondChar == 'l' && noSpaces.Length > 2 && noSpaces[2] == '-' => ("Pl-*****", Catalog.Pl),
            'P' when secondChar == 'G' && noSpaces.Length > 2 && noSpaces[2] == 'C' => ("PGC000000", Catalog.PGC),
            'P' when secondChar == 'S' && noSpaces.Length > 2 && noSpaces[2] == 'R' => ("", Catalog.PSR),
            'R' when secondChar == 'C' => ("RCW000*", Catalog.RCW),
            'S' when secondChar is 'H' or 'h' && noSpaces.Length > 2 && noSpaces[2] is '2' or 'a' => ("Sh2-000*", Catalog.Sharpless),
            'T' when secondIsDigit || secondChar == 'r' => ("TrES00", Catalog.TrES),
            'T' when secondChar is 'Y' or 'Y' => ("TYC 0000-00000-0", Catalog.Tycho2),
            'U' when secondIsDigit || secondChar == 'G' => ("U00000", Catalog.UGC),
            'v' or 'V' when secondChar is 'd' or 'D' => ("vdB000*", Catalog.vdB),
            'W' when secondChar == 'D' && noSpaces.Length > 2 && noSpaces[2] == 'S' => ("", Catalog.WDS),
            'W' when secondIsDigit || secondChar == 'A' => ("WASP000", Catalog.WASP),
            'X' when secondIsDigit || secondChar == 'O' => ("XO000*", Catalog.XO),
            _ => ("", (Catalog)0)
        };
        digits = catalog.GetNumericalIndexSize();

        if (catalog != Catalog.BonnerDurchmusterung)
        {
            trimmedInput = noSpaces;
        }

        return digits > 0 && catalog != 0;
    }
}
