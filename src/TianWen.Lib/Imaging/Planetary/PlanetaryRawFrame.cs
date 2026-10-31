using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// One frame of a capture put on a stack's grid: the frame demosaiced as a master is, and where the stack's own registration
/// moves it (A | B's Raw | Stack, #1883).
/// </summary>
/// <param name="Frame">The frame in unit range, full resolution (a split-CFA frame demosaiced), a fresh image the caller owns.</param>
/// <param name="Dx">The frame sampled at <c>(x + Dx, y + Dy)</c> lands on the stack's pixel <c>(x, y)</c>
/// (<see cref="GlobalAligner.Estimate"/>), so the frame is DRAWN <c>(-Dx, -Dy)</c> from the stack's origin.</param>
public sealed record PlanetaryPlacedFrame(Image Frame, double Dx, double Dy);

/// <summary>
/// Puts a capture's frame where the stack's disk is, by the registration the stack itself uses for a frame (<see cref="GlobalAligner"/>
/// against the stack's own master), never by a centre of mass or an edge of the frame alone.
/// </summary>
public static class PlanetaryRawFrame
{
    /// <summary>
    /// Loads frame <paramref name="index"/> of <paramref name="stream"/>, demosaics it as the stack's master is
    /// (<see cref="PlanetaryMaster.MergeAndDemosaicAsync"/>), and registers it onto <paramref name="stackMaster"/> as
    /// <see cref="RollingWindowStacker"/> registers a frame onto its reference. Null when the stream no longer holds the frame, or the
    /// frame's size is not the master's (a master of another geometry has no common grid with it). The master is only read.
    /// </summary>
    public static async Task<PlanetaryPlacedFrame?> LoadPlacedAsync(IPlanetaryFrameStream stream, int index, Image stackMaster,
        RollingWindowOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(stackMaster);

        if (await stream.TryLoadAsync(index, cancellationToken).ConfigureAwait(false) is not { } loaded)
        {
            return null;
        }

        Image frame;
        try
        {
            frame = await PlanetaryMaster.MergeAndDemosaicAsync(loaded, stream.Layout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A demosaic reads the mosaic and builds a fresh image; a stream whose frame needed none handed it through, and then it is the frame.
            if (stream.Layout == PlanetaryFrameLayout.SplitCfa && loaded.ChannelCount == 4)
            {
                loaded.Release();
            }
        }

        if (frame.Width != stackMaster.Width || frame.Height != stackMaster.Height)
        {
            frame.Release();
            return null;
        }

        var shift = RegisterOnto(frame, stackMaster, options);
        return new PlanetaryPlacedFrame(frame, shift.Dx, shift.Dy);
    }

    /// <summary>
    /// Where <paramref name="image"/> lands on <paramref name="stackMaster"/>'s grid, by the registration the stack uses for a frame
    /// (<see cref="GlobalAligner"/> against the stack's own master). Both are only read, and they need not be the same size: the disk
    /// is what is registered, so a master cropped to where its frames reached (a Full stack) places onto a Live one. <c>(Dx, Dy)</c>
    /// as for <see cref="PlanetaryPlacedFrame"/>: the image is DRAWN <c>(-Dx, -Dy)</c> from the master's origin.
    /// </summary>
    public static PhaseCorrelation.Shift RegisterOnto(Image image, Image stackMaster, RollingWindowOptions? options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stackMaster);

        var region = PlanetaryDisk.BoundingBox(stackMaster);
        var tileSize = options is { AlignTileSize: > 0 }
            ? NextPowerOfTwo(options.AlignTileSize)
            : Math.Clamp(NextPowerOfTwo(Math.Max(region.Width, region.Height)), 64, 512);
        var aligner = GlobalAligner.FromReference(stackMaster, region, tileSize, options?.WhitenedCorrelation ?? false);
        return aligner.Estimate(image, PlanetaryDisk.BoundingBox(image));
    }

    private static int NextPowerOfTwo(int value)
    {
        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }
        return p;
    }
}
