using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Devices;

/// <summary>
/// Where a mount's encoders last stood, as its driver last read them: steps from home on each axis, the counts per revolution
/// they are in, whether the mount was moving then, and when. A mount that loses power comes back reading home while it still
/// stands here (an unpowered Synta mount does not move by itself), so writing these steps back restores its position (#1954).
/// </summary>
/// <param name="RaSteps">Steps from home on the RA (primary) axis.</param>
/// <param name="DecSteps">Steps from home on the Dec (secondary) axis.</param>
/// <param name="CprRa">Counts per revolution of the RA axis the steps are in; a mount reporting another count is another mount.</param>
/// <param name="CprDec">Counts per revolution of the Dec axis.</param>
/// <param name="Moving">The mount was slewing or moving when this was read, so it went on past it if the power went then.</param>
/// <param name="AtUtc">When the encoders were read.</param>
public sealed record MountLastKnownPosition(int RaSteps, int DecSteps, uint CprRa, uint CprDec, bool Moving, DateTimeOffset AtUtc);

/// <summary>How an operator restores the position of a mount that lost it (<see cref="IMountDriver.PositionLoss"/>).</summary>
public enum MountPositionRestore
{
    /// <summary>Write the last known encoder steps back: right unless the clutches slipped or someone moved the mount.</summary>
    LastKnown = 0,

    /// <summary>The mount was put back at home by hand: the encoders reading home are right.</summary>
    AtHome = 1,
}

/// <summary>
/// A mount that lost its position, usually its power, and the position it last knew. While a driver reports one, it accepts
/// no goto, Home, park or move (stop and tracking off stay allowed) and its mechanical axis angle is not given out, until
/// the operator restores the position: <see cref="IMountDriver.RestorePositionAsync"/>, or a sync to a plate solve.
/// </summary>
public sealed record MountPositionLoss(MountLastKnownPosition LastKnown)
{
    /// <summary>
    /// What happened and what to do, in ONE wording for every host: a refusal, the session's prompt and the device panel.
    /// </summary>
    public string Describe(string mountName)
        => $"{mountName} lost power: it thinks it is at home, but it is still where it was. Restore its position before "
            + $"moving it: sync it from its last known position ({LastKnownInWords}), sync it to a plate solve, or say it "
            + "was put back at home by hand.";

    /// <summary>The refusal of a move while the position is lost, naming the move.</summary>
    public string Refuse(string mountName, string operation)
        => $"{operation} refused: {Describe(mountName)}";

    /// <summary>What a restore from the last known position assumes, for a prompt that asks for it.</summary>
    public string LastKnownCaveat
        => LastKnown.Moving
            ? "It was moving when that was read, so it may have gone on past it: check against a plate solve afterwards."
            : "This is wrong if the clutches slipped or someone moved the mount while it was off.";

    private string LastKnownInWords => $"read {LastKnown.AtUtc:yyyy-MM-dd HH:mm} UTC{(LastKnown.Moving ? ", while moving" : "")}";
}

[JsonSerializable(typeof(MountLastKnownPosition))]
internal partial class MountPositionJsonContext : JsonSerializerContext;

/// <summary>
/// Keeps each mount's <see cref="MountLastKnownPosition"/> on disk, one file per mount under <c>Mounts/</c> in the app data
/// folder, keyed by <see cref="DeviceBase.DeviceId"/>, so it outlives the driver, a node restart and a crash.
/// </summary>
public static class MountPositionMemory
{
    private const string SubDirectory = "Mounts";

    private static readonly MountPositionJsonContext IndentedContext = new(new JsonSerializerOptions { WriteIndented = true });

    private static string PathFor(IExternal external, string mountDeviceId)
        => Path.Combine(external.CreateSubDirectoryInAppDataFolder(SubDirectory).FullName, mountDeviceId + ".last-position.json");

    public static Task RememberAsync(IExternal external, string mountDeviceId, MountLastKnownPosition position, CancellationToken cancellationToken)
        => external.AtomicWriteJsonAsync(PathFor(external, mountDeviceId), position, IndentedContext.MountLastKnownPosition, cancellationToken);

    public static Task<MountLastKnownPosition?> TryRecallAsync(IExternal external, string mountDeviceId, ILogger? logger, CancellationToken cancellationToken)
        => external.TryReadJsonAsync(PathFor(external, mountDeviceId), IndentedContext.MountLastKnownPosition, logger, cancellationToken);
}
