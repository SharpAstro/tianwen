using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// One guide correction: the error a guide frame measured and the pulse the guider answered it with.
/// Raised by <see cref="IGuider.GuideCorrectionEvent"/> once per guide frame the guider corrects on, on
/// the guider's own thread.
/// <para>
/// <b>Stamped with the guide FRAME's time, never with the time anyone read it.</b> A session that polled
/// the guider once per imaging tick stamped each sample with the poll, sampled one guide frame twice or
/// missed it, and so <c>GUIDEN</c> counted polls rather than guide frames (#821).
/// </para>
/// <para>
/// A null error is a correction whose error the guider did not measure (or cannot say), and a consumer
/// records nothing for it: null is not zero.
/// </para>
/// </summary>
/// <param name="frameTime">When the guide frame was taken: the middle of its exposure where the guider
/// knows it (the in-process guiders), else the time the guider itself reported the step (PHD2's
/// <c>GuideStep</c> <c>Timestamp</c>).</param>
/// <param name="raError">RA error in arcseconds, or null when it was not measured.</param>
/// <param name="decError">Dec error in arcseconds, or null when it was not measured.</param>
/// <param name="raCorrectionMs">RA correction pulse in ms (positive = West). 0 = no correction.</param>
/// <param name="decCorrectionMs">Dec correction pulse in ms (positive = North). 0 = no correction.</param>
public sealed class GuideCorrectionEventArgs(
    DateTimeOffset frameTime,
    double? raError,
    double? decError,
    double raCorrectionMs = 0,
    double decCorrectionMs = 0) : EventArgs
{
    public DateTimeOffset FrameTime { get; } = frameTime;

    public double? RaError { get; } = raError;

    public double? DecError { get; } = decError;

    public double RaCorrectionMs { get; } = raCorrectionMs;

    public double DecCorrectionMs { get; } = decCorrectionMs;
}
