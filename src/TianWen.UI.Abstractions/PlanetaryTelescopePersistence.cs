using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The telescope the viewer's Best stack panel holds (<see cref="ViewerState.PlanetaryApertureMm"/>, <see cref="ViewerState.PlanetaryDesign"/>),
/// remembered between runs of <c>tianwen-fits</c>, which has no profile to read it from (the owner's choice of 2026-10-02, #1159). One
/// small file, <c>Viewer/planetary-telescope.json</c>, written atomically as every AppData file is.
/// </summary>
public static class PlanetaryTelescopePersistence
{
    /// <summary>Where the telescope is kept.</summary>
    public static string PathFor(IExternal external) => Path.Combine(external.AppDataFolder.FullName, "Viewer", "planetary-telescope.json");

    /// <summary>Restores the panel's telescope, when one was saved; leaves the defaults otherwise.</summary>
    public static async Task LoadAsync(ViewerState state, IExternal external, CancellationToken cancellationToken)
    {
        if (await external.TryReadJsonAsync(PathFor(external), PlanetaryTelescopeJsonContext.Default.PlanetaryTelescopeDto, logger: null, cancellationToken) is { } saved)
        {
            state.PlanetaryApertureMm = saved.ApertureMm;
            state.PlanetaryDesign = saved.Design;
            state.NeedsRedraw = true;
        }
    }

    /// <summary>Saves the panel's telescope as it stands.</summary>
    public static Task SaveAsync(ViewerState state, IExternal external, CancellationToken cancellationToken)
        => external.AtomicWriteJsonAsync(PathFor(external), new PlanetaryTelescopeDto(state.PlanetaryApertureMm, state.PlanetaryDesign),
            PlanetaryTelescopeJsonContext.Default.PlanetaryTelescopeDto, cancellationToken);
}

/// <summary>The saved telescope: its aperture in mm (null for none) and its design.</summary>
public sealed record PlanetaryTelescopeDto(int? ApertureMm, OpticalDesign Design);

[JsonSerializable(typeof(PlanetaryTelescopeDto))]
internal partial class PlanetaryTelescopeJsonContext : JsonSerializerContext;
