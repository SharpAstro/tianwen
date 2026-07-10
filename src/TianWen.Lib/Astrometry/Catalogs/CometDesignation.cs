using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TianWen.Lib.Astrometry.Comets;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Orbit-type letter of an IAU comet designation (the letter before the slash in <c>C/2024 A1</c>
/// or after the number in <c>13P</c>).
/// </summary>
public enum CometOrbitKind : byte
{
    /// <summary>Periodic comet (P/, orbital period &lt; 200 years or confirmed multi-apparition).</summary>
    Periodic = (byte)'P',
    /// <summary>Non-periodic / long-period comet (C/).</summary>
    LongPeriod = (byte)'C',
    /// <summary>Defunct or lost comet (D/), e.g. D/1993 F2 Shoemaker-Levy 9.</summary>
    Defunct = (byte)'D',
    /// <summary>Comet with no reliable orbit (X/).</summary>
    Uncertain = (byte)'X',
    /// <summary>Object on a cometary orbit with no detected activity (A/).</summary>
    Asteroidal = (byte)'A',
    /// <summary>Interstellar object (I/), e.g. 1I/'Oumuamua, 2I/Borisov.</summary>
    Interstellar = (byte)'I'
}

/// <summary>
/// A parsed IAU comet designation -- either numbered-periodic (<c>13P</c>, <c>73P-C</c>, <c>1I</c>) or
/// provisional (<c>C/2024 A1</c>, <c>P/2023 X1</c>, <c>C/2019 Y4-D</c>, <c>C/2001 OG108</c>,
/// <c>C/-146 P1</c>). It is the single source of truth for the packed form used as a
/// <see cref="CatalogIndex"/> under <see cref="Catalog.Comet"/>.
///
/// <para><b>Packing.</b> The plain 7-bit-ASCII <see cref="CatalogIndex"/> packing (9 chars) cannot hold
/// the longest real designations -- the asteroid-style two-letter half-months of dual-designated active
/// asteroids (e.g. <c>C/2001 OG108</c>, compact 10 chars) -- so instead the designation's components are
/// bit-packed (<see cref="TryToPackedValue"/>: 1-bit numbered/provisional discriminant + 3-bit kind +
/// 11-bit fragment + either a 14-bit periodic number or 13-bit year + 10-bit half-month letters + 10-bit
/// order = &le; 48 bits) and Base91-encoded exactly like the Tycho-2 / PSR / WDS catalogs. This covers the
/// full observed catalog (max order/year/number all fit) with no plain-ASCII length ceiling. The trade vs.
/// the abandoned plain-ASCII scheme is that the raw index is an opaque Base91 value rather than a readable
/// <c>cC2024A1</c> -- but <see cref="ToCanonical"/> still round-trips to <c>C/2024 A1</c>, which is what
/// surfaces in search and display. Parser, the free-text catalog cleanup, and the packer all flow through
/// the one <see cref="TryToPackedValue"/> producer so the packed value can never diverge between paths.</para>
///
/// <para>SBDB's <c>pdes</c> omits the orbit-type prefix for provisional comets (it stores <c>2023 A3</c>,
/// not <c>C/2023 A3</c>) and keeps it in a separate <c>prefix</c> field, so the data source reconstructs
/// the canonical string (<c>prefix + "/" + pdes</c>) before parsing.</para>
/// </summary>
[JsonConverter(typeof(CometDesignationJsonConverter))]
public readonly partial record struct CometDesignation
{
    // Bit-field widths for the packed value (see TryToPackedValue). Must sum (provisional branch) to <= 49
    // so the value + the 7-bit catalog tag fits the 56-bit Base91 body.
    private const int KindBits = 3, FragmentBits = 11, NumberBits = 14, YearBits = 13, LettersBits = 10, OrderBits = 10;
    private const int YearBias = 4096; // maps [-4096, 4095] -> [0, 8191]

    private static ReadOnlySpan<byte> KindByCode => "CPDXAI"u8;

    public CometOrbitKind Kind { get; init; }

    /// <summary>Periodic number for the numbered form (13 for <c>13P</c>); 0 for a provisional designation.</summary>
    public int PeriodicNumber { get; init; }

    /// <summary>Discovery year for the provisional form (may be negative for BC comets); 0 for the numbered form.</summary>
    public int Year { get; init; }

    /// <summary>Half-month discovery letter(s) A..Z for the provisional form (<c>"A"</c>, <c>"OG"</c>); empty for the numbered form.</summary>
    public string HalfMonthLetters { get; init; }

    /// <summary>Order within the half-month for the provisional form (1 for <c>C/2024 A1</c>); 0 when absent (e.g. <c>C/1942 EA</c>) or for the numbered form.</summary>
    public int Order { get; init; }

    /// <summary>Fragment suffix, e.g. "C" for <c>73P-C</c> or "P1" for <c>D/1993 F2-P1</c>; empty when not a fragment.</summary>
    public string Fragment { get; init; }

    public bool IsNumbered => PeriodicNumber > 0;

    // Numbered: "12P/Pons-Brooks", "73P-C", "1I/'Oumuamua" -- optional fragment (letter, or letter+digit
    // for SL9 sub-fragments), optional /Name tail.
    [GeneratedRegex(@"^([0-9]{1,4})([PDI])(?:-([A-Z][A-Z0-9]?))?(?:/.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedPattern { get; }

    // Provisional: "C/2024 A1", "C/2019 Y4-D", "C/2001 OG108", "C/1942 EA" (no order), "C/-146 P1" (BC).
    // Space optional (the catalog-guess path strips spaces); 1-2 half-month letters + 0-3 order digits;
    // optional -fragment; optional trailing "(Name)".
    [GeneratedRegex(@"^([PCDXAI])/(-?[0-9]{1,4}) ?([A-Z]{1,2})([0-9]{0,3})(?:-([A-Z][A-Z0-9]?))?(?: ?\(.*\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProvisionalPattern { get; }

    // Compact payload form: "C2024A1", "C2019Y4D", "C2001OG108", "13P", "73PC", "1I".
    [GeneratedRegex(@"^(?:([0-9]{1,4})([PDI])([A-Z][A-Z0-9]?)?|([PCDXAI])(-?[0-9]{1,4})([A-Z]{1,2})([0-9]{0,3})([A-Z][A-Z0-9]?)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactPattern { get; }

    /// <summary>
    /// Parses a comet designation in canonical (<c>C/2024 A1</c>, <c>73P-C</c>, <c>12P/Pons-Brooks</c>),
    /// space-stripped (<c>C/2024A1</c>) or compact (<c>C2024A1</c>, <c>73PC</c>) form.
    /// Case-insensitive; a trailing <c>/Name</c> or <c>(Name)</c> is ignored.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> input, out CometDesignation designation)
    {
        var trimmed = input.Trim();
        if (trimmed.Length is 0 or > 64)
        {
            designation = default;
            return false;
        }

        Span<char> upper = stackalloc char[trimmed.Length];
        trimmed.ToUpperInvariant(upper);
        var candidate = new string(upper);

        var numbered = NumberedPattern.Match(candidate);
        if (numbered.Success)
        {
            designation = new CometDesignation
            {
                Kind = (CometOrbitKind)(byte)numbered.Groups[2].ValueSpan[0],
                PeriodicNumber = int.Parse(numbered.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                HalfMonthLetters = "",
                Fragment = numbered.Groups[3].Success ? numbered.Groups[3].Value : ""
            };
            return true;
        }

        var provisional = ProvisionalPattern.Match(candidate);
        if (provisional.Success)
        {
            designation = new CometDesignation
            {
                Kind = (CometOrbitKind)(byte)provisional.Groups[1].ValueSpan[0],
                Year = int.Parse(provisional.Groups[2].ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                HalfMonthLetters = provisional.Groups[3].Value,
                Order = provisional.Groups[4].Length > 0 ? int.Parse(provisional.Groups[4].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : 0,
                Fragment = provisional.Groups[5].Success ? provisional.Groups[5].Value : ""
            };
            return true;
        }

        return TryFromCompact(candidate, out designation);
    }

    /// <summary>Parses the compact (punctuation-free) form, e.g. <c>C2024A1</c> or <c>73PC</c>.</summary>
    public static bool TryFromCompact(ReadOnlySpan<char> compact, out CometDesignation designation)
    {
        var match = CompactPattern.Match(new string(compact));
        if (!match.Success)
        {
            designation = default;
            return false;
        }

        if (match.Groups[1].Success)
        {
            designation = new CometDesignation
            {
                Kind = (CometOrbitKind)(byte)match.Groups[2].ValueSpan[0],
                PeriodicNumber = int.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                HalfMonthLetters = "",
                Fragment = match.Groups[3].Success ? match.Groups[3].Value : ""
            };
        }
        else
        {
            designation = new CometDesignation
            {
                Kind = (CometOrbitKind)(byte)match.Groups[4].ValueSpan[0],
                Year = int.Parse(match.Groups[5].ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                HalfMonthLetters = match.Groups[6].Value,
                Order = match.Groups[7].Length > 0 ? int.Parse(match.Groups[7].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : 0,
                Fragment = match.Groups[8].Success ? match.Groups[8].Value : ""
            };
        }
        return true;
    }

    /// <summary>
    /// The compact payload form (no punctuation): <c>C2024A1</c>, <c>C2019Y4D</c>, <c>13P</c>, <c>73PC</c>.
    /// </summary>
    public string ToCompact()
    {
        var order = Order > 0 ? Order.ToString(CultureInfo.InvariantCulture) : "";
        return IsNumbered
            ? string.Create(CultureInfo.InvariantCulture, $"{PeriodicNumber}{(char)Kind}{Fragment}")
            : string.Create(CultureInfo.InvariantCulture, $"{(char)Kind}{Year}{HalfMonthLetters}{order}{Fragment}");
    }

    /// <summary>
    /// The canonical IAU display form: <c>C/2024 A1</c>, <c>C/2019 Y4-D</c>, <c>13P</c>, <c>73P-C</c>.
    /// </summary>
    public string ToCanonical()
    {
        if (IsNumbered)
        {
            return Fragment is { Length: > 0 }
                ? string.Create(CultureInfo.InvariantCulture, $"{PeriodicNumber}{(char)Kind}-{Fragment}")
                : string.Create(CultureInfo.InvariantCulture, $"{PeriodicNumber}{(char)Kind}");
        }

        var order = Order > 0 ? Order.ToString(CultureInfo.InvariantCulture) : "";
        return Fragment is { Length: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"{(char)Kind}/{Year} {HalfMonthLetters}{order}-{Fragment}")
            : string.Create(CultureInfo.InvariantCulture, $"{(char)Kind}/{Year} {HalfMonthLetters}{order}");
    }

    /// <summary>
    /// Bit-packs the designation into a &le; 48-bit value (see the class remarks) for Base91 encoding as a
    /// <see cref="Catalog.Comet"/> <see cref="CatalogIndex"/>. Returns false only when a component is out of
    /// the (generous) field range -- which no real SBDB comet reaches.
    /// </summary>
    internal bool TryToPackedValue(out ulong value)
    {
        value = 0;

        if (KindByCode.IndexOf((byte)(char)Kind) is var kindCode && kindCode < 0)
        {
            return false;
        }

        if (!TryEncodeFragment(Fragment, out var fragCode))
        {
            return false;
        }

        var bit = 0;
        value |= (IsNumbered ? 0UL : 1UL) << bit; bit += 1;
        value |= (ulong)kindCode << bit; bit += KindBits;
        value |= (ulong)fragCode << bit; bit += FragmentBits;

        if (IsNumbered)
        {
            if (PeriodicNumber is <= 0 or >= (1 << NumberBits))
            {
                return false;
            }
            value |= (ulong)PeriodicNumber << bit;
            return true;
        }

        var biasedYear = Year + YearBias;
        if (biasedYear is < 0 or >= (1 << YearBits) || Order is < 0 or >= (1 << OrderBits) || !TryEncodeLetters(HalfMonthLetters, out var lettersCode))
        {
            return false;
        }

        value |= (ulong)biasedYear << bit; bit += YearBits;
        value |= (ulong)lettersCode << bit; bit += LettersBits;
        value |= (ulong)Order << bit;
        return true;
    }

    /// <summary>Reconstructs a designation from the <see cref="TryToPackedValue"/> bit layout.</summary>
    internal static CometDesignation FromPackedValue(ulong value)
    {
        var bit = 0;
        var isProvisional = (value & 1) != 0; bit += 1;
        var kind = (CometOrbitKind)KindByCode[(int)((value >> bit) & ((1 << KindBits) - 1))]; bit += KindBits;
        var fragment = DecodeFragment((int)((value >> bit) & ((1 << FragmentBits) - 1))); bit += FragmentBits;

        if (!isProvisional)
        {
            return new CometDesignation
            {
                Kind = kind,
                PeriodicNumber = (int)((value >> bit) & ((1 << NumberBits) - 1)),
                HalfMonthLetters = "",
                Fragment = fragment
            };
        }

        var year = (int)((value >> bit) & ((1 << YearBits) - 1)) - YearBias; bit += YearBits;
        var letters = DecodeLetters((int)((value >> bit) & ((1 << LettersBits) - 1))); bit += LettersBits;
        var order = (int)((value >> bit) & ((1 << OrderBits) - 1));
        return new CometDesignation
        {
            Kind = kind,
            Year = year,
            HalfMonthLetters = letters,
            Order = order,
            Fragment = fragment
        };
    }

    /// <summary>Packs this designation into a <see cref="Catalog.Comet"/> <see cref="CatalogIndex"/>.</summary>
    public bool TryToCatalogIndex(out CatalogIndex catalogIndex)
    {
        if (TryToPackedValue(out var value))
        {
            catalogIndex = CatalogUtils.AbbreviationToCatalogIndex(CatalogUtils.EncodeCometCatalogIndex(value), isBase91Encoded: true);
            return true;
        }

        catalogIndex = 0;
        return false;
    }

    /// <summary>
    /// Cheap shape probe for a numbered comet designation ("13P", "73P-C", "1I/'Oumuamua"): 1-4 digits,
    /// then P/D/I, then end / fragment / name tail. Used by the catalog-format guesser to route
    /// digit-leading input to <see cref="Catalog.Comet"/> before the 2MASS arms claim it.
    /// </summary>
    internal static bool IsNumberedShape(ReadOnlySpan<char> input)
    {
        var digits = 0;
        while (digits < input.Length && char.IsAsciiDigit(input[digits]))
        {
            digits++;
        }

        if (digits is < 1 or > 4 || digits >= input.Length || char.ToUpperInvariant(input[digits]) is not ('P' or 'D' or 'I'))
        {
            return false;
        }

        return input.Length == digits + 1 || input[digits + 1] is '-' or '/';
    }

    public override string ToString() => ToCanonical();

    // Fragment: 0-2 chars, each A-Z (1-26) or 0-9 (27-36), 0 = absent. code = c0*37 + c1.
    private static bool TryEncodeFragment(string fragment, out int code)
    {
        code = 0;
        if (fragment.Length > 2)
        {
            return false;
        }
        var c0 = fragment.Length > 0 ? FragmentCharCode(fragment[0]) : 0;
        var c1 = fragment.Length > 1 ? FragmentCharCode(fragment[1]) : 0;
        if (c0 < 0 || c1 < 0)
        {
            return false;
        }
        code = c0 * 37 + c1;
        return true;
    }

    private static string DecodeFragment(int code)
    {
        var c0 = code / 37;
        var c1 = code % 37;
        if (c0 == 0)
        {
            return "";
        }
        return c1 == 0
            ? new string(FragmentCharFromCode(c0), 1)
            : new string([FragmentCharFromCode(c0), FragmentCharFromCode(c1)]);
    }

    private static int FragmentCharCode(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A' + 1,
        >= '0' and <= '9' => c - '0' + 27,
        _ => -1
    };

    private static char FragmentCharFromCode(int code) => code <= 26 ? (char)('A' + code - 1) : (char)('0' + code - 27);

    // Half-month letters: 1-2 letters A-Z. code = (l0-1)*27 + l1, l0 in 1..26, l1 in 0..26 (0 = single letter).
    private static bool TryEncodeLetters(string letters, out int code)
    {
        code = 0;
        if (letters.Length is < 1 or > 2 || letters[0] is < 'A' or > 'Z' || (letters.Length == 2 && letters[1] is < 'A' or > 'Z'))
        {
            return false;
        }
        var l0 = letters[0] - 'A' + 1;
        var l1 = letters.Length == 2 ? letters[1] - 'A' + 1 : 0;
        code = (l0 - 1) * 27 + l1;
        return true;
    }

    private static string DecodeLetters(int code)
    {
        var l0 = code / 27 + 1;
        var l1 = code % 27;
        var first = (char)('A' + l0 - 1);
        return l1 == 0 ? new string(first, 1) : new string([first, (char)('A' + l1 - 1)]);
    }
}
