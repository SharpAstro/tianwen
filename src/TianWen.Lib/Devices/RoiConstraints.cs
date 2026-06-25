using System;

namespace TianWen.Lib.Devices;

/// <summary>
/// A region-of-interest (sub-frame) rectangle in the camera's <see cref="ICameraDriver.NumX"/> /
/// <see cref="ICameraDriver.StartX"/> coordinate space (binned sensor pixels): top-left
/// (<paramref name="X"/>, <paramref name="Y"/>) plus (<paramref name="Width"/>, <paramref name="Height"/>).
/// </summary>
public readonly record struct RoiRect(int X, int Y, int Width, int Height)
{
    /// <summary>Exclusive right edge (<see cref="X"/> + <see cref="Width"/>).</summary>
    public int Right => X + Width;

    /// <summary>Exclusive bottom edge (<see cref="Y"/> + <see cref="Height"/>).</summary>
    public int Bottom => Y + Height;

    /// <summary>A <paramref name="width"/> x <paramref name="height"/> window centred on a <paramref name="sensorWidth"/> x <paramref name="sensorHeight"/> sensor.</summary>
    public static RoiRect Centered(int sensorWidth, int sensorHeight, int width, int height)
        => new((sensorWidth - width) / 2, (sensorHeight - height) / 2, width, height);
}

/// <summary>
/// Hardware constraints on a camera's region-of-interest (sub-frame) selection, in the camera's
/// <see cref="ICameraDriver.NumX"/> coordinate space (binned sensor pixels at the current binning). The
/// ROI is a <b>free rectangle within these bounds</b>, NOT a fixed preset list: a width must be a multiple
/// of <see cref="WidthStep"/>, a height a multiple of <see cref="HeightStep"/>, and the top-left a multiple
/// of <see cref="OriginStepX"/> / <see cref="OriginStepY"/>, with the window kept fully on the sensor.
/// <para>
/// ZWO reports 8 / 2 (width % 8 == 0, height % 2 == 0; see <c>ASICamera2.h</c>); ASCOM / Alpaca / the
/// universal fallback report step 1 (any rect). The planetary ROI picker snaps the user's chosen rect to
/// these via <see cref="Snap(RoiRect)"/> -- so the UI never has to know the per-vendor rule, and the
/// presets it offers are just snapped-to-constraint convenience shortcuts.
/// </para>
/// </summary>
/// <param name="MaxWidth">Largest legal width (= the sensor width at the current binning).</param>
/// <param name="MaxHeight">Largest legal height (= the sensor height at the current binning).</param>
/// <param name="MinWidth">Smallest legal width.</param>
/// <param name="MinHeight">Smallest legal height.</param>
/// <param name="WidthStep">Width must be a multiple of this (ZWO: 8).</param>
/// <param name="HeightStep">Height must be a multiple of this (ZWO: 2).</param>
/// <param name="OriginStepX">Top-left X must be a multiple of this.</param>
/// <param name="OriginStepY">Top-left Y must be a multiple of this.</param>
public readonly record struct RoiConstraints(
    int MaxWidth,
    int MaxHeight,
    int MinWidth,
    int MinHeight,
    int WidthStep,
    int HeightStep,
    int OriginStepX,
    int OriginStepY)
{
    /// <summary>
    /// The unconstrained default for a sensor of the given (binned) dimensions: any rect of any size at any
    /// origin is legal (all steps = 1, min = 1, max = the sensor). The base
    /// <see cref="ICameraDriver.RoiConstraints"/> returns this; cameras with alignment rules override.
    /// </summary>
    public static RoiConstraints ForSensor(int sensorWidth, int sensorHeight)
        => new(
            MaxWidth: Math.Max(1, sensorWidth),
            MaxHeight: Math.Max(1, sensorHeight),
            MinWidth: 1,
            MinHeight: 1,
            WidthStep: 1,
            HeightStep: 1,
            OriginStepX: 1,
            OriginStepY: 1);

    /// <summary>Snaps an arbitrary width to a legal one: clamped to [<see cref="MinWidth"/>, <see cref="MaxWidth"/>] and rounded to a multiple of <see cref="WidthStep"/>.</summary>
    public int SnapWidth(int width) => SnapSize(width, MinWidth, MaxWidth, WidthStep);

    /// <summary>Snaps an arbitrary height to a legal one: clamped to [<see cref="MinHeight"/>, <see cref="MaxHeight"/>] and rounded to a multiple of <see cref="HeightStep"/>.</summary>
    public int SnapHeight(int height) => SnapSize(height, MinHeight, MaxHeight, HeightStep);

    /// <summary>
    /// Snaps an arbitrary rect to the nearest legal ROI: size first (so the window can fit), then the
    /// top-left snapped to its origin step and clamped so the whole window stays on the sensor.
    /// </summary>
    public RoiRect Snap(RoiRect rect)
    {
        var w = SnapWidth(rect.Width);
        var h = SnapHeight(rect.Height);
        var x = SnapOrigin(rect.X, w, OriginStepX, MaxWidth);
        var y = SnapOrigin(rect.Y, h, OriginStepY, MaxHeight);
        return new RoiRect(x, y, w, h);
    }

    // Round v down to a multiple of step within [min, max]. If rounding down falls below min, round the
    // min up to the next multiple instead (so the result is always a legal in-range multiple).
    private static int SnapSize(int v, int min, int max, int step)
    {
        step = Math.Max(1, step);
        var clamped = Math.Clamp(v, min, max);
        var snapped = clamped / step * step;
        if (snapped < min)
        {
            snapped = (min + step - 1) / step * step; // ceil(min/step)*step
        }
        // Never exceed max (the largest in-range multiple).
        var maxMultiple = max / step * step;
        return Math.Min(snapped, maxMultiple);
    }

    // Snap a top-left coordinate to its step and clamp so origin + size stays within [0, max].
    private static int SnapOrigin(int v, int size, int step, int max)
    {
        step = Math.Max(1, step);
        var maxStart = Math.Max(0, max - size);
        var clamped = Math.Clamp(v, 0, maxStart);
        var snapped = clamped / step * step; // snapping DOWN keeps it <= maxStart
        return Math.Min(snapped, maxStart);
    }
}
