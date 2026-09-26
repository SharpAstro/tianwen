using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Sequencing.PolarAlignment;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Polar alignment on the wire (P5 part 4 of docs/plans/hardware-in-the-server.md, #934): the WHOLE configuration a client
/// asks for, and everything the polar panel draws from, cross losslessly through the node's JSON, and a value the routine
/// does not have crosses as null and comes back as NaN, never as 0.
/// </summary>
public class PolarAlignmentDtoTests
{
    // Every field away from its default, so a field the DTO drops comes back as the default and fails.
    // A field added to PolarAlignmentConfiguration is added here too.
    private static readonly PolarAlignmentConfiguration EveryFieldSet = new PolarAlignmentConfiguration(
        ExposureRamp: [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1.5)],
        MinStarsForSolve: 33,
        RotationDeg: 60,
        SettleSeconds: 7.5,
        TargetAccuracyArcmin: 0.5,
        OnDone: PolarAlignmentOnDone.Park,
        SaveFrames: true,
        MaxFrame2Retries: 5,
        SmoothingWindow: 9,
        SettleSigmaArcmin: 0.25,
        RefineFullSolveInterval: 12,
        UseIncrementalSolver: false,
        ReferenceFrameAverages: 3,
        RotationMinStars: 61,
        RefineMinStars: 19);

    private static T RoundTrip<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(value, info), info).ShouldNotBeNull();

    [Fact]
    public void EveryFieldOfTheConfigurationCrosses()
    {
        var request = new PolarAlignmentRequestDto { OtaIndex = 1, UseGuider = true, Configuration = PolarAlignmentConfigDto.From(EveryFieldSet) };

        var back = RoundTrip(request, HostingJsonContext.Default.PolarAlignmentRequestDto);

        (back.OtaIndex, back.UseGuider).ShouldBe((1, true));
        var configuration = back.Configuration.ShouldNotBeNull().ToConfiguration();
        configuration.ExposureRamp.ShouldBe(EveryFieldSet.ExposureRamp);
        // The ramp compared above; a record struct compares an ImmutableArray by reference.
        (configuration with { ExposureRamp = EveryFieldSet.ExposureRamp }).ShouldBe(EveryFieldSet);
    }

    [Fact]
    public void AConfigurationACallerLeavesOutIsTheDefault()
    {
        var back = JsonSerializer.Deserialize("{\"otaIndex\":0,\"configuration\":{\"rotationDeg\":30}}", HostingJsonContext.Default.PolarAlignmentRequestDto)
            .ShouldNotBeNull();

        var configuration = back.Configuration.ShouldNotBeNull().ToConfiguration();
        configuration.RotationDeg.ShouldBe(30);
        configuration.ExposureRamp.ShouldBe(PolarAlignmentConfiguration.Default.ExposureRamp);
        (configuration with { ExposureRamp = PolarAlignmentConfiguration.Default.ExposureRamp, RotationDeg = PolarAlignmentConfiguration.Default.RotationDeg })
            .ShouldBe(PolarAlignmentConfiguration.Default);
    }

    [Fact]
    public void PhaseAAndARefinementTickCrossWhole()
    {
        var phaseA = new TwoFrameSolveResult(true, null, new Vec3(0.01, -0.02, 0.9997), 2.1e-3, -1.4e-3, 0.78, 0.7801,
            TimeSpan.FromMilliseconds(400), 71, 64, CommandedRotationRad: 0.7854, MeasuredRotationRad: 0.7849);
        var arrow = new PolarCorrectionArrow(2.5, 88.1, 2.7, 88.4);
        var overlay = new PolarOverlay(0, 90, 0.013, 89.991, 1.9, 89.87, [5f, 15f, 30f], 7.2, -4.8, Hemisphere.North, arrow, LSTHours: 21.25);
        var wcs = new WCS(2.1, 88.9) { CRPix1 = 1024.5, CRPix2 = 768.5, CD1_1 = -1.9e-4, CD1_2 = 2.0e-6, CD2_1 = -2.1e-6, CD2_2 = -1.9e-4 };
        var live = new LiveSolveResult(58, TimeSpan.FromMilliseconds(400), "/node/polar/0007.fits", 2.0e-3, -1.3e-3, 2.05e-3, -1.35e-3,
            IsSettled: true, IsAligned: false, ConsecutiveFailedSolves: 2, new Vec3(0.011, -0.021, 0.9996), overlay, wcs);
        var state = new PolarStateDto
        {
            Source = "OTA #1; Main",
            StatusMessage = "Refining at 400ms",
            Phase = PolarAlignmentPhase.Refining,
            Running = true,
            PhaseA = PolarPhaseADto.From(phaseA),
            LastSolve = PolarLiveSolveDto.From(live),
        };

        var back = RoundTrip(ResponseEnvelopeOf(state), HostingJsonContext.Default.ResponseEnvelopePolarStateDto).Response.ShouldNotBeNull();

        back.Phase.ShouldBe(PolarAlignmentPhase.Refining);
        back.PhaseA.ShouldNotBeNull().ToResult().ShouldBe(phaseA);
        var tick = back.LastSolve.ShouldNotBeNull().ToResult();
        var tickOverlay = tick.Overlay.ShouldNotBeNull();
        tickOverlay.RingRadiiArcmin.ShouldBe(overlay.RingRadiiArcmin);
        (tickOverlay with { RingRadiiArcmin = overlay.RingRadiiArcmin }).ShouldBe(overlay);
        var tickWcs = tick.Wcs.ShouldNotBeNull();
        (tickWcs.CenterRA, tickWcs.CenterDec, tickWcs.CRPix1, tickWcs.CD2_1).ShouldBe((wcs.CenterRA, wcs.CenterDec, wcs.CRPix1, wcs.CD2_1));
        (tick with { Overlay = live.Overlay, Wcs = live.Wcs }).ShouldBe(live);
    }

    [Fact]
    public void AValueTheRoutineDoesNotHaveCrossesAsNullAndComesBackAsNaN()
    {
        // No sidereal time for the meridian, and a Phase A that failed before it had an axis error.
        var overlay = new PolarOverlay(0, -90, 0, -90, 0, -89.9, [5f], double.NaN, double.NaN, Hemisphere.South);
        var failed = new TwoFrameSolveResult(false, "No exposure solved", default, double.NaN, double.NaN, 0, 0, TimeSpan.Zero, 0, 0);

        var overlayDto = PolarOverlayDto.From(overlay);
        var phaseADto = PolarPhaseADto.From(failed);
        overlayDto.LSTHours.ShouldBeNull();
        overlayDto.AzErrorArcmin.ShouldBeNull();
        phaseADto.AzErrorRad.ShouldBeNull();

        var json = JsonSerializer.Serialize(new PolarStateDto
        {
            Source = "OTA #1",
            StatusMessage = "No exposure solved",
            PhaseA = phaseADto,
            LastSolve = new PolarLiveSolveDto { Overlay = overlayDto },
        }, HostingJsonContext.Default.PolarStateDto);
        json.ShouldNotContain("NaN");

        var back = JsonSerializer.Deserialize(json, HostingJsonContext.Default.PolarStateDto).ShouldNotBeNull();
        double.IsNaN(back.LastSolve.ShouldNotBeNull().Overlay.ShouldNotBeNull().ToOverlay().LSTHours).ShouldBeTrue();
        double.IsNaN(back.PhaseA.ShouldNotBeNull().ToResult().AzErrorRad).ShouldBeTrue();
    }

    private static TianWen.Hosting.Api.ResponseEnvelope<PolarStateDto> ResponseEnvelopeOf(PolarStateDto state)
        => TianWen.Hosting.Api.ResponseEnvelope<PolarStateDto>.Ok(state);
}
