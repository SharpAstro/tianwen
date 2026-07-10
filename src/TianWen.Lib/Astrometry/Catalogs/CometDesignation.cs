using System;
using System.Globalization;
using System.Text.RegularExpressions;

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
/// provisional (<c>C/2024 A1</c>, <c>P/2023 X1</c>, <c>C/2019 Y4-D</c>). This is the single source of
/// truth for the compact packed form used as <see cref="CatalogIndex"/> under <see cref="Catalog.Comet"/>:
/// a lowercase <c>c</c> catalog tag followed by the designation with all punctuation dropped
/// (<c>cC2024A1</c>, <c>c73PC</c>) -- the digit/letter alternation of the grammar keeps that lossless,
/// so it fits the plain 7-bit-ASCII <see cref="CatalogIndex"/> packing (max 9 chars) and stays readable
/// in the debugger. Parser (<see cref="TryParse"/>) and formatter (<see cref="ToCanonical"/>) both flow
/// through <see cref="ToCompact"/>, so the packed value is bit-identical no matter which path produced it
/// (the <c>Pl-Sol</c> free-text-vs-literal mismatch must never be repeated here). Designations whose
/// compact form exceeds 9 chars (3-digit half-month order plus fragment: SOHO-style sungrazers, never
/// observable targets) do not fit and are rejected by <see cref="TryToCatalogIndex"/> -- callers skip them.
/// </summary>
public readonly partial record struct CometDesignation
{
    private const int MaxCompactLength = 8; // 9 incl. the 'c' catalog tag

    public CometOrbitKind Kind { get; init; }

    /// <summary>Periodic number for the numbered form (13 for <c>13P</c>); 0 for a provisional designation.</summary>
    public ushort PeriodicNumber { get; init; }

    /// <summary>Discovery year for the provisional form (2024 for <c>C/2024 A1</c>); 0 for a numbered designation.</summary>
    public ushort Year { get; init; }

    /// <summary>Half-month discovery letter A..Y (excluding I) for the provisional form; '\0' for a numbered designation.</summary>
    public char HalfMonth { get; init; }

    /// <summary>Order within the half-month for the provisional form (1 for <c>C/2024 A1</c>); 0 for a numbered designation.</summary>
    public ushort Order { get; init; }

    /// <summary>Fragment letter(s), e.g. "C" for <c>73P-C</c> or "BB" for <c>73P-BB</c>; empty when not a fragment.</summary>
    public string Fragment { get; init; }

    public bool IsNumbered => PeriodicNumber > 0;

    // Numbered: "12P/Pons-Brooks", "73P-C", "1I/'Oumuamua" -- optional fragment, optional /Name tail.
    [GeneratedRegex(@"^([0-9]{1,4})([PDI])(?:-([A-Z]{1,2}))?(?:/.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedPattern { get; }

    // Provisional: "C/2024 A1", "C/2019 Y4-D" -- space optional (the catalog-guess path strips spaces),
    // optional -fragment, optional trailing "(Name)".
    [GeneratedRegex(@"^([PCDXAI])/([0-9]{4}) ?([A-HJ-Y])([0-9]{1,3})(?:-([A-Z]{1,2}))?(?: ?\(.*\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProvisionalPattern { get; }

    // Compact packed payload (after the 'c' tag): "C2024A1", "C2019Y4D", "13P", "73PC", "1I".
    [GeneratedRegex(@"^(?:([0-9]{1,4})([PDI])([A-Z]{1,2})?|([PCDXAI])([0-9]{4})([A-HJ-Y])([0-9]{1,3})([A-Z]{1,2})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactPattern { get; }

    /// <summary>
    /// Parses a comet designation in canonical (<c>C/2024 A1</c>, <c>73P-C</c>, <c>12P/Pons-Brooks</c>),
    /// space-stripped (<c>C/2024A1</c>) or compact (<c>C2024A1</c>, <c>73PC</c>) form.
    /// Case-insensitive; a trailing <c>/Name</c> or <c>(Name)</c> is ignored.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> input, out CometDesignation designation)
    {
        var trimmed = input.Trim();
        Span<char> upper = stackalloc char[Math.Min(trimmed.Length, 64)];
        if (trimmed.Length > upper.Length)
        {
            designation = default;
            return false;
        }
        trimmed.ToUpperInvariant(upper);
        var candidate = new string(upper[..trimmed.Length]);

        var numbered = NumberedPattern.Match(candidate);
        if (numbered.Success)
        {
            designation = new CometDesignation
            {
                Kind = (CometOrbitKind)(byte)numbered.Groups[2].ValueSpan[0],
                PeriodicNumber = ushort.Parse(numbered.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                Fragment = numbered.Groups[3].Success ? numbered.Groups[3].Value : "",
                HalfMonth = '\0'
            };
            return true;
        }

        var provisional = ProvisionalPattern.Match(candidate);
        if (provisional.Success)
        {
            designation = new CometDesignation
            {
                Kind = (CometOrbitKind)(byte)provisional.Groups[1].ValueSpan[0],
                Year = ushort.Parse(provisional.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                HalfMonth = provisional.Groups[3].ValueSpan[0],
                Order = ushort.Parse(provisional.Groups[4].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                Fragment = provisional.Groups[5].Success ? provisional.Groups[5].Value : ""
            };
            return true;
        }

        return TryFromCompact(candidate, out designation);
    }

    /// <summary>
    /// Parses the compact (punctuation-free) payload as decoded from a <see cref="Catalog.Comet"/>
    /// <see cref="CatalogIndex"/>, e.g. <c>C2024A1</c> or <c>73PC</c>.
    /// </summary>
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
                PeriodicNumber = ushort.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                Fragment = match.Groups[3].Success ? match.Groups[3].Value : "",
                HalfMonth = '\0'
            };
        }
        else
        {
            designation = new CometDesignation
            {
                Kind = (CometOrbitKind)(byte)match.Groups[4].ValueSpan[0],
                Year = ushort.Parse(match.Groups[5].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                HalfMonth = match.Groups[6].ValueSpan[0],
                Order = ushort.Parse(match.Groups[7].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
                Fragment = match.Groups[8].Success ? match.Groups[8].Value : ""
            };
        }
        return true;
    }

    /// <summary>
    /// The compact packed payload (no punctuation, no <c>c</c> tag): <c>C2024A1</c>, <c>C2019Y4D</c>,
    /// <c>13P</c>, <c>73PC</c>. Digit/letter alternation keeps it lossless.
    /// </summary>
    public string ToCompact() => IsNumbered
        ? string.Create(CultureInfo.InvariantCulture, $"{PeriodicNumber}{(char)Kind}{Fragment}")
        : string.Create(CultureInfo.InvariantCulture, $"{(char)Kind}{Year}{HalfMonth}{Order}{Fragment}");

    /// <summary>
    /// The canonical IAU display form: <c>C/2024 A1</c>, <c>C/2019 Y4-D</c>, <c>13P</c>, <c>73P-C</c>.
    /// </summary>
    public string ToCanonical() => IsNumbered
        ? Fragment is { Length: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"{PeriodicNumber}{(char)Kind}-{Fragment}")
            : string.Create(CultureInfo.InvariantCulture, $"{PeriodicNumber}{(char)Kind}")
        : Fragment is { Length: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"{(char)Kind}/{Year} {HalfMonth}{Order}-{Fragment}")
            : string.Create(CultureInfo.InvariantCulture, $"{(char)Kind}/{Year} {HalfMonth}{Order}");

    /// <summary>
    /// The full plain-ASCII packed abbreviation (the <c>c</c> catalog tag + <see cref="ToCompact"/>),
    /// i.e. exactly the string whose 7-bit pack IS the <see cref="CatalogIndex"/> value. The single
    /// producer shared by <see cref="TryToCatalogIndex"/> and the free-text catalog-name cleanup, so the
    /// packed ulong can never diverge between the two paths. <c>null</c> when the compact form exceeds
    /// the 9-char packing budget (SOHO-style high-order fragments).
    /// </summary>
    internal string? ToPackedAbbreviationOrNull()
    {
        var compact = ToCompact();
        return compact.Length <= MaxCompactLength ? "c" + compact : null;
    }

    /// <summary>
    /// Packs this designation into a <see cref="Catalog.Comet"/> <see cref="CatalogIndex"/>
    /// (plain 7-bit-ASCII packing of <c>c</c> + <see cref="ToCompact"/>). Fails when the compact
    /// form exceeds the 9-char packing budget (SOHO-style high-order fragments).
    /// </summary>
    public bool TryToCatalogIndex(out CatalogIndex catalogIndex)
    {
        if (ToPackedAbbreviationOrNull() is { } packed)
        {
            catalogIndex = CatalogUtils.AbbreviationToCatalogIndex(packed, isBase91Encoded: false);
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
}
