using System;
using System.Collections.Immutable;
using System.Threading;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// This computer's planetary capture as the planetary panel drives it, the capture itself being its node's run (P6 of
/// docs/plans/hardware-in-the-server.md, #936; the node's <c>NodePlanetary</c> runs <see cref="PlanetaryCapture"/>, its
/// stack, its recenter and its recording). The panel's live controls are staged here and sent to the node by the run's loop
/// only as they CHANGE (<see cref="TakeChanges"/>): the panel pushes its recenter settings every frame, which over HTTP
/// would be a request a frame. The telemetry the panel shows is the node's, as last read: a poll's
/// (<see cref="UpdateFromPoll"/>), or the node's answer to a command it took (<see cref="UpdateFromAnswer"/>), which a poll
/// sent before it cannot undo.
/// </summary>
public sealed class NodePlanetaryCapture
{
    private sealed record Pending(double? ExposureMs, short? Gain, int? RoiWidth, int? RoiHeight, int JogX, int JogY, PlanetaryRecenterDto? Recenter,
        BitDepth? BitDepth, bool? HighSpeed)
    {
        public static readonly Pending None = new Pending(null, null, null, null, 0, 0, null, null, null);

        public bool IsEmpty => this == None;
    }

    private Pending _pending = Pending.None;
    private PlanetaryRecenterDto? _recenter;
    private Shown _shown = Shown.None;
    private int _running;

    // The node's state as last shown, the run it belongs to, and how many answers to commands have been shown in it. A poll's
    // state is shown only within the run it was sent in, and only if no command's answer was shown after it was sent, so a poll
    // sent before a record and answered after it cannot undo the record's answer (review round 1 of #1610, N1); but a poll that
    // says the run ENDED is shown whatever answer came meanwhile, since the node refuses a record or a stop once its run has
    // ended, so no answer can be newer than the end (round 2, B1). Keyed to its run, so a late end of the previous run never
    // ends a newer run's panel, whatever the controller's order. Replaced whole, by compare-and-swap: a poll's answer and a
    // command's arrive on two threads.
    private sealed record Shown(PlanetaryStateDto? State, long Answers, long Run)
    {
        public static readonly Shown None = new Shown(null, 0, 0);
    }

    private PlanetaryStateDto? State => Volatile.Read(ref _shown).State;

    /// <summary>Whether this computer's node runs the capture this view started: from its start until the node says it ended.</summary>
    public bool IsCapturing => Volatile.Read(ref _running) == 1;

    /// <summary>The readout window as the node snapped it to the camera's ROI rule, or zero before the node's first answer.</summary>
    public (int Width, int Height) Roi => State is { } state ? (state.RoiWidth, state.RoiHeight) : (0, 0);

    public int FramesReceived => State?.FramesReceived ?? 0;

    public int DroppedFrames => State?.DroppedFrames ?? 0;

    public double MeasuredFps => State?.FramesPerSecond ?? 0;

    /// <summary>The CURRENT rate (#1619), which the readouts show; 0 before the node can say.</summary>
    public double CurrentFps => State?.CurrentFramesPerSecond ?? 0;

    /// <summary>The recenter's last measured offset of the disk from the ROI's centre, in pixels.</summary>
    public (double X, double Y) LastComOffset => State is { } state ? (state.OffsetX ?? 0, state.OffsetY ?? 0) : (0, 0);

    public RecenterActuator LastRecenterActuator => State?.RecenterActuator ?? default;

    /// <summary>Why the node's capture failed, in words; null while it goes well.</summary>
    public string? FailureReason => State?.FailureReason;

    /// <summary>The node's recording of the capture going on, or the last one to end, as last read; null before any (#1344).</summary>
    public PlanetaryRecordingDto? Recording => State?.Recording;

    public void SetExposure(TimeSpan exposure) => Stage(p => p with { ExposureMs = exposure.TotalMilliseconds });

    public void SetGain(int gain) => Stage(p => p with { Gain = (short)Math.Clamp(gain, 0, short.MaxValue) });

    public void SetRoiSize(int width, int height) => Stage(p => p with { RoiWidth = width, RoiHeight = height });

    /// <summary>A new depth for the stream going on (<see cref="PlanetaryControlsDto.BitDepth"/>).</summary>
    public void SetBitDepth(BitDepth bitDepth) => Stage(p => p with { BitDepth = bitDepth });

    /// <summary>Switches the stream's high-speed readout (<see cref="PlanetaryControlsDto.HighSpeed"/>).</summary>
    public void SetHighSpeed(bool on) => Stage(p => p with { HighSpeed = on });

