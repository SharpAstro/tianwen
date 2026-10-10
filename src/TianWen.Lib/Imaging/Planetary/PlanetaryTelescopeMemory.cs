using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The telescope each camera was last said to be on (A4, #1391): what AUTO takes for a capture whose header, capture program and path
/// name none, which is every capture SharpCap writes with its default "Telescope" field and no telescope in its folder's name. Said by a
/// person, never guessed: the viewer remembers the telescope its panel is set to for the capture on show, and <c>planetary stack</c> a
/// telescope given to it. One file, <c>Planetary/telescopes.json</c>, which every host adds to, so it is updated, never rewritten whole.
/// </summary>
public static class PlanetaryTelescopeMemory
{
    /// <summary>Where the telescopes are kept.</summary>
    public static string PathFor(IExternal external) => Path.Combine(external.AppDataFolder.FullName, "Planetary", "telescopes.json");

    /// <summary>The telescope <paramref name="camera"/> was last said to be on, or null when none was, or the camera has no name.</summary>
    public static async Task<RememberedTelescope?> RecallAsync(IExternal external, string? camera, CancellationToken cancellationToken = default)
    {
        if (Key(camera) is not { } key)
        {
            return null;
        }
        var saved = await external.TryReadJsonAsync(PathFor(external), PlanetaryTelescopeMemoryJsonContext.Default.TelescopesByCamera, logger: null, cancellationToken)
            .ConfigureAwait(false);
        return saved?.Cameras is { } cameras && cameras.TryGetValue(key, out var telescope) ? telescope : null;
    }

    /// <summary>Remembers that <paramref name="camera"/> is on a telescope of <paramref name="apertureMm"/> and <paramref name="design"/>.</summary>
    public static Task RememberAsync(IExternal external, string? camera, int apertureMm, OpticalDesign design, CancellationToken cancellationToken = default)
    {
        if (Key(camera) is not { } key || apertureMm <= 0)
        {
            return Task.CompletedTask;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(PathFor(external)) ?? external.AppDataFolder.FullName);
        return external.UpdateJsonAsync(PathFor(external), PlanetaryTelescopeMemoryJsonContext.Default.TelescopesByCamera, saved =>
        {
            var cameras = new Dictionary<string, RememberedTelescope>(saved?.Cameras ?? [], StringComparer.Ordinal)
            {
                [key] = new RememberedTelescope(apertureMm, design),
            };
            return new TelescopesByCamera(cameras);
        }, logger: null, cancellationToken);
    }

    // A camera's name as it is kept: trimmed, lower case, its runs of blanks one space, so "ZWO ASI462MC" and "zwo  asi462mc" are one camera.
    private static string? Key(string? camera)
    {
        if (string.IsNullOrWhiteSpace(camera))
        {
            return null;
        }
        return string.Join(' ', camera.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>A camera's remembered telescope: its aperture and its design.</summary>
public sealed record RememberedTelescope(int ApertureMm, OpticalDesign Design);

/// <summary>Every camera's remembered telescope, by its name as <see cref="PlanetaryTelescopeMemory"/> keys it.</summary>
public sealed record TelescopesByCamera(Dictionary<string, RememberedTelescope> Cameras);

[JsonSerializable(typeof(TelescopesByCamera))]
internal partial class PlanetaryTelescopeMemoryJsonContext : JsonSerializerContext;
