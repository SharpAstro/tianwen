using Shouldly;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="EnhanceOptions.TryParse"/> -- the single source of truth for the
/// backend (<c>auto</c>/<c>rc</c>/<c>tianwen</c>) + per-product tuning parse shared by
/// <c>image sharpen</c>, <c>stack --enhance</c>, and the server <c>POST /api/v1/image/enhance</c>.
/// </summary>
public class EnhanceOptionsTests
{
    [Theory]
    [InlineData(null, EnhanceBackend.Auto)]
    [InlineData("", EnhanceBackend.Auto)]
    [InlineData("auto", EnhanceBackend.Auto)]
    [InlineData("AUTO", EnhanceBackend.Auto)]
    [InlineData("  Auto  ", EnhanceBackend.Auto)]
    [InlineData("rc", EnhanceBackend.ForceRcAstro)]
    [InlineData("rcastro", EnhanceBackend.ForceRcAstro)]
    [InlineData("rc-astro", EnhanceBackend.ForceRcAstro)]
    [InlineData("RC", EnhanceBackend.ForceRcAstro)]
    [InlineData("tianwen", EnhanceBackend.TianWen)]
    [InlineData("TianWen", EnhanceBackend.TianWen)]
    [InlineData("  tianwen ", EnhanceBackend.TianWen)]
    public void TryParse_ValidBackend_ParsesAndHasNoTuningWhenOverridesNull(string? backend, EnhanceBackend expected)
    {
        var ok = EnhanceOptions.TryParse(backend, null, null, null, null, out var options, out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        options.Backend.ShouldBe(expected);
        options.Tuning.ShouldBeNull();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("n2n")] // renamed 'tianwen' on 2026-10-09 with no alias (the owner: not that level of compatibility yet)
    [InlineData("rc_astro")]
    [InlineData("blurx")]
    public void TryParse_UnknownBackend_FailsWithErrorAndDefaultOptions(string backend)
    {
        var ok = EnhanceOptions.TryParse(backend, null, null, null, null, out var options, out var error);

        ok.ShouldBeFalse();
        var msg = error.ShouldNotBeNull();
        msg.ShouldContain(backend);
        options.ShouldBe(EnhanceOptions.Default);
    }

    /// <summary>
    /// The SETI Astro backend was removed on 2026-09-26 (its model licence allows use only within
    /// SASpro). A script written before then still says <c>sas</c>, so it gets a message that says
    /// what happened and what to use, never a silent Auto that would run something else.
    /// </summary>
    [Theory]
    [InlineData("sas")]
    [InlineData("SAS")]
    public void TryParse_TheRemovedSasBackend_FailsAndSaysWhy(string backend)
    {
        var ok = EnhanceOptions.TryParse(backend, null, null, null, null, out var options, out var error);

        ok.ShouldBeFalse();
        var msg = error.ShouldNotBeNull();
        msg.ShouldContain("removed");
        msg.ShouldContain("auto");
        options.ShouldBe(EnhanceOptions.Default);
    }

    [Fact]
    public void TryParse_AnyOverridePresent_BuildsTuning()
    {
        var ok = EnhanceOptions.TryParse("rc", 0.85f, null, null, null, out var options, out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        options.Backend.ShouldBe(EnhanceBackend.ForceRcAstro);
        var tuning = options.Tuning.ShouldNotBeNull();
        tuning.DeblurSharpen.ShouldBe(0.85f);
        tuning.DenoiseStrength.ShouldBeNull();
        tuning.DenoiseIterations.ShouldBeNull();
    }

    [Fact]
    public void TryParse_AllOverridesPresent_BuildsFullTuning()
    {
        var ok = EnhanceOptions.TryParse("auto", 0.7f, 0.5f, 3, null, out var options, out _);

        ok.ShouldBeTrue();
        var tuning = options.Tuning.ShouldNotBeNull();
        tuning.DeblurSharpen.ShouldBe(0.7f);
        tuning.DenoiseStrength.ShouldBe(0.5f);
        tuning.DenoiseIterations.ShouldBe(3);
    }

    [Fact]
    public void TryParse_AKernelAloneBuildsATuningThatCarriesIt()
    {
        DeconvolutionKernel.TryParse("0.77,0.91,0.98", null, null, out var kernel, out var kernelError).ShouldBeTrue(kernelError);

        var ok = EnhanceOptions.TryParse("tianwen", null, null, null, kernel, out var options, out var error);

        ok.ShouldBeTrue(error);
        options.Backend.ShouldBe(EnhanceBackend.TianWen);
        options.Tuning.ShouldNotBeNull().Deconvolution.ShouldBe(new DeconvolutionKernel(0.77, 0.91, 0.98));
    }

    [Theory]
    [InlineData("0.9", 0.9, 0.9, 0.9)]
    [InlineData(" 0.77 , 0.91 , 0.98 ", 0.77, 0.91, 0.98)]
    [InlineData("0", 0.0, 0.0, 0.0)]
    public void DeconvolutionKernel_OneWidthOrThree(string text, double red, double green, double blue)
    {
        DeconvolutionKernel.TryParse(text, null, null, out var kernel, out var error).ShouldBeTrue(error);

        var k = kernel.ShouldNotBeNull();
        (k.FwhmRed, k.FwhmGreen, k.FwhmBlue).ShouldBe((red, green, blue));
        k.Beta.ShouldBe(DeconvolutionKernel.DefaultBeta);
        k.Resample.ShouldBe(DeconvolutionKernel.DefaultResample);
    }

    [Fact]
    public void DeconvolutionKernel_NoneStatedIsNoKernelAndNoError()
    {
        DeconvolutionKernel.TryParse(null, null, null, out var kernel, out var error).ShouldBeTrue();
        kernel.ShouldBeNull();
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("0.8,0.9", null, null, "three")]
    [InlineData("wide", null, null, "not a width")]
    [InlineData("-0.5", null, null, "not a width")]
    [InlineData("NaN", null, null, "not a width")]
    [InlineData("0.9", 0.0, null, "beta")]
    [InlineData("0.9", null, 0.8, "resample")]
    [InlineData(null, 4.0, null, "without the kernel")]
    public void DeconvolutionKernel_RefusesWhatIsNotAKernel(string? text, double? beta, double? resample, string reason)
    {
        DeconvolutionKernel.TryParse(text, beta, resample, out var kernel, out var error).ShouldBeFalse();
        kernel.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain(reason);
    }
}
