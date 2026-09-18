using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.ColorCalibration;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a colour calibration reports about itself. The white-balance popover shows the multipliers on its
/// sliders, so it takes the provenance alone, in lines short enough never to be cut.
/// </summary>
public sealed class ColorCalibrationSummaryTests
{
    [Fact]
    public void TheProvenanceIsTheMethodItsStarsAndItsWhiteReferenceWithoutTheTriple()
    {
        var spcc = new ColorCalibrationSummary("SPCC", 0.671f, 1f, 1.605f, 22, "Average spiral galaxy (SWIRE Sb)");

        spcc.ProvenanceLines().ToArray().ShouldBe(["SPCC, 22 stars", "White: Average spiral galaxy (SWIRE Sb)"]);
        spcc.Describe().ShouldContain("R=0.671", Case.Sensitive, "the one-line form keeps the triple for a tooltip");
    }

    [Fact]
    public void AMethodWithNoStarsAndNoWhiteReferenceIsOneLine()
        => new ColorCalibrationSummary("Sky background", 1.1f, 1f, 0.9f, StarCount: 0, WhiteReference: null)
            .ProvenanceLines().ToArray().ShouldBe(["Sky background"]);
}
