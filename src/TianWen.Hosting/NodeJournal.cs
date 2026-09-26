using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// The node's crash journal (docs/plans/hardware-in-the-server.md, "When the server dies", decision 8): a small file
/// beside the node's lock of what the node holds, the devices connected, the run going on and what each camera's
/// cooler is being asked to do, written whenever that changes (<see cref="NodeJournalService"/>). A node that stops
/// cleanly has released it all, so it leaves none; a node that starts and finds one knows the last one died, and what
/// it died holding.
/// </summary>
/// <remarks>
/// <see cref="WrittenUtc"/> is the machine's REAL clock, never the node's (which a simulated <c>TIANWEN_NOW</c> can
/// shift): it is compared with the machine's boot time, which is real.
/// </remarks>
internal sealed record NodeJournal
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>When the node last wrote it, on the machine's real clock.</summary>
    public DateTimeOffset WrittenUtc { get; init; }

    /// <summary>The node that wrote it, which its keeper names when it restarts it after a crash.</summary>
    public int ProcessId { get; init; }

    /// <summary>The devices connected through the node's hub, in URI order.</summary>
    public ImmutableArray<NodeJournalDevice> Devices { get; init; } = [];

    /// <summary>The run going on, or the one a node before it died in, until that report is dismissed.</summary>
    public NodeJournalRun? Run { get; init; }

    /// <summary>
    /// When nodes on this socket have crashed lately, on the real clock: the crash-loop guard. Each node that finds a
    /// journal adds the crash that left it, and carries those within <see cref="NodeKeeper.CrashLoopWindow"/> on.
    /// </summary>
    public ImmutableArray<DateTimeOffset> Crashes { get; init; } = [];

    /// <summary>
    /// The device the node is reconnecting from a journal it found, written BEFORE the connect: if a driver crashes the
    /// node there, this names it for the next one.
    /// </summary>
    public string? Touching { get; init; }

    /// <summary>Whether the node holds anything a successor would need to know about.</summary>
    public bool HoldsAnything => !Devices.IsDefaultOrEmpty || Run is not null || Touching is not null;

    /// <summary>The journal of the node at <paramref name="socketPath"/>: beside its socket and lock, one per node.</summary>
    public static string PathFor(string socketPath) => Path.ChangeExtension(socketPath, ".journal");

    /// <summary>Whether this holds what <paramref name="other"/> holds, whenever and by whom each was written.</summary>
    public bool HoldsTheSameAs(NodeJournal other) =>
        Run == other.Run && Touching == other.Touching
        && DevicesOrEmpty(this).SequenceEqual(DevicesOrEmpty(other))
        && CrashesOrEmpty(this).SequenceEqual(CrashesOrEmpty(other));

    /// <summary>
    /// The crashes that count towards a crash loop as a node finds this journal at <paramref name="foundUtc"/>: those
    /// within <see cref="NodeKeeper.CrashLoopWindow"/>, and the one that left this journal behind.
    /// </summary>
    public ImmutableArray<DateTimeOffset> CrashesWith(DateTimeOffset foundUtc) =>
        [.. CrashesOrEmpty(this).Where(crash => foundUtc - crash < NodeKeeper.CrashLoopWindow), foundUtc];

    /// <summary>What <paramref name="hub"/> and the node's run hold now.</summary>
    /// <param name="session">The run's session, for the target it is on.</param>
    public static NodeJournal Of(IDeviceHub hub, NodeRunRecord? run, ISession? session, DateTimeOffset writtenUtc, int processId)
    {
        var devices = new List<NodeJournalDevice>();
        foreach (var (uri, _) in hub.ConnectedDevices)
        {
            var displayName = hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : null;
            CoolerIntentKind? cooler = null;
            double? setpoint = null;
            if (hub.TryGetCoolerIntent(uri, out var intent))
            {
                cooler = intent.Kind;
                setpoint = double.IsFinite(intent.SetpointC) ? intent.SetpointC : null;
            }
            devices.Add(new NodeJournalDevice(uri.ToString(), displayName, cooler, setpoint));
        }
        devices.Sort(static (a, b) => string.CompareOrdinal(a.DeviceUri, b.DeviceUri));

        return new NodeJournal
        {
            WrittenUtc = writtenUtc,
            ProcessId = processId,
            Devices = [.. devices],
            Run = run is { } r ? new NodeJournalRun(r.Kind, r.ProfileId, r.StartedUtc, session?.ActiveObservation?.Target.Name) : null,
        };
    }

    /// <summary>
    /// What a node that found this journal as it started reports: which run was interrupted and when, and whether the
    /// journal can be believed at all.
    /// <list type="bullet">
    /// <item>Written by the node whose crash its keeper just saw (<paramref name="afterCrashOf"/>): seconds old, however
    /// long ago it was last written, since the keeper is the witness that no boot came between.</item>
    /// <item>Otherwise older than the machine's last boot (a power cut, a restart): STALE, a rig whose state nobody
    /// knows, shown for information and never acted on. So is a journal whose age against a boot the OS will not tell
    /// cannot be judged.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// A journal that can be believed is still not acted on when nodes have crashed twice within
    /// <see cref="NodeKeeper.CrashLoopWindow"/> (<see cref="NodeRecoveryDto.CrashLoop"/>): a driver that crashes the node
    /// would crash every node that reconnected it, so the loop ends here, naming the device the last one was reaching for.
    /// </remarks>
    public NodeRecoveryDto Recover(int? afterCrashOf, DateTimeOffset foundUtc, DateTimeOffset? lastBootUtc)
    {
        var afterCrash = afterCrashOf == ProcessId;
        var stale = !afterCrash && (lastBootUtc is not { } boot || WrittenUtc < boot);
        return new NodeRecoveryDto
        {
            JournalWrittenUtc = WrittenUtc,
            FoundUtc = foundUtc,
            AfterCrash = afterCrash,
            Stale = stale,
            CrashLoop = !stale && CrashesWith(foundUtc).Length >= 2,
            SuspectDevice = Touching,
            InterruptedRun = Run is { } run
                ? new NodeRunDto { Kind = run.Kind, ProfileId = run.ProfileId, StartedUtc = run.StartedUtc, Target = run.Target }
                : null,
            Devices = [.. DevicesOrEmpty(this).Select(static d => new NodeHeldDeviceDto
            {
                DeviceUri = d.DeviceUri,
                DisplayName = d.DisplayName,
                Cooler = d.Cooler,
                CoolerSetpointC = d.CoolerSetpointC,
            })],
        };
    }

    private static ImmutableArray<NodeJournalDevice> DevicesOrEmpty(NodeJournal journal) => journal.Devices.IsDefault ? [] : journal.Devices;

    private static ImmutableArray<DateTimeOffset> CrashesOrEmpty(NodeJournal journal) => journal.Crashes.IsDefault ? [] : journal.Crashes;
}

/// <summary>A device the node held, with the full URI it reconnects by, and for a camera its cooler intent.</summary>
internal sealed record NodeJournalDevice(string DeviceUri, string? DisplayName, CoolerIntentKind? Cooler, double? CoolerSetpointC);

/// <summary>The node's run: its kind, profile and start, and the target it was on when last written.</summary>
internal sealed record NodeJournalRun(NodeRunKind Kind, Guid ProfileId, DateTimeOffset StartedUtc, string? Target);

// Names, not numbers: a person reads this file after a crash, and nothing but the next node parses it.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(NodeJournal))]
internal partial class NodeJournalJsonContext : JsonSerializerContext;
