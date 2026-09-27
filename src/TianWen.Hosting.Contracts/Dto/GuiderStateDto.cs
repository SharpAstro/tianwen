using System;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

public sealed class GuiderStateDto
{
    public string? State { get; init; }
    public double? TotalRMS { get; init; }
    public double? RaRMS { get; init; }
    public double? DecRMS { get; init; }
    public double? PeakRa { get; init; }
    public double? PeakDec { get; init; }
    public required double GuideExposureSeconds { get; init; }
    public required ImmutableArray<GuideStepDto> RecentSteps { get; init; }

    /// <summary>
    /// Guide star position in guide-frame pixels, and its SNR. Null when nothing is being tracked.
    /// <para>
    /// These are what turn the guide preview into a guider view: without the position the crosshair has
    /// nowhere to go, and the SNR is the number that says whether a drifting graph means seeing or a
    /// star about to be lost. Deliberately NOT <c>required</c> -- a nullable wire member that is also
    /// required cannot round-trip, because the writer omits it when null.
    /// </para>
    /// </summary>
    public double? GuideStarX { get; init; }

    /// <inheritdoc cref="GuideStarX"/>
    public double? GuideStarY { get; init; }

    /// <inheritdoc cref="GuideStarX"/>
    public double? GuideStarSNR { get; init; }

    /// <summary>
    /// Change token for the guide-camera preview, so a client polling <c>/preview/guider</c> can decide
    /// from a state poll it was making anyway whether to refetch. Mirrors <c>CameraStateDto</c>'s frame
    /// number serving the per-OTA previews.
    /// </summary>
    public required int GuideFrameNumber { get; init; }

    /// <summary>
    /// The last step's errors and pulses, which the guider tab's readouts show (P5b part 3). Null before a step, and
    /// with no stats.
    /// </summary>
    public double? LastRaErr { get; init; }

    /// <inheritdoc cref="LastRaErr"/>
    public double? LastDecErr { get; init; }

    /// <inheritdoc cref="LastRaErr"/>
    public double? LastRaPulseMs { get; init; }

    /// <inheritdoc cref="LastRaErr"/>
    public double? LastDecPulseMs { get; init; }

    /// <summary>The settle in progress, which a dither or a recovery waits on; null when none has been started.</summary>
    public SettleProgressDto? Settle { get; init; }

    /// <summary>
    /// The guide star's horizontal and vertical profiles through its centroid, drawn beside the guide frame; empty when
    /// nothing is being tracked.
    /// </summary>
    public ImmutableArray<float> StarProfileH { get; set; } = [];

    /// <inheritdoc cref="StarProfileH"/>
    public ImmutableArray<float> StarProfileV { get; set; } = [];

    /// <summary>The last calibration's steps, drawn as the L over the guide frame; null before one.</summary>
    public CalibrationOverlayDto? Calibration { get; init; }

    /// <summary>Projects the guider slice. <see cref="ISessionTelemetry"/> for the same reason as
    /// <see cref="SessionStateDto.FromSession"/>.</summary>
    public static GuiderStateDto FromSession(ISessionTelemetry session)
    {
        var stats = session.LastGuideStats;
        var steps = ImmutableArray.CreateBuilder<GuideStepDto>(session.GuideSamples.Length);
        foreach (var s in session.GuideSamples)
        {
            steps.Add(new GuideStepDto
            {
                Timestamp = s.Timestamp,
                RaError = JsonNumber.OrNull(s.RaError),
                DecError = JsonNumber.OrNull(s.DecError),
                RaCorrectionMs = JsonNumber.OrNull(s.RaCorrectionMs),
                DecCorrectionMs = JsonNumber.OrNull(s.DecCorrectionMs),
                IsDither = s.IsDither,
                IsSettling = s.IsSettling,
            });
        }

        return new GuiderStateDto
        {
            State = session.GuiderState,
            // Null with no stats, and null for a figure no sample has been folded into yet (NaN): not known
            // is never 0 on the native wire.
            TotalRMS = JsonNumber.OrNull(stats?.TotalRMS ?? double.NaN),
            RaRMS = JsonNumber.OrNull(stats?.RaRMS ?? double.NaN),
            DecRMS = JsonNumber.OrNull(stats?.DecRMS ?? double.NaN),
            PeakRa = JsonNumber.OrNull(stats?.PeakRa ?? double.NaN),
            PeakDec = JsonNumber.OrNull(stats?.PeakDec ?? double.NaN),
            GuideExposureSeconds = session.GuideExposure.TotalSeconds,
            RecentSteps = steps.MoveToImmutable(),
            // A centroid on a frame with no star can come back non-finite, which is not known, so null: one NaN
            // reaching the writer would be a bodiless 500 for the WHOLE state response, not just this field.
            GuideStarX = session.GuideStarPosition is { } p ? JsonNumber.OrNull(p.X) : null,
            GuideStarY = session.GuideStarPosition is { } q ? JsonNumber.OrNull(q.Y) : null,
            GuideStarSNR = session.GuideStarSNR is { } snr ? JsonNumber.OrNull(snr) : null,
            GuideFrameNumber = session.LastGuideFrameNumber,
            LastRaErr = stats?.LastRaErr is { } raErr ? JsonNumber.OrNull(raErr) : null,
            LastDecErr = stats?.LastDecErr is { } decErr ? JsonNumber.OrNull(decErr) : null,
            LastRaPulseMs = stats?.LastRaPulseMs is { } raPulse ? JsonNumber.OrNull(raPulse) : null,
            LastDecPulseMs = stats?.LastDecPulseMs is { } decPulse ? JsonNumber.OrNull(decPulse) : null,
            Settle = session.GuiderSettleProgress is { } settle ? SettleProgressDto.From(settle) : null,
            StarProfileH = session.GuideStarProfile is { } profile ? [.. profile.H] : [],
            StarProfileV = session.GuideStarProfile is { } profileV ? [.. profileV.V] : [],
            Calibration = session.CalibrationOverlay is { } overlay ? CalibrationOverlayDto.From(overlay) : null,
        };
    }
}

