using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// A master's MEASURED bright-star halo (2026-11-07, real-star check 3's domain gap): per channel, the light its real bright stars
/// carry over what the field profile draws for the same catalogued amplitude, read on the master less its starless plate and
/// tabled per pixel of radius (<see cref="OuterHalo.Table"/>), for two classes: unsaturated stars of
/// <see cref="MinSignificance"/> and up, and saturated stars. Drawn under an injected star of that class
/// (<see cref="InjectionPopulation"/>), so the synthetic bright stars carry the real ones' light 3 to 30 px out, which neither
/// the field Moffat (it falls to nothing past about 12 px) nor a fitted outer halo (#1870, whose Moffat the core mismatch bent
/// onto its search bounds) drew. A class with no table draws none. Measured per session, never pooled: the halo is the rig's
/// optics, its filter and its night's sky together.
/// </summary>
/// <param name="SessionId">The session the table was read on, without a flip suffix (a flip is the same master, and the
/// halo is round).</param>
/// <param name="Bright">Per channel, the unsaturated class's table; empty for none.</param>
/// <param name="Saturated">Per channel, the saturated class's table; empty for none.</param>
public sealed record BrightHalo(string SessionId, ImmutableArray<ImmutableArray<float>> Bright, ImmutableArray<ImmutableArray<float>> Saturated)
{
    /// <summary>The lowest catalogued significance an unsaturated star takes the bright class's halo at.</summary>
    public const float MinSignificance = 1000f;

    /// <summary>The key a session is looked up by: its id without a <c>|flip=</c> suffix.</summary>
    public static string KeyOf(string sessionId)
        => sessionId.IndexOf("|flip=", StringComparison.Ordinal) is var i and >= 0 ? sessionId[..i] : sessionId;

    /// <summary>
    /// The halo a star of <paramref name="significance"/> is drawn with, one per channel, or the default array for a star
    /// below the bright class or a class this master has no table for. A saturated star is drawn at positive infinity.
    /// </summary>
    public ImmutableArray<OuterHalo> For(float significance, int channels)
    {
        if (!(significance >= MinSignificance))
        {
            return default;
        }
        var saturated = float.IsPositiveInfinity(significance);
        var tables = saturated ? Saturated : Bright;
        if (tables.IsDefaultOrEmpty || tables.Length != channels)
        {
            return default;
        }
        var source = $"{SessionId}:{(saturated ? "saturated" : "bright")}";
        return [.. tables.Select(t => new OuterHalo(0.0, 0.0, 0.0, t, source))];
    }

    /// <summary>
    /// Reads a bright-halos file: one JSON object keyed by session id (flip suffixes removed on reading), each holding a
    /// <c>bright</c> and a <c>saturated</c> class, each an array per channel of the excess per pixel of radius from 0.
    /// </summary>
    public static async Task<Dictionary<string, BrightHalo>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var result = new Dictionary<string, BrightHalo>(StringComparer.Ordinal);
        foreach (var session in doc.RootElement.EnumerateObject())
        {
            static ImmutableArray<ImmutableArray<float>> Class(JsonElement e, string name)
                => e.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.Array
                    ? [.. c.EnumerateArray().Select(static ch => ch.EnumerateArray().Select(static v => v.GetSingle()).ToImmutableArray())]
                    : [];
            var key = KeyOf(session.Name);
            result[key] = new BrightHalo(key, Class(session.Value, "bright"), Class(session.Value, "saturated"));
        }
        return result;
    }
}
