using System;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The minimal surface the image renderer needs to <i>preview</i> a source: image geometry, the current
/// frame's per-channel pixel data, cached stretch statistics, and frame navigation. A still image
/// (FITS/TIFF -> <see cref="AstroImageDocument"/>) is a one-frame source; a SER planetary video is an
/// N-frame source (<c>SerPreviewSource</c>).
/// <para>
/// Decoupling the renderer from <see cref="AstroImageDocument"/> keeps per-frame playback off the heavy
/// document/stats path: stretch statistics are computed once and reused, and per frame only the channel
/// data is refreshed + re-uploaded. Still-only features (plate solve, star detection, colour calibration,
/// WCS overlays) stay on <see cref="AstroImageDocument"/>; the renderer reaches them by testing
/// <c>source is AstroImageDocument</c> (null for a SER), so they are simply inactive for a video. Kept
/// deliberately seek-agnostic so a future live-camera stream can implement it too.
/// </para>
/// </summary>
public interface IPreviewSource
{
    /// <summary>Frame width in pixels.</summary>
    int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    int Height { get; }

    /// <summary>Pixel-plane count of the current frame (1 = mono / raw mosaic, 3 = colour).</summary>
    int ChannelCount { get; }

    /// <summary>Sensor type of the current frame (drives the raw-Bayer GPU debayer path).</summary>
    SensorType SensorType { get; }

    /// <summary>Bayer CFA X offset (only meaningful for <see cref="SensorType.RGGB"/>).</summary>
    int BayerOffsetX { get; }

    /// <summary>Bayer CFA Y offset (only meaningful for <see cref="SensorType.RGGB"/>).</summary>
    int BayerOffsetY { get; }

    /// <summary>Flat row-major [0,1] pixel data for one channel of the current frame (height * width floats).</summary>
    ReadOnlySpan<float> GetChannelData(int channel);

    /// <summary>Per-channel histograms for the representative frame, driving the histogram display.</summary>
    ImageHistogram[] ChannelStatistics { get; }

    /// <summary>Per-channel background level fed into the post-stretch background math.</summary>
    float[] PerChannelBackground { get; }

    /// <summary>Luminance background level.</summary>
    float LumaBackground { get; }

    /// <summary>Computes display stretch uniforms from the (cached) statistics. Cheap to call per frame.
    /// <paramref name="manualWhiteBalance"/> is the user's WB-slider triple, composed with any auto color
    /// calibration; (1,1,1) or null leaves the existing (auto-only) behaviour bit-identical.</summary>
    StretchUniforms ComputeStretchUniforms(
        StretchMode mode,
        StretchParameters parameters,
        LumaWeighting weighting = LumaWeighting.Rec709,
        float lumaBlend = 1f,
        bool normalize = false,
        int curvesMode = 0,
        ReadOnlySpan<float> curveLut = default,
        float curvesBoost = 0f,
        float curvesMidpoint = 0.25f,
        float hdrAmount = 0f,
        float hdrKnee = 0.8f,
        float bgNeutralizationStrength = 1f,
        (float R, float G, float B)? manualWhiteBalance = null);

    /// <summary>Number of frames (1 for a still image).</summary>
    int FrameCount { get; }

    /// <summary>Index of the currently selected frame (0 for a still image).</summary>
    int FrameIndex { get; }

    /// <summary>
    /// Selects frame <paramref name="index"/>, refreshing <see cref="GetChannelData"/>. Returns true if the
    /// displayed frame changed. No-op (returns false) for a still image.
    /// </summary>
    bool SelectFrame(int index);

    /// <summary>True when the source carries per-frame timestamps.</summary>
    bool HasTimestamps { get; }

    /// <summary>UTC timestamp of frame <paramref name="index"/>; <see cref="DateTimeOffset.MinValue"/> when unavailable.</summary>
    DateTimeOffset TimestampOf(int index);
}
