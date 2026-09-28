using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

/// <summary>
/// Options for a live video / streaming capture (planetary lucky-imaging EAA), and, handed to
/// <see cref="IVideoCameraDriver.ApplyVideoControlsAsync"/>, the changes to a running one: there every field left at its
/// default is left as it is. Kept deliberately small; the USB bandwidth a stream asks for is the driver's (a DAL camera
/// takes its maximum while it streams).
/// </summary>
/// <param name="Exposure">Per-frame exposure. Planetary capture runs very short (ms) exposures at high fps; zero leaves a
/// running stream's as it is.</param>
/// <param name="Gain">Gain to apply for the stream, or <c>null</c> to leave the current gain unchanged.</param>
/// <param name="HighSpeedMode">
/// The sensor's high-speed readout (ZWO <c>ASI_HIGH_SPEED_MODE</c>) where the camera has one
/// (<see cref="ICameraDriver.CanFastReadout"/>): <c>null</c> starts a stream with it ON and leaves a running one's as it
/// is. It doubles an ASI462MC's full-frame rate in 8 bits and changes nothing in 16 (measured 2026-09-28).
/// </param>
/// <param name="BitDepth">
/// The depth the stream reads out in, one of <see cref="IVideoCameraDriver.VideoBitDepths"/>: <c>null</c> starts a stream
/// in the camera's own depth (what its single exposures use) and leaves a running one's as it is. The stream's depth is
/// its own, never the camera's setting, so a planetary capture in 8 bits leaves the next deep-sky exposure in 16.
/// </param>
public sealed record VideoCaptureOptions(TimeSpan Exposure, short? Gain = null, bool? HighSpeedMode = null, BitDepth? BitDepth = null);

/// <summary>
/// A camera that can stream frames continuously in <b>video mode</b>, for live planetary lucky-imaging.
/// This is the single, vendor-neutral capture contract. Canon implements it over FC.SDK Live View, the shared
/// <c>DALCameraDriver</c> for a body whose SDK streams (ZWO first, #813, docs/plans/planetary-native-video.md Phase D),
/// and the fake camera for tests; <c>PlanetaryCapture</c> streams a camera without it through its own short-exposure
/// loop.
/// The planetary live-stack pipeline (<c>LiveCameraFrameStream</c> -> <c>RollingWindowStacker</c> ->
/// the preview) consumes only this interface, blind to vendor.
/// <para>
/// <b>One stream method.</b> Start / yield / stop are folded into <see cref="CaptureVideoAsync"/>: it
/// starts capture, yields each frame as it arrives, and stops when the enumerator is disposed or
/// <paramref name="cancellationToken"/> is cancelled. There is no separate Start/Stop to leave the
/// camera in an illegal "started but not draining" state. Capability is gated by
/// <see cref="CanVideoCapture"/>.
/// </para>
/// <para>
/// <b>Frame ownership.</b> Each yielded <see cref="Image"/> is only valid until the next
/// <c>MoveNextAsync</c> -- a driver may recycle its buffer for the next frame. The consumer must copy
/// (or otherwise finish with) the frame before requesting the next one, and should <see cref="Image.Release"/>
/// it when done. <c>LiveCameraFrameStream</c> copies each frame into its own ring on push for exactly
/// this reason.
/// </para>
/// </summary>
public interface IVideoCameraDriver : ICameraDriver
{
    /// <summary>True if the camera can stream in video mode (else only single-shot exposures are available).</summary>
    bool CanVideoCapture { get; }

    /// <summary>
    /// True if the readout window (ROI) can be panned mid-stream without restarting capture (hardware
    /// "ROI jog"). False for cameras whose live view is a fixed frame (e.g. Canon EVF) -- the recenter
    /// loop then falls back to mount jog. Meaningful only while a video capture is running.
    /// </summary>
    bool CanJogRoi { get; }

    /// <summary>
    /// Number of frames the camera/SDK reported as dropped since the current capture started (buffer
    /// starvation / USB bandwidth). 0 when not streaming or when the driver can't report it.
    /// </summary>
    int DroppedFrames { get; }

    /// <summary>
    /// The depths this camera can stream in (<see cref="VideoCaptureOptions.BitDepth"/>), shallowest first; empty when a
    /// stream's depth is the camera's own and cannot be chosen (a Canon's live view).
    /// </summary>
    ImmutableArray<BitDepth> VideoBitDepths => [];

    /// <summary>
    /// The current readout window (origin + size, unbinned sensor px) of the running stream -- the live
    /// position <see cref="JogRoiAsync"/> pans. The recenter loop reads this to know how much pan range is
    /// left before an edge (when the window is at the sensor edge it hands off to the mount). A snapshot;
    /// the size matches the yielded frame, the origin reflects the jogs applied so far. Defaults to a
    /// sensor-sized window at the origin when not streaming.
    /// </summary>
    RoiRect VideoRoi { get; }

    /// <summary>
    /// Streams frames in video mode until the enumerator is disposed or <paramref name="cancellationToken"/>
    /// is cancelled. Only call when <see cref="CanVideoCapture"/> is <see langword="true"/>. Run the
    /// enumeration off the render / UI thread. See the type remarks for frame-ownership rules.
    /// </summary>
    IAsyncEnumerable<Image> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pans the readout window by <paramref name="dxPixels"/> / <paramref name="dyPixels"/> (unbinned
    /// sensor pixels) while a video capture is running, keeping the frame size unchanged -- the fast,
    /// mount-free recenter actuator. Only call when <see cref="CanJogRoi"/> is <see langword="true"/>.
    /// Implementations clamp to the sensor and snap the start position to the alignment the SDK requires.
    /// </summary>
    ValueTask JogRoiAsync(int dxPixels, int dyPixels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies new per-frame controls to a <b>running</b> stream: the live-tuning path for the planetary
    /// tab's exposure / gain steppers and its depth and high-speed switches (a real planetary capture lets you
    /// tweak these on the fly). Takes effect from the next frame; a field left at its default is left as it is.
    /// A new depth or readout mode may restart the stream inside the driver, as a new window size does. The readout-window <b>size</b> is changed through the standard
    /// <see cref="ICameraDriver.NumX"/> / <see cref="ICameraDriver.NumY"/> setters, which a streaming driver
    /// re-reads per frame (the consumer resizes its frame stream when the yielded frame dimensions change);
    /// the window <b>position</b> through <see cref="JogRoiAsync"/>. No-op when not streaming.
    /// </summary>
    ValueTask ApplyVideoControlsAsync(VideoCaptureOptions controls, CancellationToken cancellationToken = default);
}
