using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;

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
    /// Returns the JSON file path for a given focuser's backlash history, keyed by <see cref="DeviceBase.DeviceId"/>. That
    /// ID does NOT survive a COM-port change for a focuser with no hardware identity (<c>GeminiFocuser_COM3</c>,
    /// <c>QFOC_COM4</c>), so a profile that follows one to its new port carries the history with it
    /// (<see cref="FollowMovedFocusersAsync"/>, #1744).
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
        try
        {
            await using var stream = await SharedFile.TryOpenReadAsync(path, cancellationToken);
            return stream is null ? null : await JsonSerializer.DeserializeAsync(stream, IndentedContext.BacklashEstimateRecord, cancellationToken);
        }
        catch (JsonException)
        {
            // Corrupt sidecar: treat as no prior history. The next AutoFocus will start fresh.
            return null;
        }
    }

    /// <summary>
    /// Carries each focuser's history from its old device ID to its new one when a reconcile followed it to another port
    /// (<see cref="DeviceDiscoveryExtensions.FollowMovedPort"/>, #1744): the slots of <paramref name="before"/> and
    /// <paramref name="after"/> are paired in order, and a focuser slot whose key changed moves its file, unless the new ID
    /// already has one, which is kept. Answers how many it moved.
    /// </summary>
    public static async Task<int> FollowMovedFocusersAsync(IExternal external, ProfileData before, ProfileData after, CancellationToken cancellationToken)
    {
        var moved = 0;
        foreach (var (from, to) in before.DeviceUris().Zip(after.DeviceUris()))
        {
            if (DeviceTypeHelper.TryParseDeviceType(to.Scheme) is not DeviceType.Focuser
                || DeviceUriExtensions.DeviceKeyComparer.Equals(from.DeviceKey, to.DeviceKey))
            {
                continue;
            }

            var (fromId, toId) = (DeviceBase.DeviceIdOf(from), DeviceBase.DeviceIdOf(to));
            if (await TryLoadAsync(external, toId, cancellationToken) is not null
                || await TryLoadAsync(external, fromId, cancellationToken) is not { } record)
            {
                continue;
            }

            await SaveAsync(external, toId, record, cancellationToken);
            File.Delete(PathFor(external.ProfileFolder, fromId));
            moved++;
        }

        return moved;
    }
}