/// <summary>A guider's settle on the wire (<see cref="SettleProgress"/>).</summary>
public sealed class SettleProgressDto
{
    public required bool Done { get; init; }
    public double? Distance { get; init; }
    public double? SettlePx { get; init; }
    public double? Time { get; init; }
    public double? SettleTime { get; init; }
    public required int Status { get; init; }
    public string? Error { get; init; }
    public required bool StarLocked { get; init; }

    public static SettleProgressDto From(SettleProgress settle) => new()
    {
        Done = settle.Done,
        Distance = JsonNumber.OrNull(settle.Distance),
        SettlePx = JsonNumber.OrNull(settle.SettlePx),
        Time = JsonNumber.OrNull(settle.Time),
        SettleTime = JsonNumber.OrNull(settle.SettleTime),
        Status = settle.Status,
        Error = settle.Error,
        StarLocked = settle.StarLocked,
    };

    public SettleProgress ToSettle() => SettleProgress.Of(Done, JsonNumber.FromWire(Distance), JsonNumber.FromWire(SettlePx),
        JsonNumber.FromWire(Time), JsonNumber.FromWire(SettleTime), Status, Error, StarLocked);
}

/// <summary>A guider calibration's overlay on the wire (<see cref="CalibrationOverlayData"/>): absolute image pixels.</summary>
public sealed class CalibrationOverlayDto
{
    public required CalibrationPointDto RaOrigin { get; init; }
    public required CalibrationPointDto DecOrigin { get; init; }
    public ImmutableArray<CalibrationPointDto> RaSteps { get; set; } = [];
    public ImmutableArray<CalibrationPointDto> DecSteps { get; set; } = [];
    public double? PixelScaleArcsec { get; init; }
    public double? CameraAngleRad { get; init; }
    public double? RaRateArcsecPerSec { get; init; }
    public double? DecRateArcsecPerSec { get; init; }
    public int BacklashClearingStepsRa { get; init; }
    public int BacklashClearingStepsDec { get; init; }

    public static CalibrationOverlayDto From(CalibrationOverlayData overlay) => new()
    {
        RaOrigin = CalibrationPointDto.From(overlay.RaOrigin),
        DecOrigin = CalibrationPointDto.From(overlay.DecOrigin),
        RaSteps = [.. overlay.RaSteps.Select(CalibrationPointDto.From)],
        DecSteps = [.. overlay.DecSteps.Select(CalibrationPointDto.From)],
        PixelScaleArcsec = JsonNumber.OrNull(overlay.PixelScaleArcsec),
        CameraAngleRad = JsonNumber.OrNull(overlay.CameraAngleRad),
        RaRateArcsecPerSec = JsonNumber.OrNull(overlay.RaRateArcsecPerSec),
        DecRateArcsecPerSec = JsonNumber.OrNull(overlay.DecRateArcsecPerSec),
        BacklashClearingStepsRa = overlay.BacklashClearingStepsRa,
        BacklashClearingStepsDec = overlay.BacklashClearingStepsDec,
    };

    public CalibrationOverlayData ToOverlay() => new CalibrationOverlayData(
        RaOrigin.ToStep(), DecOrigin.ToStep(),
        [.. RaSteps.Select(p => p.ToStep())], [.. DecSteps.Select(p => p.ToStep())],
        JsonNumber.FromWire(PixelScaleArcsec), JsonNumber.FromWire(CameraAngleRad),
        JsonNumber.FromWire(RaRateArcsecPerSec), JsonNumber.FromWire(DecRateArcsecPerSec),
        BacklashClearingStepsRa, BacklashClearingStepsDec);
}

/// <summary>One position of a calibration's star, in absolute image pixels.</summary>
public sealed class CalibrationPointDto
{
    public double? X { get; init; }
    public double? Y { get; init; }

    public static CalibrationPointDto From(CalibrationStep step) => new() { X = JsonNumber.OrNull(step.X), Y = JsonNumber.OrNull(step.Y) };

    public CalibrationStep ToStep() => new CalibrationStep(JsonNumber.FromWire(X), JsonNumber.FromWire(Y));
}

public sealed class GuideStepDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public double? RaError { get; init; }
    public double? DecError { get; init; }
    public double? RaCorrectionMs { get; init; }
    public double? DecCorrectionMs { get; init; }
    public required bool IsDither { get; init; }
    public required bool IsSettling { get; init; }
}
