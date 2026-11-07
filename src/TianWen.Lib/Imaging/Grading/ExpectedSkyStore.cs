using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging.Grading;

/// <summary>
/// What one fitted <see cref="ExpectedSkyModel"/> is for: the site (to 0.1 degree, as the clear-night references are), the rig
/// (<c>LightGrading.RigOf</c>: gain, offset, ISO, instrument, telescope, the same string <see cref="LightGradeReference"/> keeps),
/// and the sensor, filter, exposure and bin of <see cref="LightGradeKey"/>, minus the frame size and the target, neither of which
/// the sky depends on.
/// </summary>
public readonly record struct ExpectedSkyKey(double SiteLatitude, double SiteLongitude, string Rig, SensorType SensorType, string FilterKey,
    long ExposureMs, int Bin)
{
    /// <summary>The key in words, for the CLI's table.</summary>
    public string Describe()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"site {SiteLatitude:0.0},{SiteLongitude:0.0} rig [{Rig}] {SensorType} {(FilterKey.Length > 0 ? FilterKey : "no filter")} {ExposureMs / 1000.0:0.###} s bin {Bin}");
}

/// <summary>One kept model with the number of nights it was fitted over.</summary>
public sealed record ExpectedSkyEntry(ExpectedSkyKey Key, ExpectedSkyModel Model, int Nights);

/// <summary>
/// The fitted expected-sky models (#1974), kept beside the clear-night references under the same rig key: <c>Viewer/expected-sky.json</c>
/// under AppData. Every save replaces the entries of the keys it fitted and leaves the others, so fitting a second folder of another
/// filter adds to the file, and re-fitting a key over more nights is how a model improves. Written through
/// <c>UpdateJsonAsync</c> and read through <c>TryReadJsonAsync</c> (two processes cannot lose each other's keys).
/// </summary>
public static class ExpectedSkyStore
{
    /// <summary>Where the models are kept: <c>Viewer/expected-sky.json</c>.</summary>
    public static string PathFor(IExternal external) => Path.Combine(external.AppDataFolder.FullName, "Viewer", "expected-sky.json");

    /// <summary>Every kept model; empty (never default) when none was saved.</summary>
    public static async Task<ImmutableArray<ExpectedSkyEntry>> LoadAsync(IExternal external, CancellationToken cancellationToken)
        => await external.TryReadJsonAsync(PathFor(external), ExpectedSkyJsonContext.Default.ExpectedSkyFile, logger: null, cancellationToken)
            .ConfigureAwait(false) is { } saved
            ? [.. saved.Models]
            : [];

    /// <summary>The entry for <paramref name="key"/> in <paramref name="entries"/>, or null when the key was never fitted.</summary>
    public static ExpectedSkyEntry? Find(ImmutableArray<ExpectedSkyEntry> entries, in ExpectedSkyKey key)
    {
        foreach (var entry in entries)
        {
            if (entry.Key == key)
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>Adds <paramref name="fitted"/> to the file (an entry of the same key replaced) and returns what the file holds afterwards.</summary>
    public static async Task<ImmutableArray<ExpectedSkyEntry>> SaveAsync(IExternal external, IReadOnlyList<ExpectedSkyEntry> fitted,
        CancellationToken cancellationToken)
    {
        if (fitted.Count == 0)
        {
            return await LoadAsync(external, cancellationToken).ConfigureAwait(false);
        }
        var path = PathFor(external);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? external.AppDataFolder.FullName);
        var after = ImmutableArray<ExpectedSkyEntry>.Empty;
        await external.UpdateJsonAsync(path, ExpectedSkyJsonContext.Default.ExpectedSkyFile, saved =>
        {
            var merged = new Dictionary<ExpectedSkyKey, ExpectedSkyEntry>();
            foreach (var entry in saved?.Models ?? [])
            {
                merged[entry.Key] = entry;
            }
            foreach (var entry in fitted)
            {
                merged[entry.Key] = entry;
            }
            after = [.. merged.Values];
            return new ExpectedSkyFile([.. after]);
        }, logger: null, cancellationToken).ConfigureAwait(false);
        return after;
    }
}

/// <summary>The file: a plain list, ending on a non-nullable value (<c>JsonStreamReadTrapTests</c>).</summary>
public sealed record ExpectedSkyFile(List<ExpectedSkyEntry> Models, int Version = 1);

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(ExpectedSkyFile))]
internal partial class ExpectedSkyJsonContext : JsonSerializerContext;
