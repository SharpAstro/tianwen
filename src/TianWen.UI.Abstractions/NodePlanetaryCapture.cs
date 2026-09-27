using System;
using System.Collections.Immutable;
using System.Threading;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// This computer's planetary capture as the planetary panel drives it, the capture itself being its node's run (P6 of
/// docs/plans/hardware-in-the-server.md, #936; the node's <c>NodePlanetary</c> runs <see cref="PlanetaryCapture"/>, its
/// stack, its recenter and its recording). The panel's live controls are staged here and sent to the node by the run's loop
/// only as they CHANGE (<see cref="TakeChanges"/>): the panel pushes its recenter settings every frame, which over HTTP
/// would be a request a frame. The telemetry the panel shows is the node's, as last read (<see cref="Update"/>).
/// </summary>
public sealed class NodePlanetaryCapture
{
    private sealed record Pending(double? ExposureMs, short? Gain, int? RoiWidth, int? RoiHeight, int JogX, int JogY, PlanetaryRecenterDto? Recenter)
    {
        public static readonly Pending None = new Pending(null, null, null, null, 0, 0, null);

        public bool IsEmpty => this == None;
    }

    private Pending _pending = Pending.None;
    private PlanetaryRecenterDto? _recenter;
    private PlanetaryStateDto? _state;
    private int _running;

    /// <summary>Whether this computer's node runs the capture this view started: from its start until the node says it ended.</summary>
    public bool IsCapturing => Volatile.Read(ref _running) == 1;

    /// <summary>The readout window as the node snapped it to the camera's ROI rule, or zero before the node's first answer.</summary>
    public (int Width, int Height) Roi => Volatile.Read(ref _state) is { } state ? (state.RoiWidth, state.RoiHeight) : (0, 0);

    public int FramesReceived => Volatile.Read(ref _state)?.FramesReceived ?? 0;

    public int DroppedFrames => Volatile.Read(ref _state)?.DroppedFrames ?? 0;

    public double MeasuredFps => Volatile.Read(ref _state)?.FramesPerSecond ?? 0;

    /// <summary>The recenter's last measured offset of the disk from the ROI's centre, in pixels.</summary>
    public (double X, double Y) LastComOffset => Volatile.Read(ref _state) is { } state ? (state.OffsetX ?? 0, state.OffsetY ?? 0) : (0, 0);

    public RecenterActuator LastRecenterActuator => Volatile.Read(ref _state)?.RecenterActuator ?? default;

    /// <summary>Why the node's capture failed, in words; null while it goes well.</summary>
    public string? FailureReason => Volatile.Read(ref _state)?.FailureReason;

    public void SetExposure(TimeSpan exposure) => Stage(p => p with { ExposureMs = exposure.TotalMilliseconds });

    public void SetGain(int gain) => Stage(p => p with { Gain = (short)Math.Clamp(gain, 0, short.MaxValue) });

    public void SetRoiSize(int width, int height) => Stage(p => p with { RoiWidth = width, RoiHeight = height });

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
        };
    }

    /// <summary>The run began: controls staged from here are for it, and the start carried the rest.</summary>
    internal void Began()
    {
        Interlocked.Exchange(ref _pending, Pending.None);
        Volatile.Write(ref _state, null);
        Volatile.Write(ref _running, 1);
    }

    /// <summary>The node's state of the run, as last read.</summary>
    internal void Update(PlanetaryStateDto state) => Volatile.Write(ref _state, state);

    /// <summary>The run has ended on the node, or this view stopped watching it.</summary>
    internal void Ended() => Volatile.Write(ref _running, 0);
}
