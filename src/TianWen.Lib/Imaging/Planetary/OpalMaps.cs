using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// OPAL's global maps of Jupiter and Saturn in a folder (<c>jupiter-2023a_f631n_v1_globalmap.fits</c> and its apparition's readme),
/// grouped by apparition year: the truth a synthetic twin is rendered from (R2) and the colour <c>planetary colour</c> balances to.
/// One reader for both, so a twin and a colour balance never read a year's maps two ways (docs/plans/planetary-stacking.md, A1).
/// </summary>
public static partial class OpalMaps
{
    /// <summary>
    /// The planet's maps in <paramref name="folder"/>, grouped by apparition year, each visible filter's rotations together, every filter's
    /// I/F factor and Minnaert k read from its apparition's readme (the last by name where a year has several). Saturn's maps are filled
    /// zonally where the rings hid the globe. A year with no readme, or fewer than two filters, is left out.
    /// </summary>
    public static ImmutableArray<OpalApparition> ReadApparitions(string folder, CatalogIndex planet)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var name = planet == CatalogIndex.Saturn ? "saturn" : "jupiter";
        var maps = new List<(int Year, string Filter, string Path)>();
        foreach (var path in FileEnumeration.EnumerateFiles(folder, "_globalmap.fits", recursive: false))
        {
            if (MapName().Match(Path.GetFileName(path)) is { Success: true } m && string.Equals(m.Groups["planet"].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                maps.Add((int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture), m.Groups["filter"].Value.ToUpperInvariant(), path));
            }
        }
        var readmes = FileEnumeration.EnumerateFiles(folder, "_readme.txt", recursive: false).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var apparitions = ImmutableArray.CreateBuilder<OpalApparition>();
        foreach (var year in maps.Select(m => m.Year).Distinct().Order())
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{name}-{year}");
            if (readmes.LastOrDefault(r => Path.GetFileName(r).Contains(key, StringComparison.OrdinalIgnoreCase)) is not { } readme)
            {
                continue;
            }
            var filters = ImmutableArray.CreateBuilder<OpalFilter>();
            var byFilter = ImmutableArray.CreateBuilder<ImmutableArray<PlanetMap>>();
            foreach (var filter in PlanetaryColour.ReadmeFilters(File.ReadLines(readme)))
            {
                var rotations = maps.Where(m => m.Year == year && m.Filter == filter.Name).OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(m => PlanetMap.ReadFits(m.Path)).OfType<PlanetMap>()
                    .Select(map => planet == CatalogIndex.Saturn ? map.FilledZonally() : map).ToImmutableArray();
                if (rotations.Length > 0)
                {
                    filters.Add(filter);
                    byFilter.Add(rotations);
                }
            }
            if (filters.Count >= 2)
            {
                apparitions.Add(new OpalApparition(year, filters.ToImmutable(), byFilter.ToImmutable()));
            }
        }
        return apparitions.ToImmutable();
    }

    /// <summary>
    /// The map a twin of a capture taken in <paramref name="year"/> at <paramref name="wavelengthNm"/> is rendered from: the apparition
    /// nearest that year (the later on a tie), its filter nearest the wavelength, its first rotation. Null with no apparitions. The
    /// planet's belts change from year to year (R8 follow-up 3: another year's spectrum is no object below 0.2 cycles a pixel), so the
    /// year the map came from is returned beside it for the caller to report.
    /// </summary>
    public static (PlanetMap Map, int Year, OpalFilter Filter)? ForCapture(ImmutableArray<OpalApparition> apparitions, int year, double wavelengthNm)
    {
        if (apparitions.IsDefaultOrEmpty)
        {
            return null;
        }
        var apparition = apparitions.OrderBy(a => Math.Abs(a.Year - year)).ThenByDescending(a => a.Year).First();
        var nearest = 0;
        for (var f = 1; f < apparition.Filters.Length; f++)
        {
            if (Math.Abs(apparition.Filters[f].PivotNm - wavelengthNm) < Math.Abs(apparition.Filters[nearest].PivotNm - wavelengthNm))
            {
                nearest = f;
            }
        }
        return (apparition.Maps[nearest][0], apparition.Year, apparition.Filters[nearest]);
    }

    [GeneratedRegex(@"(?<planet>jupiter|saturn)-(?<year>\d{4})[a-z]_(?<filter>f\w+?)_v1_globalmap\.fits$", RegexOptions.IgnoreCase)]
    private static partial Regex MapName();
}
