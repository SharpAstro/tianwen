using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>Which actuator(s) a <see cref="PlanetaryRecenterController"/> frame chose.</summary>
public enum RecenterActuator
{
    /// <summary>The disk is within the deadband (or nothing is actionable); hold position.</summary>
    None,
    /// <summary>Pan the readout window (the fast, mount-free recenter).</summary>
    Roi,
    /// <summary>Nudge the mount (the coarse fallback when the ROI is at the sensor edge / can't jog).</summary>
    Mount,
    /// <summary>One axis jogs the ROI while the other (edge-blocked) axis nudges the mount.</summary>
    Both
}

/// <summary>
/// Tuning for the planetary centre-of-mass (COM) recenter loop. All distances are in unbinned sensor
/// pixels of the streamed frame.
/// </summary>
/// <param name="DeadbandPixels">Hold position while the COM is within this distance of the frame centre
/// (suppresses jitter / seeing-driven centroid wander).</param>
/// <param name="Gain">Fraction of the measured offset corrected per frame (0..1]. A value below 1 damps
/// the loop so a noisy single-frame COM can't overshoot; the residual is corrected over the next frames.</param>
/// <param name="EdgeMarginFraction">The ROI counts as "at the edge" on an axis when its origin is within
/// this fraction of the axis's pan range of either end. At the edge, correction on that axis hands off to
/// the mount (when enabled) instead of jogging the ROI further into a clamp.</param>
/// <param name="MaxRoiStepPixels">Clamp on a single-frame ROI jog, so one bad COM can't fling the window.</param>
/// <param name="MountJogEnabled">Opt-in: allow the coarse mount nudge when the ROI is edge-blocked. Off by
/// default -- ROI jog is the zero-disturbance primary.</param>
/// <param name="PixelScaleArcsec">Arcsec per pixel (from the OTA focal length + camera pixel size), used to
/// size the mount nudge. <see cref="double.NaN"/> / non-positive disables the mount path regardless of
/// <paramref name="MountJogEnabled"/> (we can't size a pulse without a scale).</param>
/// <param name="MaxMountArcsec">Cap on a single mount nudge per axis. The mount sign is uncalibrated (see
/// <paramref name="FlipRa"/> / <paramref name="FlipDec"/>), so a wrong guess only moves the field by this
/// bounded amount, never a runaway slew -- the user watches and flips / nudges manually.</param>
/// <param name="FlipRa">Invert the X-pixel -> RA-direction mapping (the on-sky orientation depends on camera
/// rotation + pier side, which this loop does not solve for).</param>
/// <param name="FlipDec">Invert the Y-pixel -> Dec-direction mapping.</param>
public readonly record struct RecenterOptions(
    double DeadbandPixels = 4.0,
    double Gain = 0.5,
    double EdgeMarginFraction = 0.08,
    int MaxRoiStepPixels = 80,
    bool MountJogEnabled = false,
    double PixelScaleArcsec = double.NaN,
    double MaxMountArcsec = 60.0,
    bool FlipRa = false,
    bool FlipDec = false);

/// <summary>
/// The recenter decision for one frame. The controller is <b>pure</b> -- it never touches a driver; the
/// caller actuates whatever is populated: a non-zero <see cref="RoiDx"/>/<see cref="RoiDy"/> through
/// <c>IVideoCameraDriver.JogRoiAsync</c>, and a non-zero <see cref="MountRaArcsec"/>/<see cref="MountDecArcsec"/>
/// through a pulse-guide. Both can be set in the same frame (one axis jogs the ROI, the edge-blocked axis
/// nudges the mount). <see cref="OffsetX"/>/<see cref="OffsetY"/> are always populated (telemetry).
/// </summary>
/// <param name="Actuator">A summary of which actuator(s) this frame engaged (for telemetry / UI).</param>
/// <param name="RoiDx">ROI pan in X (sensor px), or 0. Positive shifts the window toward larger X, which
/// moves a right-of-centre disk back toward the frame centre.</param>
/// <param name="RoiDy">ROI pan in Y (sensor px), or 0.</param>
/// <param name="MountRaArcsec">Signed RA nudge in arcsec, or 0. &gt; 0 = the caller pulses East, &lt; 0 = West
/// (subject to <see cref="RecenterOptions.FlipRa"/>).</param>
/// <param name="MountDecArcsec">Signed Dec nudge in arcsec, or 0. &gt; 0 = North, &lt; 0 = South (subject to
/// <see cref="RecenterOptions.FlipDec"/>).</param>
/// <param name="OffsetX">Measured COM offset from the frame centre in X (px). Telemetry; always set.</param>
/// <param name="OffsetY">Measured COM offset from the frame centre in Y (px). Telemetry; always set.</param>
public readonly record struct RecenterDecision(
    RecenterActuator Actuator,
    int RoiDx,
    int RoiDy,
    double MountRaArcsec,
    double MountDecArcsec,
    double OffsetX,
    double OffsetY);

