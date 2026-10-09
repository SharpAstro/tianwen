using System.Text.Json;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The hosted enhance request on the wire (#1401): every field, the deconvolution kernel's three among them, crosses the
/// node's JSON and back, and a body a client writes by hand in camelCase reaches the same parsers the CLI uses as the
/// kernel it states. The endpoint's 400 for a kernel the parser refuses is a functional test
/// (<c>HostingApiTests.ImageEnhance_WithABadDeconvolutionKernel_ReturnsBadRequestNamingIt</c>).
/// </summary>
public class EnhanceRequestDtoTests
{
    [Fact]
    public void EveryFieldCrossesTheWireAndBack()
    {
        // Every field away from its default, so a field the context drops comes back as the default and fails.
        var sent = new EnhanceRequestDto
        {
            InputPath = "/data/master.fits",
            OutputPath = "/data/master_deblurred.fits",
            Backend = "tianwen",
            DeblurSharpen = 0.4f,
            DenoiseStrength = 0.7f,
            DenoiseIterations = 3,
            DeconvKernel = "0.77,0.91,0.98",
            DeconvBeta = 3.5,
            DeconvResample = 1.5,
        };

        var json = JsonSerializer.Serialize(sent, HostingJsonContext.Default.EnhanceRequestDto);
        var back = JsonSerializer.Deserialize(json, HostingJsonContext.Default.EnhanceRequestDto).ShouldNotBeNull();

        back.InputPath.ShouldBe(sent.InputPath);
        back.OutputPath.ShouldBe(sent.OutputPath);
        back.Backend.ShouldBe(sent.Backend);
        back.DeblurSharpen.ShouldBe(sent.DeblurSharpen);
        back.DenoiseStrength.ShouldBe(sent.DenoiseStrength);
        back.DenoiseIterations.ShouldBe(sent.DenoiseIterations);
        back.DeconvKernel.ShouldBe(sent.DeconvKernel);
        back.DeconvBeta.ShouldBe(sent.DeconvBeta);
        back.DeconvResample.ShouldBe(sent.DeconvResample);
    }

    [Fact]
    public void AHandWrittenBodyStatesTheKernelTheParsersRead()
    {
        var request = JsonSerializer.Deserialize(
            """{"inputPath":"/data/master.fits","backend":"tianwen","deconvKernel":"0.77,0.91,0.98","deconvBeta":3.5,"deconvResample":1.5}""",
            HostingJsonContext.Default.EnhanceRequestDto).ShouldNotBeNull();

        DeconvolutionKernel.TryParse(request.DeconvKernel, request.DeconvBeta, request.DeconvResample, out var kernel, out var error)
            .ShouldBeTrue(error);
        EnhanceOptions.TryParse(request.Backend, request.DeblurSharpen, request.DenoiseStrength, request.DenoiseIterations, kernel, out var options, out error)
            .ShouldBeTrue(error);

        options.Backend.ShouldBe(EnhanceBackend.TianWen);
        options.Tuning.ShouldNotBeNull().Deconvolution.ShouldBe(new DeconvolutionKernel(0.77, 0.91, 0.98, 3.5, 1.5));
    }

    [Fact]
    public void ABodyWithNoKernelStatesNone()
    {
        var request = JsonSerializer.Deserialize("""{"inputPath":"/data/master.fits","backend":"tianwen"}""", HostingJsonContext.Default.EnhanceRequestDto)
            .ShouldNotBeNull();

        request.DeconvKernel.ShouldBeNull();
        request.DeconvBeta.ShouldBeNull();
        request.DeconvResample.ShouldBeNull();
        DeconvolutionKernel.TryParse(request.DeconvKernel, request.DeconvBeta, request.DeconvResample, out var kernel, out var error).ShouldBeTrue(error);
        kernel.ShouldBeNull();
    }
}
