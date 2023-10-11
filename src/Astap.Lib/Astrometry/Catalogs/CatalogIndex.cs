using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using static Astap.Lib.Astrometry.Catalogs.CatalogUtils;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry.Catalogs;

/// <summary>
/// Represents a unique entry in a catalogue s.th. NGC0001, M13 or IC0001
/// </summary>
[DebuggerDisplay("{CatalogIndexEx.ToCanonical(this),nq}")]
public enum CatalogIndex : ulong
{
    Sol = (ulong)'P' << 28 | 'l' << 21 | 'S' << 14 | 'o' << 7 | 'l',
    Mercury = 'P' << 21 | 'l' << 14 | 'M' << 7 | 'e',
    Venus = (ulong)'P' << 14 | 'l' << 7 | 'V',
    Earth = (ulong)'P' << 14 | 'l' << 7 | 'E',
    Moon = (ulong)'P' << 28 | 'l' << 21 | 'E' << 7 | 'I',
    EarthMoonBarycenter = (ulong)'P' << 28 | 'l' << 21 | 'E' << 14 | 'I' << 7 | 'B',
    Mars = 'P' << 21 | 'l' << 14 | 'M' << 7 | 'a',
    Jupiter = 'P' << 14 | 'l' << 7 | 'J',
    Saturn = 'P' << 14 | 'l' << 7 | 'S',
    Uranus = 'P' << 14 | 'l' << 7 | 'U',
    Neptune = 'P' << 14 | 'l' << 7 | 'N',