/// <summary>
/// Pure controller for the planetary <b>centre-of-mass recenter loop</b> (Phase C of the live planetary
/// plan). Given a measured disk centre of mass and the current readout-window geometry, it decides how to
/// pull the planet back to the frame centre:
/// <list type="bullet">
///   <item><b>ROI jog (auto, primary)</b> -- shift the readout window toward the disk. Instant, zero mount
///   disturbance. The workhorse.</item>
///   <item><b>Mount jog (opt-in, coarse fallback)</b> -- when the ROI is at the sensor edge (or the camera
///   can't ROI-jog), nudge the mount so the planet comes back toward sensor centre and the ROI can resume.</item>
/// </list>
/// It holds no state and touches no hardware: feed it the COM (from <see cref="PlanetaryDisk.CenterOfMass"/>)
/// and the geometry, act on the returned <see cref="RecenterDecision"/>. This keeps the loop deterministic
/// and unit-testable; the driver calls live in the capture controller.
/// </summary>
public static class PlanetaryRecenterController
{
    /// <summary>
    /// Decide the recenter action for one frame.
    /// </summary>
    /// <param name="centerOfMass">Disk centre of mass in frame pixel coordinates (e.g. from
    /// <see cref="PlanetaryDisk.CenterOfMass"/>).</param>
    /// <param name="frameWidth">Streamed frame width (= the ROI width) in px.</param>
    /// <param name="frameHeight">Streamed frame height (= the ROI height) in px.</param>
    /// <param name="roi">The current readout window on the sensor (origin + size).</param>
    /// <param name="sensorWidth">Full sensor width in px (the ROI pan range is <c>sensorWidth - roi.Width</c>).</param>
    /// <param name="sensorHeight">Full sensor height in px.</param>
    /// <param name="canJogRoi">Whether the camera can pan the readout window mid-stream. When false, only the
    /// mount path is available.</param>
    /// <param name="options">Loop tuning.</param>
    public static RecenterDecision Decide(
        (double X, double Y) centerOfMass,
        int frameWidth,
        int frameHeight,
        RoiRect roi,
        int sensorWidth,
        int sensorHeight,
        bool canJogRoi,
        in RecenterOptions options)
    {
        var offsetX = centerOfMass.X - (frameWidth / 2.0);
        var offsetY = centerOfMass.Y - (frameHeight / 2.0);

        // Per-axis deadband -> hold an axis whose offset is within it. Per-axis (not distance) so a large
        // offset on one axis never drives noise-corrections on the other, otherwise-centred axis. Telemetry
        // offsets are always reported. Both within deadband -> hold entirely.
        var deadband = Math.Max(0.0, options.DeadbandPixels);
        var actX = Math.Abs(offsetX) > deadband;
        var actY = Math.Abs(offsetY) > deadband;
        if (!actX && !actY)
        {
            return new RecenterDecision(RecenterActuator.None, 0, 0, 0.0, 0.0, offsetX, offsetY);
        }

        // Desired ROI jog: move the window toward the disk by a damped fraction of the offset. A positive
        // offset (disk right/below centre) needs a positive jog (window follows), which shifts the disk back.
        var gain = Math.Clamp(options.Gain, 0.0, 1.0);
        var maxStep = Math.Max(1, options.MaxRoiStepPixels);
        var wantDx = actX ? Math.Clamp((int)Math.Round(offsetX * gain), -maxStep, maxStep) : 0;
        var wantDy = actY ? Math.Clamp((int)Math.Round(offsetY * gain), -maxStep, maxStep) : 0;

        // Pan range on each axis and the edge margin within it.
        var maxStartX = Math.Max(0, sensorWidth - roi.Width);
        var maxStartY = Math.Max(0, sensorHeight - roi.Height);
        var marginX = (int)Math.Round(Math.Clamp(options.EdgeMarginFraction, 0.0, 0.5) * maxStartX);
        var marginY = (int)Math.Round(Math.Clamp(options.EdgeMarginFraction, 0.0, 0.5) * maxStartY);

        // An axis is "blocked" when it wants to jog but cannot: the camera can't ROI-jog, the axis has no pan
        // range, or the window is already within the edge margin in the direction it needs to move.
        var blockedX = wantDx != 0 && (!canJogRoi || maxStartX == 0
            || (wantDx > 0 && roi.X >= maxStartX - marginX)
            || (wantDx < 0 && roi.X <= marginX));
        var blockedY = wantDy != 0 && (!canJogRoi || maxStartY == 0
            || (wantDy > 0 && roi.Y >= maxStartY - marginY)
            || (wantDy < 0 && roi.Y <= marginY));

        var roiDx = blockedX ? 0 : wantDx;
        var roiDy = blockedY ? 0 : wantDy;

        // Mount handoff for the blocked axes (opt-in + a usable pixel scale). Sized to the FULL offset on that
        // axis (the mount re-points the whole field), capped, and sign subject to the uncalibrated flip flags.
        double raArcsec = 0.0, decArcsec = 0.0;
        var scale = options.PixelScaleArcsec;
        var mountUsable = options.MountJogEnabled && double.IsFinite(scale) && scale > 0.0;
        if (mountUsable)
        {
            var cap = Math.Max(0.0, options.MaxMountArcsec);
            if (blockedX)
            {
                var ra = (options.FlipRa ? -offsetX : offsetX) * scale;
                raArcsec = Math.Clamp(ra, -cap, cap);
            }

            if (blockedY)
            {
                var dec = (options.FlipDec ? -offsetY : offsetY) * scale;
                decArcsec = Math.Clamp(dec, -cap, cap);
            }
        }

        var jogged = roiDx != 0 || roiDy != 0;
        var nudged = raArcsec != 0.0 || decArcsec != 0.0;
        var actuator = (jogged, nudged) switch
        {
            (true, true) => RecenterActuator.Both,
            (true, false) => RecenterActuator.Roi,
            (false, true) => RecenterActuator.Mount,
            _ => RecenterActuator.None
        };

        return new RecenterDecision(actuator, roiDx, roiDy, raArcsec, decArcsec, offsetX, offsetY);
    }
}
