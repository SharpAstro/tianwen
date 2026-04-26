using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.Focus;

/// <summary>
/// EWMA backlash estimate plus the bookkeeping used to age it: sample count for
/// over-confidence gating, last-updated timestamp for staleness checks.
/// </summary>
public sealed record BacklashEstimateRecord(
    int EwmaIn,
    int EwmaOut,
    int Samples,
    DateTimeOffset LastUpdatedUtc);

[JsonSerializable(typeof(BacklashEstimateRecord))]
internal partial class BacklashHistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Sidecar persistence for per-focuser backlash EWMA. The actual backlash values
/// also get mirrored to the focuser's URI on profile save (so drivers can seed
/// <see cref="IFocuserDriver.BacklashStepsIn"/> / Out from the URI on connect),
/// but the EWMA's bookkeeping (sample count, timestamp) lives here so the URI
/// stays a clean values-only seed and so a profile copy/edit doesn't accidentally
/// inflate the sample count.
/// </summary>
public static class BacklashHistoryPersistence
{
    private const string SubDirectory = "BacklashHistory";

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };
    private static readonly BacklashHistoryJsonContext IndentedContext = new(IndentedOptions);

    /// <summary>
    /// Returns the JSON file path for a given focuser's backlash history.
    /// Keyed by <see cref="DeviceBase.DeviceId"/> which is stable across COM-port renames
    /// (the transport bits live in the URI query string, not the device id).
    /// </summary>
    private static string PathFor(DirectoryInfo profileFolder, string focuserDeviceId)
    {
        var dir = profileFolder.CreateSubdirectory(SubDirectory);
        return Path.Combine(dir.FullName, focuserDeviceId + ".json");
    }

    public static Task SaveAsync(
        IExternal external,
        string focuserDeviceId,
        BacklashEstimateRecord record,
        CancellationToken cancellationToken)
        => external.AtomicWriteJsonAsync(
            PathFor(external.ProfileFolder, focuserDeviceId),
            record,
            IndentedContext.BacklashEstimateRecord,
            cancellationToken);

    public static async Task<BacklashEstimateRecord?> TryLoadAsync(
        IExternal external,
        string focuserDeviceId,
        CancellationToken cancellationToken)
    {
        var path = PathFor(external.ProfileFolder, focuserDeviceId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, IndentedContext.BacklashEstimateRecord, cancellationToken);
        }
        catch (JsonException)
        {
            // Corrupt sidecar — treat as no prior history. The next AutoFocus will start fresh.
            return null;
        }
    }
}
