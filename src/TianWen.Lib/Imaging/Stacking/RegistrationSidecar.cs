using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Persists / loads <see cref="RegistrationResult"/> sidecars adjacent to each
/// light frame as <c>&lt;light&gt;.match.json</c>. Atomic write semantics via
/// temp-file rename so a Ctrl-C mid-write leaves the existing sidecar intact.
/// </summary>
/// <remarks>
/// Re-implements the atomic-write pattern from <see cref="Devices.IExternal"/>
/// as a static helper because <see cref="Registrator"/> doesn't otherwise need
/// the full IExternal surface (DI container, device cache, etc.) — keeping the
/// stacking pipeline as a pure-library concern with minimal coupling.
/// </remarks>
public static class RegistrationSidecar
{
    /// <summary>Suffix appended to a light path to form its sidecar path.</summary>
    public const string Suffix = ".match.json";

    /// <summary>Returns the sidecar path for a given light frame.</summary>
    public static string PathFor(string lightPath) => lightPath + Suffix;

    /// <summary>
    /// Atomically writes <paramref name="result"/> to <c>&lt;LightPath&gt;.match.json</c>.
    /// Writes to a <c>.tmp</c> file first, then renames over the destination.
    /// </summary>
    public static async Task WriteAsync(RegistrationResult result, CancellationToken cancellationToken = default)
    {
        var path = PathFor(result.LightPath);
        var tmp = path + ".tmp";

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, result, RegistrationJsonContext.Default.RegistrationResult, cancellationToken);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads the sidecar for <paramref name="lightPath"/>. Returns null if the
    /// sidecar doesn't exist, fails to parse, or its <see cref="RegistrationResult.LightPath"/>
    /// doesn't match (the JSON was for a different file — e.g. someone renamed
    /// the light without re-registering).
    /// </summary>
    public static async Task<RegistrationResult?> TryReadAsync(string lightPath, CancellationToken cancellationToken = default)
    {
        var path = PathFor(lightPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await JsonSerializer.DeserializeAsync(stream, RegistrationJsonContext.Default.RegistrationResult, cancellationToken);
            if (result is null || result.LightPath != lightPath)
            {
                // Sidecar contents don't match the file we asked for; treat as
                // missing so the caller re-registers rather than applying a
                // transform from a different image.
                return null;
            }
            return result;
        }
        catch (System.Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
