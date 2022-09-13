using System.Runtime.CompilerServices;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Astrometry
{
    /// <summary>
    /// Represents a unique entry in a catalogue s.th. NGC0001, M13 or IC0001
    /// </summary>
    public enum CatalogIndex : ulong { }

    public static class CatalogIndexEx
    {
        public static string ToAbbreviation(this CatalogIndex catalogIndex) => EnumValueToAbbreviation((ulong)catalogIndex);
    }
}
