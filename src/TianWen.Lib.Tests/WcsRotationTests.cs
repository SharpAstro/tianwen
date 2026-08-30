using Shouldly;
using System;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// P1 of the meridian-flip verification plan (docs/plans/meridian-flip-verification.md):
/// <see cref="WCS.RotationDeg"/> reads the field's position angle back out of a CD matrix, so a
/// plate solve the session already performs can say whether the field turned over.
/// </summary>
[Collection("Astrometry")]
public class WcsRotationTests
{
    private const double PixelScaleDeg = 1.0 / 3600.0; // 1"/px

    /// <summary>
    /// A CD matrix for a frame whose +Y axis sits at position angle <paramref name="paDeg"/> (north
    /// through east). Built as scale * rotation so the only thing under test is how
    /// <see cref="WCS.RotationDeg"/> reads it back.
    /// <para>
    /// <paramref name="mirrored"/> reverses the x axis, which is what an extra reflection in the
    /// optical train does. It must not move the answer.
    /// </para>
    /// </summary>
    private static WCS BuildRotated(double paDeg, bool mirrored = false)
    {
        var (sin, cos) = Math.SinCos(double.DegreesToRadians(paDeg));
        // +Y maps to (east, north) = scale * (sin PA, cos PA); +X is 90 degrees round from it, and
        // reversed for a mirrored train. East is -X in the conventional north-up sky orientation,
        // hence the leading minus on the unmirrored branch.
        var xSign = mirrored ? 1.0 : -1.0;
        return new WCS(5.5, 42.0)
        {
            CRPix1 = 512.5,
            CRPix2 = 384.5,
            CD1_1 = xSign * PixelScaleDeg * cos,
            CD2_1 = xSign * PixelScaleDeg * -sin,
            CD1_2 = PixelScaleDeg * sin,
            CD2_2 = PixelScaleDeg * cos
        };
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(90.0)]
    [InlineData(180.0)]
    [InlineData(273.4)]
    public void ARotatedFieldReportsItsPositionAngle(double paDeg)
    {
        BuildRotated(paDeg).RotationDeg.ShouldBe(paDeg, tolerance: 1e-9);
    }

    [Fact]
    public void ANorthUpFrameReadsZero()
    {
        // The convention worth pinning by name: a conventional north-up, east-left frame is 0, not
        // 180. A reader who takes the number at face value has to get this one right.
        BuildRotated(0.0).RotationDeg.ShouldBe(0.0, tolerance: 1e-9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(273.4)]
    public void AMirroredOpticalTrainDoesNotMoveTheAnswer(double paDeg)
    {
        // Parity independence is not a nicety here: it is what lets the flip check work on any rig
        // without knowing its mirror count. A flip is a rotation, never a reflection.
        BuildRotated(paDeg, mirrored: true).RotationDeg.ShouldBe(paDeg, tolerance: 1e-9);
    }

    [Fact]
    public void AFieldRotated180ReportsAHalfTurnFromItsOriginal()
    {
        var before = BuildRotated(15.0);
        var after = BuildRotated(15.0 + 180.0);

        Math.Abs(after.RotationDeltaDeg(before)).ShouldBe(180.0, tolerance: 1e-9,
            "this is the flip having physically happened");
        Math.Abs(after.RotationDeltaDeg(after)).ShouldBe(0.0, tolerance: 1e-9,
            "and this is it having been declined");
    }

    [Fact]
    public void TheDeltaTakesTheShortWayRound()
    {
        // 350 -> 10 is twenty degrees east, not three hundred and forty west. Reading it the long way
        // round would make a rig that barely moved look like it had flipped and back.
        BuildRotated(10.0).RotationDeltaDeg(BuildRotated(350.0)).ShouldBe(20.0, tolerance: 1e-9);
        BuildRotated(350.0).RotationDeltaDeg(BuildRotated(10.0)).ShouldBe(-20.0, tolerance: 1e-9);
    }

    [Fact]
    public void ACentreOnlySolutionHasNoRotationToReport()
    {
        // Centre coordinates alone carry no orientation, and answering 0 for "I do not know" would
        // read as north-up -- a flip check fed that would see a half turn out of nothing.
        var centreOnly = new WCS(5.5, 42.0);
        centreOnly.HasCDMatrix.ShouldBeFalse();
        double.IsNaN(centreOnly.RotationDeg).ShouldBeTrue();
        double.IsNaN(centreOnly.RotationDeltaDeg(BuildRotated(0.0))).ShouldBeTrue();
        double.IsNaN(BuildRotated(0.0).RotationDeltaDeg(centreOnly)).ShouldBeTrue();
    }
}