    /// <summary>The depth the newest frame came in, as the node reports it; null before the first.</summary>
    public BitDepth? FrameBitDepth => State?.BitDepth;

    /// <summary>Pans the ROI by a step; steps taken between two sends add up.</summary>
    public void JogRoi(int dxPixels, int dyPixels) => Stage(p => p with { JogX = p.JogX + dxPixels, JogY = p.JogY + dyPixels });

    /// <summary>
    /// The recenter the panel shows. Called every frame: staged only when it differs from what the node was last given,
    /// so an unchanged panel sends nothing.
    /// </summary>
    public void ConfigureRecenter(bool auto, bool mountJog, int deadbandPixels, double gain, bool flipRa = false, bool flipDec = false)
    {
        var recenter = new PlanetaryRecenterDto
        {
            Auto = auto,
            MountJog = mountJog,
            DeadbandPixels = deadbandPixels,
            Gain = gain,
            FlipRa = flipRa,
            FlipDec = flipDec,
        };
        if (Volatile.Read(ref _recenter) is { } known && Same(known, recenter))
        {
            return;
        }
        Volatile.Write(ref _recenter, recenter);
        Stage(p => p with { Recenter = recenter });
    }

    /// <summary>The recenter the panel shows now, for a start to carry, so the first frames are recentred as the panel says.</summary>
    public PlanetaryRecenterDto? RecenterForStart => Volatile.Read(ref _recenter);

    private static bool Same(PlanetaryRecenterDto a, PlanetaryRecenterDto b) =>
        a.Auto == b.Auto && a.MountJog == b.MountJog && a.DeadbandPixels == b.DeadbandPixels && a.Gain == b.Gain
        && a.FlipRa == b.FlipRa && a.FlipDec == b.FlipDec;

    private void Stage(Func<Pending, Pending> change) => ImmutableInterlocked.Update(ref _pending, change);

    /// <summary>What has changed since the last send, taken, or null when nothing has: for the run's loop to send.</summary>
    internal PlanetaryControlsDto? TakeChanges()
    {
        var pending = Interlocked.Exchange(ref _pending, Pending.None);
        if (pending.IsEmpty)
        {
            return null;
        }
        return new PlanetaryControlsDto
        {
            ExposureMs = pending.ExposureMs,
            Gain = pending.Gain,
            RoiWidth = pending.RoiWidth,
            RoiHeight = pending.RoiHeight,
            JogX = pending.JogX == 0 ? null : pending.JogX,
            JogY = pending.JogY == 0 ? null : pending.JogY,
            Recenter = pending.Recenter,
            BitDepth = pending.BitDepth,
            HighSpeed = pending.HighSpeed,
        };
    }

    /// <summary>The run began: controls staged from here are for it, and the start carried the rest.</summary>
    internal void Began()
    {
        Interlocked.Exchange(ref _pending, Pending.None);
        ImmutableInterlocked.Update(ref _shown, static shown => new Shown(null, shown.Answers, shown.Run + 1));
        Volatile.Write(ref _running, 1);
    }

    /// <summary>The stamp a poll takes before it is sent, for <see cref="UpdateFromPoll"/>: the run it is of, and the answers shown so far.</summary>
    internal (long Run, long Answers) PollStamp
    {
        get
        {
            var shown = Volatile.Read(ref _shown);
            return (shown.Run, shown.Answers);
        }
    }

    /// <summary>
    /// The node's state of the run as a poll sent at <paramref name="stamp"/> read it: shown if it is of the run on show and
    /// either says the run ended or was sent after every command's answer shown so far, which would be newer than it.
    /// </summary>
    internal void UpdateFromPoll(PlanetaryStateDto state, (long Run, long Answers) stamp)
    {
        var shown = Volatile.Read(ref _shown);
        while (shown.Run == stamp.Run && (shown.Answers == stamp.Answers || !state.Running))
        {
            var seen = Interlocked.CompareExchange(ref _shown, shown with { State = state }, shown);
            if (ReferenceEquals(seen, shown))
            {
                return;
            }
            shown = seen;
        }
    }

    /// <summary>
    /// The node's answer to a command it took (a start, a record, its stop): its newest state, shown at once; but never over the
    /// run's end once shown, which no answer that says the run goes on can be newer than.
    /// </summary>
    internal void UpdateFromAnswer(PlanetaryStateDto state)
        => ImmutableInterlocked.Update(ref _shown, shown => shown.State is { Running: false } && state.Running
            ? shown
            : new Shown(state, shown.Answers + 1, shown.Run));

    /// <summary>The run has ended on the node, or this view stopped watching it.</summary>
    internal void Ended() => Volatile.Write(ref _running, 0);
}
