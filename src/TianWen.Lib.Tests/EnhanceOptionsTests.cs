using Shouldly;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="EnhanceOptions.TryParse"/> -- the single source of truth for the
/// backend (<c>auto</c>/<c>rc</c>/<c>sas</c>) + per-product tuning parse shared by
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
    [InlineData("sas", EnhanceBackend.ForceSas)]
    [InlineData("SAS", EnhanceBackend.ForceSas)]
    public void TryParse_ValidBackend_ParsesAndHasNoTuningWhenOverridesNull(string? backend, EnhanceBackend expected)
    {
        var ok = EnhanceOptions.TryParse(backend, null, null, null, out var options, out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        options.Backend.ShouldBe(expected);
        options.Tuning.ShouldBeNull();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("rc_astro")]
    [InlineData("blurx")]
    public void TryParse_UnknownBackend_FailsWithErrorAndDefaultOptions(string backend)
    {
        var ok = EnhanceOptions.TryParse(backend, null, null, null, out var options, out var error);

        ok.ShouldBeFalse();
        var msg = error.ShouldNotBeNull();
        msg.ShouldContain(backend);
        options.ShouldBe(EnhanceOptions.Default);
    }

    [Fact]
    public void TryParse_AnyOverridePresent_BuildsTuning()
    {
        var ok = EnhanceOptions.TryParse("rc", 0.85f, null, null, out var options, out var error);

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
        var ok = EnhanceOptions.TryParse("auto", 0.7f, 0.5f, 3, out var options, out _);

        ok.ShouldBeTrue();
        var tuning = options.Tuning.ShouldNotBeNull();
        tuning.DeblurSharpen.ShouldBe(0.7f);
        tuning.DenoiseStrength.ShouldBe(0.5f);
        tuning.DenoiseIterations.ShouldBe(3);
    }
}