    // Used for testing
    Base91Enc = 1UL << 63,
    Barnard_22 = 'B' << 21 | '0' << 14 | '2' << 7 | '2',
    BD_16_1591s = Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'v' << 42 | (ulong)'(' << 35 | (ulong)'f' << 28 | 'T' << 21 | 'n' << 14 | 'L' << 7 | 'H',
    C009 = (ulong)'C' << 21 | '0' << 14 | '0' << 7 | '9',
    C041 = (ulong)'C' << 21 | '0' << 14 | '4' << 7 | '1',
    C069 = (ulong)'C' << 21 | '0' << 14 | '6' << 7 | '9',
    C092 = (ulong)'C' << 21 | '0' << 14 | '9' << 7 | '2',
    C099 = (ulong)'C' << 21 | '0' << 14 | '9' << 7 | '9',
    Cr024 = (ulong)'C' << 28 | 'r' << 21 | '0' << 14 | '2' << 7 | '4',
    Cr050 = (ulong)'C' << 28 | 'r' << 21 | '0' << 14 | '5' << 7 | '0',
    Cr360 = (ulong)'C' << 28 | 'r' << 21 | '3' << 14 | '6' << 7 | '0',
    Cr399 = (ulong)'C' << 28 | 'r' << 21 | '3' << 14 | '9' << 7 | '9',
    Ced0014 = (ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '4',
    Ced0016 = (ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '6',
    Ced0201 = (ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '0' << 21 | '2' << 14 | '0' << 7 | '1',
    Ced135a = (ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '1' << 21 | '3' << 14 | '5' << 7 | 'a',
    Ced135b = (ulong)'C' << 42 | (ulong)'e' << 35 | (ulong)'d' << 28 | '1' << 21 | '3' << 14 | '5' << 7 | 'b',
    CG0004 = (ulong)'C' << 35 | (ulong)'G' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '4',
    CG22B1 = (ulong)'C' << 35 | (ulong)'G' << 28 | '2' << 21 | '2' << 14 | 'B' << 7 | '1',
    DG0017 = (ulong)'D' << 35 | (ulong)'G' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '7',
    DG0018 = (ulong)'D' << 35 | (ulong)'G' << 28 | '0' << 21 | '0' << 14 | '1' << 7 | '8',
    DG0179 = (ulong)'D' << 35 | (ulong)'G' << 28 | '0' << 21 | '1' << 14 | '7' << 7 | '9',
    DOBASHI_0222 = (ulong)'D' << 42 | (ulong)'o' << 35 | (ulong)'0' << 28 | '0' << 21 | '2' << 14 | '2' << 7 | '2',
    GUM016 = (ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '1' << 7 | '6',
    GUM020 = (ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '2' << 7 | '0',
    GUM033 = (ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '3' << 7 | '3',
    GUM052 = (ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '5' << 7 | '2',
    GUM060 = (ulong)'G' << 35 | (ulong)'U' << 28 | 'M' << 21 | '0' << 14 | '6' << 7 | '0',
    HIP000935 = (ulong)'H' << 49 | (ulong)'I' << 42 | (ulong)'0' << 35 | (ulong)'0' << 28 | '0' << 21 | '9' << 14 | '3' << 7 | '5',
    HIP004427 = (ulong)'H' << 49 | (ulong)'I' << 42 | (ulong)'0' << 35 | (ulong)'0' << 28 | '4' << 21 | '4' << 14 | '2' << 7 | '7',
    HIP016537 = (ulong)'H' << 49 | (ulong)'I' << 42 | (ulong)'0' << 35 | (ulong)'1' << 28 | '6' << 21 | '5' << 14 | '3' << 7 | '7',
    HIP017499 = (ulong)'H' << 49 | (ulong)'I' << 42 | (ulong)'0' << 35 | (ulong)'1' << 28 | '7' << 21 | '4' << 14 | '9' << 7 | '9',
    HIP081100 = (ulong)'H' << 49 | (ulong)'I' << 42 | (ulong)'0' << 35 | (ulong)'8' << 28 | '1' << 21 | '1' << 14 | '0' << 7 | '0',
    HIP120404 = (ulong)'H' << 49 | (ulong)'I' << 42 | (ulong)'1' << 35 | (ulong)'2' << 28 | '0' << 21 | '4' << 14 | '0' << 7 | '4',
    HR0897 = (ulong)'H' << 35 | (ulong)'R' << 28 | '0' << 21 | '8' << 14 | '9' << 7 | '7',
    HR0264 = (ulong)'H' << 35 | (ulong)'R' << 28 | '0' << 21 | '2' << 14 | '6' << 7 | '4',
    HR1084 = (ulong)'H' << 35 | (ulong)'R' << 28 | '1' << 21 | '0' << 14 | '8' << 7 | '4',
    HR1142 = (ulong)'H' << 35 | (ulong)'R' << 28 | '1' << 21 | '1' << 14 | '4' << 7 | '2',
    IC0458 = (ulong)'I' << 28 | '0' << 21 | (ulong)'4' << 14 | (ulong)'5' << 7 | '8',
    IC0715NW = (ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'1' << 28 | '5' << 21 | '_' << 14 | 'N' << 7 | 'W',
    IC0720_NED02 = (ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'2' << 28 | '0' << 21 | 'N' << 14 | '0' << 7 | '2',
    IC0048 = (ulong)'I' << 28 | '0' << 21 | '0' << 14 | '4' << 7 | '8',
    IC0049 = (ulong)'I' << 28 | '0' << 21 | '0' << 14 | '4' << 7 | '9',
    IC1000 = (ulong)'I' << 28 | '1' << 21 | '0' << 14 | '0' << 7 | '0',
    IC1577 = (ulong)'I' << 28 | '1' << 21 | '5' << 14 | '7' << 7 | '7',
    IC4703 = (ulong)'I' << 28 | '4' << 21 | '7' << 14 | '0' << 7 | '3',
    LDN00146 = (ulong)'L' << 49 | (ulong)'D' << 42 | (ulong)'N' << 35 | (ulong)'0' << 28 | '0' << 21 | '1' << 14 | '4' << 7 | '6',
    M020 = 'M' << 21 | '0' << 14 | '2' << 7 | '0',
    M040 = 'M' << 21 | '0' << 14 | '4' << 7 | '0',
    M042 = 'M' << 21 | '0' << 14 | '4' << 7 | '2',
    M045 = 'M' << 21 | '0' << 14 | '4' << 7 | '5',
    M051 = 'M' << 21 | '0' << 14 | '5' << 7 | '1',
    M054 = 'M' << 21 | '0' << 14 | '5' << 7 | '4',
    M102 = 'M' << 21 | '1' << 14 | '0' << 7 | '2',
    Mel013 = (ulong)'M' << 35 | (ulong)'e' << 28 | 'l' << 21 | '0' << 14 | '1' << 7 | '3',
    Mel022 = (ulong)'M' << 35 | (ulong)'e' << 28 | 'l' << 21 | '0' << 14 | '2' << 7 | '2',
    Mel025 = (ulong)'M' << 35 | (ulong)'e' << 28 | 'l' << 21 | '0' << 14 | '2' << 7 | '5',
    NGC0056 = (ulong)'N' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '6',
    NGC0526_B = (ulong)'N' << 42 | (ulong)'0' << 35 | (ulong)'5' << 28 | '2' << 21 | '6' << 14 | '_' << 7 | 'B',
    NGC0869 = (ulong)'N' << 28 | '0' << 21 | '8' << 14 | '6' << 7 | '9',
    NGC1530_A = (ulong)'N' << 42 | (ulong)'1' << 35 | (ulong)'5' << 28 | '3' << 21 | '0' << 14 | '_' << 7 | 'A',
    NGC1333 = (ulong)'N' << 28 | '1' << 21 | '3' << 14 | '3' << 7 | '3',
    NGC1976 = (ulong)'N' << 28 | '1' << 21 | '9' << 14 | '7' << 7 | '6',
    NGC2070 = (ulong)'N' << 28 | '2' << 21 | '0' << 14 | '7' << 7 | '0',
    NGC3372 = (ulong)'N' << 28 | '3' << 21 | '3' << 14 | '7' << 7 | '2',
    NGC4038 = (ulong)'N' << 28 | '4' << 21 | '0' << 14 | '3' << 7 | '8',
    NGC4039 = (ulong)'N' << 28 | '4' << 21 | '0' << 14 | '3' << 7 | '9',
    NGC4913 = (ulong)'N' << 28 | '4' << 21 | '9' << 14 | '1' << 7 | '3',
    NGC5194 = (ulong)'N' << 28 | '5' << 21 | '1' << 14 | '9' << 7 | '4',
    NGC5457 = (ulong)'N' << 28 | '5' << 21 | '4' << 14 | '5' << 7 | '7',
    NGC6164 = (ulong)'N' << 28 | '6' << 21 | '1' << 14 | '6' << 7 | '4',
    NGC6165 = (ulong)'N' << 28 | '6' << 21 | '1' << 14 | '6' << 7 | '5',
    NGC6205 = (ulong)'N' << 28 | '6' << 21 | '2' << 14 | '0' << 7 | '5',
    NGC6302 = (ulong)'N' << 28 | '6' << 21 | '3' << 14 | '0' << 7 | '2',
    NGC6514 = (ulong)'N' << 28 | '6' << 21 | '5' << 14 | '1' << 7 | '4',
    NGC6611 = (ulong)'N' << 28 | '6' << 21 | '6' << 14 | '1' << 7 | '1',
    NGC6715 = (ulong)'N' << 28 | '6' << 21 | '7' << 14 | '1' << 7 | '5',
    NGC7293 = (ulong)'N' << 28 | '7' << 21 | '2' << 14 | '9' << 7 | '3',
    NGC7331 = (ulong)'N' << 28 | '7' << 21 | '3' << 14 | '3' << 7 | '1',
    ESO056_115 = (ulong)'E' << 49 | (ulong)'0' << 42 | (ulong)'5' << 35 | (ulong)'6' << 28 | '-' << 21 | '1' << 14 | '1' << 7 | '5',
    PSR_J2144_3933s = Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'Q' << 42 | (ulong)'A' << 35 | (ulong)'d' << 28 | 'y' << 21 | 'w' << 14 | 'X' << 7 | 'D',
    PSR_B0633_17n = Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'F' << 42 | (ulong)'t' << 35 | (ulong)'I' << 28 | 't' << 21 | 'j' << 14 | 't' << 7 | 'C',
    PSR_J0002_6216n = Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'X' << 42 | (ulong)'L' << 35 | (ulong)'@' << 28 | 'Q' << 21 | '3' << 14 | 'u' << 7 | 'C',
    RCW_0036 = (ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '0' << 14 | '3' << 7 | '6',
    RCW_0053 = (ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '3',
    RCW_0107 = (ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '1' << 14 | '0' << 7 | '7',
    RCW_0124 = (ulong)'R' << 42 | (ulong)'C' << 35 | (ulong)'W' << 28 | '0' << 21 | '1' << 14 | '2' << 7 | '4',
    Sh2_0006 = (ulong)'S' << 49 | (ulong)'h' << 42 | (ulong)'2' << 35 | (ulong)'-' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '6',
    Sh2_0155 = (ulong)'S' << 49 | (ulong)'h' << 42 | (ulong)'2' << 35 | (ulong)'-' << 28 | '0' << 21 | '1' << 14 | '5' << 7 | '5',
    TrES03 = (ulong)'T' << 35 | (ulong)'r' << 28 | 'E' << 21 | 'S' << 14 | '0' << 7 | '3',
    TwoM_J11400198_3152397n = Base91Enc | (ulong)'P' << 56 | (ulong)'6' << 49 | (ulong)'3' << 42 | (ulong)'A' << 35 | (ulong)'T' << 28 | 'J' << 21| ',' << 14 | 'y' << 7 | 'B',
    TwoM_J12015301_1852034s = Base91Enc | (ulong)']' << 56 | (ulong)'#' << 49 | (ulong)'f' << 42 | (ulong)'R' << 35 | (ulong)'t' << 28 | 'u' << 21 | 'K' << 14 | 'O' << 7 | 'L',
    TwoMX_J00185316_1035410n = Base91Enc | (ulong)'r' << 56 | (ulong)'1' << 49 | (ulong)'5' << 42 | (ulong)'|' << 35 | (ulong)'s' << 28 | '1' << 21 | 'V' << 14 | 'w' << 7 | 'H',
    TwoMX_J11380904_0936257s = Base91Enc | (ulong)'l' << 56 | (ulong)'Y' << 49 | (ulong)'<' << 42 | (ulong)'7' << 35 | (ulong)'i' << 28 | 'z' << 21 | 'o' << 14| 'u' << 7 | 'P',
    vdB0005 = (ulong)'v' << 42 | (ulong)'d' << 35 | (ulong)'B' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '5',
    vdB0020 = (ulong)'v' << 42 | (ulong)'d' << 35 | (ulong)'B' << 28 | '0' << 21 | '0' << 14 | '2' << 7 | '0',
    WDS_02583_4018s = Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'g' << 42 | (ulong)'4' << 35 | (ulong)'}' << 28 | '-' << 21 | '8' << 14 | '&' << 7 | 'G',
    WDS_23599_3112s = Base91Enc | (ulong)'A' << 56 | (ulong)'A' << 49 | (ulong)'+' << 42 | (ulong)'i' << 35 | (ulong)')' << 28 | ',' << 21 | 'N' << 14 | '%' << 7 | 'G',
    XO0003 = (ulong)'X' << 35 | (ulong)'O' << 28 | '0' << 21 | '0' << 14 | '0' << 7 | '3',
    XO002N = (ulong)'X' << 35 | (ulong)'O' << 28 | '0' << 21 | '0' << 14 | '2' << 7 | 'N',
}

public static class CatalogIndexEx
{
    public static string ToCanonical(this CatalogIndex catalogIndex, CanonicalFormat format = CanonicalFormat.Normal)
    {
        var (catalog, decoded, isMSBSet) = catalogIndex.ToCatalogAndValue();

        var prefix = catalog.ToCanonical(format);
        if (isMSBSet)
        {
            return catalog switch
            {
                Catalog.PSR =>
                    RADecEncodedIndexToCanonical(prefix, decoded, 4, 4, PSRRaMask, PSRRaShift, PSRDecMask, PSREpochSupport),
                Catalog.TwoMass or Catalog.TwoMassX =>
                    RADecEncodedIndexToCanonical(prefix, decoded, 8, 7, TwoMassRaMask, TwoMassRaShift, TwoMassDecMask, TwoMassEncOptions),
                Catalog.WDS =>
                    RADecEncodedIndexToCanonical(prefix, decoded, 5, 4, WDSRaMask, WDSRaShift, WDSDecMask, WDSEncOptions),
                Catalog.BonnerDurchmusterung =>
                    BDEncodedIndexToCanonical(prefix, decoded, 0, 2, BDRaMask, BDRaShift, BDDecMask, BDEncOptions),
                _ => throw new ArgumentException($"Catalog index {catalogIndex} with MSB = true could not be parsed", nameof(catalogIndex))
            };
        }
        else
        {
            var withoutPrefixAsStr = EnumValueToAbbreviation(decoded).AsSpan().TrimStart('-').TrimStart('0');

            if (withoutPrefixAsStr.Length is 0)
            {
                throw new ArgumentException($"Catalog index {catalogIndex} could not be parsed", nameof(catalogIndex));
            }

            int Nor_Idx;
            if (catalog is Catalog.NGC or Catalog.IC && (Nor_Idx = withoutPrefixAsStr.IndexOfAny('_', 'N')) > 0)
            {
                var sb = new StringBuilder(MaxLenInASCII)
                    .Append(prefix)
                    .Append(' ')
                    .Append(withoutPrefixAsStr[..Nor_Idx])
                    .Append(withoutPrefixAsStr[Nor_Idx] == 'N' ? " NED" : "")
                    .Append(withoutPrefixAsStr[(Nor_Idx + 1)..]);
                return sb.ToString();
            }
            else
            {
                var sep = catalog switch
                {
                    Catalog.Barnard when format is CanonicalFormat.Alternative => "",
                    Catalog.Sharpless or Catalog.TrES or Catalog.WASP or Catalog.XO when format is CanonicalFormat.Normal => "-",
                    Catalog.Messier or Catalog.Caldwell when format is CanonicalFormat.Normal => "",
                    Catalog.TrES or Catalog.WASP or Catalog.XO when format is CanonicalFormat.Alternative => "-",
                    _ => " "
                };

                return string.Concat(prefix, sep, withoutPrefixAsStr);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static string BDEncodedIndexToCanonical(string prefix, ulong decoded, int raDigits, int decDigits, uint raMask, int raShift, uint decMask, Base91EncRADecOptions encOptions)
    {
        DecodeBase91(decoded, decDigits, raMask, raShift, decMask, encOptions, out var decIsNeg, out _, out var actualDecDigits, out var dec, out var ra);

        return string.Concat(
            prefix,
            decIsNeg ? "-" : "+",
            dec.ToString("D" + actualDecDigits, CultureInfo.InvariantCulture),
            " ",
            ra.ToString("D" + raDigits, CultureInfo.InvariantCulture)
        );
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static string RADecEncodedIndexToCanonical(string prefix, ulong decoded, int raDigits, int decDigits, uint raMask, int raShift, uint decMask, Base91EncRADecOptions encOptions)
    {
        DecodeBase91(decoded, decDigits, raMask, raShift, decMask, encOptions, out var decIsNeg, out var epoch, out var actualDecDigits, out var dec, out var ra);

        // 2 digits for B is only valid for PSR
        return string.Concat(
            prefix, " ", epoch,
            ra.ToString("D" + raDigits, CultureInfo.InvariantCulture),
            decIsNeg ? "-" : "+",
            dec.ToString("D" + actualDecDigits, CultureInfo.InvariantCulture)
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void DecodeBase91(ulong decoded, int decDigits, uint raMask, int raShift, uint decMask, Base91EncRADecOptions encOptions, out bool decIsNeg, out string epoch, out int actualDecDigits, out ulong dec, out ulong ra)
    {
        // sign
        decIsNeg = (decoded & 1) == 0b1;
        decoded >>= 1;

        if (encOptions.HasFlag(Base91EncRADecOptions.ImpliedJ2000))
        {
            epoch = "J";
            actualDecDigits = decDigits;
        }
        else
        {
            var isJ = (decoded & 1) == 0b1;
            epoch = isJ ? "J" : "B";
            actualDecDigits = isJ ? decDigits : 2;
            decoded >>= 1;
        }

        // dec
        dec = decoded & decMask;
        decoded >>= raShift;

        // ra
        ra = decoded & raMask;
    }

    public static string ToAbbreviation(this CatalogIndex catalogIndex) => EnumValueToAbbreviation((ulong)catalogIndex);

    private static readonly Dictionary<byte, Catalog[]> PartitionedCategories = Enum.GetValues<Catalog>().PartitionCatalogsByMSB();

    static Dictionary<byte, Catalog[]> PartitionCatalogsByMSB(this Catalog[] entries)
    {
        var dict = new Dictionary<byte, Catalog[]>(26);

        foreach (var entry in entries.OrderByDescending(x => (ulong)x))
        {
            var catAsUlong = (ulong)entry;
            var catLZC = BitOperations.LeadingZeroCount(catAsUlong);
            var key = CatKey(catAsUlong, catLZC);
            dict.AddLookupEntry(key, entry);
        }

        return dict;
    }

    private static byte CatKey(ulong catAsUlong, int lzc) => (byte)(catAsUlong >> BitsInUlong - lzc - ASCIIBits & ASCIIMask);


    public static Catalog ToCatalog(this CatalogIndex catalogIndex)
    {
        var (catalog, _, _) = catalogIndex.ToCatalogAndValue();
        return catalog;
    }

    /// <summary>
    /// Identifies the <see cref="Catalog"/> of the given <see cref="CatalogIndex"/> <paramref name="catalogIndex"/>.
    /// Will return 0 for Catalog if Catalog cannot be determined.
    /// Additionally the embedded value specific to the catalog is returned together with indication wether the MSB is set.
    /// This can be used to extract RA and DEC or catalog index number.
    /// </summary>
    /// <param name="catalogIndex">Catalog index to decode</param>
    /// <returns>(catalog entry, decoded integral without catalog prefix/suffix, true if MSB is set)</returns>
    /// <exception cref="ArgumentException">Will throw if catalog cannot be found in the known list of catalogs</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (Catalog Catalog, ulong Value, bool MSBSet) ToCatalogAndValue(this CatalogIndex catalogIndex)
    {
        if (catalogIndex == 0)
        {
            return (0, 0UL, false);
        }

        var catalogIndexUl = (ulong)catalogIndex;
        var isMSBSet = (catalogIndexUl & MSBUlongMask) == MSBUlongMask;
        catalogIndexUl ^= isMSBSet ? MSBUlongMask : 0ul;

        if (isMSBSet)
        {
            var encoded = EnumValueToAbbreviation(catalogIndexUl);
            if (Base91Encoder.DecodeBytes(encoded) is { Length: > 1} decoded)
            {
                const int max = BytesInUlong;
                Span<byte> bytesUlN = stackalloc byte[max];
                decoded.CopyTo(bytesUlN[(max - decoded.Length)..]);

                var decodedULH = BinaryPrimitives.ReadUInt64BigEndian(bytesUlN);

                return ((Catalog)(decoded[^1] & ASCIIMask), decodedULH >> ASCIIBits, true);
            }
            else
            {
                return (0, 0UL, false);
            }
        }

        var catalogIndexLZC = BitOperations.LeadingZeroCount(catalogIndexUl);

        if (!PartitionedCategories.TryGetValue(CatKey(catalogIndexUl, catalogIndexLZC), out var categories))
        {
            return (0, 0UL, false);
        }

        for (var i = 0; i < categories.Length; i++)
        {
            var entry = categories[i];
            var entryLZC = BitOperations.LeadingZeroCount((ulong)entry);
            var shift = entryLZC - catalogIndexLZC;
            var catalogIndexCat = (Catalog)(catalogIndexUl >> shift);
            if (entry == catalogIndexCat)
            {
                var mask = ~(ulong.MaxValue << shift);
                var value = catalogIndexUl & mask;
                return (entry, value, false);
            }
        }

        throw new ArgumentException($"Cannot find Catalog type of {catalogIndex}", nameof(catalogIndex));
    }
}
