using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Random-access source of planetary lucky-imaging frames. Unlike the deep-sky
/// <see cref="Calibration.IFrameSource"/> (a lazily-enumerated folder of FITS subs), planetary frames
/// are thousands of indexed frames inside a single SER capture -- or, later, a live camera push-stream.
/// The batch grader / aligner / integrator and the live windowed stacker all consume this one interface,
/// blind to whether the backing store is a file or a live camera (the "wire it so a camera plugs in"
/// seam: a future <c>LiveCameraFrameStream</c> implements the same contract with a growing
/// <see cref="FrameCount"/>).
/// <para>
/// <b>Threading contract:</b> all frame I/O must happen off the render / UI thread (the standing
/// previewer rule). <see cref="LoadAsync"/> may complete synchronously -- a memory-mapped SER decode is
/// CPU-bound, not truly async -- so callers must invoke it on a background pipeline worker, never inline
/// on the render thread.
/// </para>
/// </summary>
public interface IPlanetaryFrameStream : IDisposable
{
    /// <summary>Number of frames available, or <c>-1</c> for an unbounded live stream.</summary>
    int FrameCount { get; }

    /// <summary>Plane width in pixels (halved for <see cref="PlanetaryFrameLayout.SplitCfa"/>).</summary>
    int Width { get; }

    /// <summary>Plane height in pixels (halved for <see cref="PlanetaryFrameLayout.SplitCfa"/>).</summary>
    int Height { get; }

    /// <summary>How each loaded frame's channels are laid out -- fixed for the lifetime of the stream.</summary>
    PlanetaryFrameLayout Layout { get; }

    /// <summary>Whether per-frame capture timestamps are available (drives the live window and de-rotation).</summary>
    bool HasTimestamps { get; }

    /// <summary>The capture timestamp of frame <paramref name="index"/>, or <c>null</c> when untimed / out of range.</summary>
    DateTimeOffset? TimestampOf(int index);

    /// <summary>
    /// Loads frame <paramref name="index"/> as an <see cref="Image"/> in unit range [0,1], with channels
    /// laid out per <see cref="Layout"/>. The caller owns the returned image and should drop it (or
    /// <see cref="Image.Release"/> it) once done. Must be called off the render / UI thread.
    /// </summary>
    ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default);
}
