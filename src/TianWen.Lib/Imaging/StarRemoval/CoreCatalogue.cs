using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>Which star catalogue core protection asks for the stars inside a protected core (<c>--core-catalogue</c>).</summary>
public enum CoreCatalogueChoice
{
    /// <summary>The star database ASTAP installs and, without one, the embedded Tycho-2.</summary>
    Auto,

    /// <summary>The embedded Tycho-2 alone.</summary>
    Embedded,

    /// <summary>None: every star inside a core stays in the starless plate with it.</summary>
    None,
}

/// <summary>
/// The catalogued stars core protection reads for a plate-solved master (#2242), and what a mask found, in words:
/// the one routine behind <c>image remove-stars</c>, <c>image protect-cores</c>, <c>image combine</c> and the
/// operations an edit list runs.
/// </summary>
public static class CoreCatalogue
{
    /// <summary>The choice a <c>--core-catalogue</c> value names; anything else is <see cref="CoreCatalogueChoice.Auto"/>,
    /// the option's default (the option itself accepts only the three words).</summary>
    public static CoreCatalogueChoice Parse(string? choice) => choice switch
    {
        "embedded" => CoreCatalogueChoice.Embedded,
        "none" => CoreCatalogueChoice.None,
        _ => CoreCatalogueChoice.Auto,
    };

    /// <summary>The word a choice is written as on a command line.</summary>
    public static string Word(CoreCatalogueChoice choice) => choice.ToString().ToLowerInvariant();

    /// <summary>
    /// What would be asked for these stars on this computer, for a step's key: the installed database's name, the
    /// embedded catalogue, or none. A result made with one is not a result made with another.
    /// </summary>
    public static string Source(CoreCatalogueChoice choice) => choice switch
    {
        CoreCatalogueChoice.None => "none",
        CoreCatalogueChoice.Embedded => "embedded",
        _ => AstapStarDatabase.FindInstalled() is { } installed ? installed.Name : "embedded",
    };

    /// <summary>
    /// The catalogued stars of a solved frame, for core protection; null, with one line said why, where there are none.
    /// </summary>
    /// <param name="choice">Which catalogue to ask.</param>
    /// <param name="wcs">The frame's plate solution. A header's pointing, or the matrix made up from its pixel scale and
    /// angle, is read as a WCS too and is no solution.</param>
    /// <param name="width">The frame's width.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="embedded">The embedded catalogue, or null where the caller was built without it.</param>
    /// <param name="say">Takes the one line that says what was read or why nothing was.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    public static async Task<IReadOnlyList<FrameStar>?> ReadAsync(CoreCatalogueChoice choice, WCS? wcs, int width, int height, ICelestialObjectDB? embedded,
        Action<string>? say, CancellationToken cancellationToken = default)
    {
        if (choice is CoreCatalogueChoice.None)
        {
            return null;
        }

        if (wcs is not { } solution || !CatalogFrameStars.IsSolved(solution))
        {
            say?.Invoke("the master carries no plate solution, so stars inside a protected core stay with it (tianwen solve writes one)");
            return null;
        }

        if (choice is CoreCatalogueChoice.Embedded && embedded is null)
        {
            // Only where the caller was built without the catalogue; the program always hands it one.
            say?.Invoke("the embedded catalogue is not at hand here, so stars inside a protected core stay with it");
            return null;
        }

        var installed = choice is CoreCatalogueChoice.Embedded ? null : AstapStarDatabase.FindInstalled();
        var (stars, why) = await CatalogFrameStars.ReadAsync(solution, width, height, installed, embedded, cancellationToken);
        if (stars is null)
        {
            say?.Invoke($"no catalogued stars ({why}), so stars inside a protected core stay with it");
            return null;
        }

        say?.Invoke($"catalogued stars from {stars.Source}");
        return stars.Stars;
    }

    /// <summary>Says what a mask protected on the image called <paramref name="name"/>, and which stars inside its
    /// cores the catalogue took back out.</summary>
    public static void Describe(string name, ExtendedCoreMask mask, Action<string>? say)
    {
        if (say is null)
        {
            return;
        }

        if (mask.Cores.Length == 0)
        {
            say($"{name}: no extended source bright enough to protect");
            return;
        }

        say($"{name}: protected " + string.Join("; ", mask.Cores.Select(static c =>
            string.Create(CultureInfo.InvariantCulture, $"({c.X}, {c.Y}), peak {c.PeakSigma:F0} sigma, {c.CoreAreaPx} px of {c.SourceAreaPx}"))));
        if (mask.StarsLeftOut is { } found)
        {
            say(found.CalibrationStars == 0
                ? $"{name}: too few catalogued stars show on the frame to read a zero point, so the stars inside the cores stay with them"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{name}: {found.Stars} of {found.Peaks} compact peaks inside the cores are catalogued stars and stay removed "
                    + $"(zero point {found.ZeroPoint:F2} over {found.CalibrationStars} stars, scatter {found.ZeroPointScatter:F2} mag; "
                    + $"a star of magnitude 10 reaches {found.ReachAtMagnitude10:F0} px)"));
        }
    }
}
