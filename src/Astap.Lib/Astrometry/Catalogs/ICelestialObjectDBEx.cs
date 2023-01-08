using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib.Astrometry.Catalogs;

public static class ICelestialObjectDBEx
{
    /// <summary>
    /// Uses <see cref="ICelestialObjectDB.CommonNames"/> and <see cref="ICelestialObjectDB.ObjectIndices"/> to create a list
    /// of all names and designations.
    /// </summary>
    /// <param name="this">Initialised object db</param>
    /// <returns>copied array of all names and canonical designations</returns>
    public static string[] CreateAutoCompleteList(this ICelestialObjectDB @this)
    {
        var commonNames = @this.CommonNames;
        var objIndices = @this.ObjectIndices;

        var canonicalSet = new HashSet<string>((int)(objIndices.Count * 1.3f));
        foreach (var objIndex in objIndices)
        {
            canonicalSet.Add(objIndex.ToCanonical(CanonicalFormat.Normal));
            canonicalSet.Add(objIndex.ToCanonical(CanonicalFormat.Long));
        }
        var canonicalArray = canonicalSet.ToArray();

        var names = new string[canonicalArray.Length + commonNames.Count];
        canonicalArray.CopyTo(names, 0);
        commonNames.CopyTo(names, canonicalArray.Length);

        return names;
    }

    internal static TSet IndicesToCatalogs<TSet>(this ICelestialObjectDB @this)
         where TSet : ISet<Catalog>, new()
    {
        var catalogs = new TSet();
        foreach (var objIndex in @this.ObjectIndices)
        {
            _ = catalogs.Add(objIndex.ToCatalog());
        }

        return catalogs;
    }
}
