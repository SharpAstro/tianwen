using Shouldly;
using System;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// P2 of the meridian-flip verification plan (docs/plans/meridian-flip-verification.md): reading a
/// flip off the pair of plate solves the session already takes.
/// </summary>
public class MeridianFlipVerificationTests
{
    private const double PixelScaleDeg = 1.0 / 3600.0;

    /// <summary>A solved field whose +Y axis sits at position angle <paramref name="paDeg"/>.</summary>
    private static WCS SolvedAt(double paDeg)
    {
        var (sin, cos) = Math.SinCos(double.DegreesToRadians(paDeg));
        return new WCS(4.89, 20.0)
        {
            CRPix1 = 512.5,
            CRPix2 = 384.5,
            CD1_1 = -PixelScaleDeg * cos,
            CD2_1 = PixelScaleDeg * sin,
            CD1_2 = PixelScaleDeg * sin,
            CD2_2 = PixelScaleDeg * cos
        };
    }

    [Theory]
    [InlineData(0.0, 180.0)]
    [InlineData(37.5, 217.5)]
    [InlineData(300.0, 120.0)]   // wraps through 360
    [InlineData(0.0, 174.0)]     // a mount whose flip lands a few degrees out is still a flip
    public void AHalfTurnIsTheTubeGoingOver(double beforePa, double afterPa)
    {
        var verdict = MeridianFlipVerification.FromSolves(SolvedAt(beforePa), SolvedAt(afterPa));
        verdict.Evidence.ShouldBe(FlipEvidence.Flipped);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(37.5, 37.5)]
    [InlineData(0.0, 358.0)]     // solve noise, not a flip
    public void NoRotationIsTheTubeStayingPut(double beforePa, double afterPa)
    {
        var verdict = MeridianFlipVerification.FromSolves(SolvedAt(beforePa), SolvedAt(afterPa));
        verdict.Evidence.ShouldBe(FlipEvidence.NotFlipped);
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(45.0)]
    [InlineData(135.0)]
    public void AnAngleAPierFlipCannotProduceIsNotRoundedToTheNearerAnswer(double afterPa)
    {
        // A quarter turn is a rotator that moved, a bad solve, or a pair drawn from two cameras.
        // Calling it "nearly nothing" or "nearly a flip" would be inventing a fact; the honest
        // answer sends the caller back to the mount's own report.
        var verdict = MeridianFlipVerification.FromSolves(SolvedAt(0.0), SolvedAt(afterPa));
        verdict.Evidence.ShouldBe(FlipEvidence.Inconclusive);
        double.IsNaN(verdict.RotationDeltaDeg).ShouldBeFalse(
            "an unclassifiable rotation was still MEASURED, and the number belongs in the log");
    }

    [Fact]
    public void ACentreOnlySolveCarriesNoOrientationAndSaysNothing()
    {
        // The fallback path of a solver that could not fit a plate, and the shape FakePlateSolver
        // returns without a catalog. Answering NotFlipped here would fail every flip on such a rig.
        var centreOnly = new WCS(4.89, 20.0);

        MeridianFlipVerification.FromSolves(centreOnly, SolvedAt(180.0)).Evidence.ShouldBe(FlipEvidence.Inconclusive);
        MeridianFlipVerification.FromSolves(SolvedAt(0.0), centreOnly).Evidence.ShouldBe(FlipEvidence.Inconclusive);
    }

    [Fact]
    public void NoPriorSolveSaysNothing()
    {
        // First observation of the night, or a centering that never converged: there is no "before".
        MeridianFlipVerification.FromSolves(null, SolvedAt(180.0)).Evidence.ShouldBe(FlipEvidence.Inconclusive);
        MeridianFlipVerification.FromSolves(SolvedAt(0.0), null).Evidence.ShouldBe(FlipEvidence.Inconclusive);
        MeridianFlipVerification.FromSolves(null, null).Evidence.ShouldBe(FlipEvidence.Inconclusive);
    }

    [Fact]
    public void TheMeasuredRotationIsReportedAlongsideTheVerdict()
    {
        var verdict = MeridianFlipVerification.FromSolves(SolvedAt(10.0), SolvedAt(190.0));
        verdict.Evidence.ShouldBe(FlipEvidence.Flipped);
        Math.Abs(verdict.RotationDeltaDeg).ShouldBe(180.0, tolerance: 1e-9);
    }

    [Fact]
    public void TheDefaultIsInconclusive()
    {
        // MeridianFlipResult carries a FlipVerdict with a default value on paths that never ran the
        // check; that default must be the answer that changes nothing, never "the tube stayed put".
        default(FlipVerdict).Evidence.ShouldBe(FlipEvidence.Inconclusive);
        FlipVerdict.Inconclusive.Evidence.ShouldBe(FlipEvidence.Inconclusive);
    }
}
