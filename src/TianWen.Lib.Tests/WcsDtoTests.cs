using System.Text.Json;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A plate solution crosses the wire whole (P5 part 3 of docs/plans/hardware-in-the-server.md, #934): the reference
/// point, the CD matrix and the SIP polynomials come back as they went, through the wire's own JSON context, and a value
/// the solution does not have crosses as null, never 0.
/// </summary>
public class WcsDtoTests
{
    private static WCS Solved() => new WCS(5.588, -5.39)
    {
        CRPix1 = 2071.5,
        CRPix2 = 1410.5,
        CD1_1 = -2.1e-4,
        CD1_2 = 3.0e-6,
        CD2_1 = -2.9e-6,
        CD2_2 = -2.1e-4,
        SipOrder = 2,
        SipA = new double[,] { { 0, 0, 1.5e-7 }, { 0, -2.0e-8, 0 }, { 4.0e-7, 0, 0 } },
        SipB = new double[,] { { 0, 0, -3.0e-7 }, { 0, 1.0e-8, 0 }, { 2.5e-7, 0, 0 } },
    };

    private static PlateSolutionDto RoundTrip(PlateSolutionDto solution)
    {
        var json = JsonSerializer.Serialize(ResponseEnvelope<PlateSolutionDto>.Ok(solution), HostingJsonContext.Default.ResponseEnvelopePlateSolutionDto);
        return JsonSerializer.Deserialize(json, HostingJsonContext.Default.ResponseEnvelopePlateSolutionDto).ShouldNotBeNull().Response.ShouldNotBeNull();
    }

    [Fact]
    public void ASolutionComesBackWholeSipIncluded()
    {
        var sent = Solved();

        var back = RoundTrip(new PlateSolutionDto { FrameNumber = 7, Solved = true, Message = "Solved", Solution = WcsDto.From(sent) })
            .Solution.ShouldNotBeNull().ToWcs();

        (back.CenterRA, back.CenterDec, back.CRPix1, back.CRPix2).ShouldBe((sent.CenterRA, sent.CenterDec, sent.CRPix1, sent.CRPix2));
        (back.CD1_1, back.CD1_2, back.CD2_1, back.CD2_2).ShouldBe((sent.CD1_1, sent.CD1_2, sent.CD2_1, sent.CD2_2));
        back.SipOrder.ShouldBe(2);
        back.HasSip.ShouldBeTrue();
        back.SipA.ShouldBe(sent.SipA);
        back.SipB.ShouldBe(sent.SipB);
        back.SipAP.ShouldBeNull();

        // What a client draws with: the same sky under the same pixel.
        back.PixelToSky(100, 200).ShouldBe(sent.PixelToSky(100, 200));
    }

    [Fact]
    public void AValueTheSolutionDoesNotHaveCrossesAsNullAndComesBackAsNaN()
    {
        var centreOnly = new WCS(12.0, 45.0);

        var dto = WcsDto.From(centreOnly);
        dto.CD1_1.ShouldBeNull("NaN crosses as null, never 0");
        dto.SipA.ShouldBeNull();

        var back = RoundTrip(new PlateSolutionDto { Message = "Solved", Solution = dto }).Solution.ShouldNotBeNull().ToWcs();
        double.IsNaN(back.CD1_1).ShouldBeTrue();
        double.IsNaN(back.CRPix1).ShouldBeTrue();
        back.HasSip.ShouldBeFalse();
    }
}
