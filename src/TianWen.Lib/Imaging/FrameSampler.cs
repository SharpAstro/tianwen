using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A display-rate sample of a video-rate stream of BORROWED frames: at most one copy per <paramref name="interval"/>, into
/// planes it recycles. What a host that shows a live stream somewhere else keeps of it (the node's planetary live frame,
/// P5 part 5 of docs/plans/hardware-in-the-server.md): a copy of every frame would copy all but one of them for nobody,
/// and a new plane per copy is the garbage the live ring was rid of.
/// </summary>
/// <remarks>
/// A sample is a RECYCLED frame (ownership convention 1 of docs/plans/frame-lifecycle.md): whoever it is handed to owns it
/// and releases it, and its planes come back here for a later sample once the last lease on it is released. One thread
/// samples at a time (the capture loop).
/// </remarks>
/// <param name="owner">Who the samples are of, for the DEBUG leak tracker.</param>
public sealed class FrameSampler(ITimeProvider timeProvider, TimeSpan interval, string owner)
{
    private readonly PlaneRecycler _planes = new PlaneRecycler(owner);
    private long _lastSample;
    private bool _sampled;

    /// <summary>How many planes it has had to allocate, for the test that holds a steady stream to none.</summary>
    internal int PlanesAllocated => _planes.PlanesAllocated;

    /// <summary>
    /// A copy of <paramref name="frame"/> the caller owns, when <c>interval</c> has passed since the last one (the first
    /// frame always); false, copying nothing, otherwise. <paramref name="frame"/> is only read.
    /// </summary>
    /// <param name="arrived">When the frame arrived, which the copy carries as its start time when the frame has none: no
    /// driver stamps a VIDEO frame, so a live view could not tell a fresh frame from a stale one, and a snapshot of one
    /// would be dated the year 1.</param>
    public bool TrySample(Image frame, DateTimeOffset arrived, [NotNullWhen(true)] out Image? sample)
    {
        var now = timeProvider.GetTimestamp();
        if (_sampled && timeProvider.GetElapsedTime(_lastSample, now) < interval)
        {
            sample = null;
            return false;
        }
        _sampled = true;
        _lastSample = now;

        var channels = ImmutableArray.CreateBuilder<Channel>(frame.ChannelCount);
        for (var c = 0; c < frame.ChannelCount; c++)
        {
            var channel = frame.GetChannel(c);
            // Every sample of the recycled plane is written, so nothing of the frame it last held survives.
            var plane = _planes.Take(frame.Height, frame.Width);
            Array.Copy(channel.Data, plane, plane.Length);
            channels.Add(_planes.Wrap(plane, channel.MinValue, channel.MaxValue, channel.Index, channel.Filter));
        }
        var meta = frame.ImageMeta.ExposureStartTime == default ? frame.ImageMeta with { ExposureStartTime = arrived } : frame.ImageMeta;
        sample = new Image(channels.MoveToImmutable(), frame.BitDepth, frame.Pedestal, meta, frame.SamplesAreUnitReferred);
        return true;
    }
}
