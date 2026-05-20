using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.AI.MCP.Tools;

/// <summary>
/// Catalog lookup tools. Phase A surfaces a single by-designation lookup;
/// later phases add spatial queries and the virtual TYC prefix search.
/// </summary>
[McpServerToolType]
public class CatalogTools
{
    [McpServerTool, Description("Look up a catalog object by designation: NGC/IC/Messier/Caldwell (e.g. 'NGC 7331', 'M 31', 'C 9'), HIP/HD numbers ('HIP 17499', 'HD 23302'), Tycho-2 (TYC 1799-1441-1), or common names ('Vega', 'Barnard's Star'). Returns the parsed CelestialObject -- RA/Dec/Mag/colour/object type and any cross-references.")]
    public static async Task<string> Lookup(
        ICelestialObjectDB db,
        [Description("Catalog designation or common name.")] string designation,
        CancellationToken ct = default)
    {
        // Init is idempotent + fast-path-protected; first call pays the
        // Tycho-2 bulk-decode cost, subsequent calls are free.
        await db.InitDBAsync(waitForTycho2BulkLoad: true, ct);

        return db.TryLookupByIndex(designation, out var obj)
            ? obj.ToString()
            : $"NOT FOUND: {designation}";
    }
}
