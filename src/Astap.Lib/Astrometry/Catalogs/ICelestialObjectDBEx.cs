using System.Collections.Generic;

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
        var names = new string[objIndices.Count + commonNames.Count];
        var i = 0;

        foreach (var objIndex in objIndices)
        {
            names[i++] = objIndex.ToCanonical();
        }

        commonNames.CopyTo(names, i);

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
