using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A refused RC-Astro licence is asked again, never remembered, and says why (2026-10-23: every probe in a
/// viewer exited 77 once, the process cached the refusal, and each enhance after it ran the whole-frame
/// program with no star split until the viewer restarted, while the status line said only "whole frame").
/// </summary>
public class RcAstroLicenseProbeTests
{
    private const string RefusedLine = "License check failed";

    /// <summary>A probe that answers from a script, one answer per call, and counts its calls.</summary>
    private sealed class ScriptedProbe(params RcAstroCli.LicenseProbe[] answers)
    {
        public int Calls { get; private set; }

        public RcAstroCli.LicenseProbe Ask(string exePath, string productKey)
            => answers[Math.Min(Calls++, answers.Length - 1)];
    }

    private static readonly RcAstroCli.LicenseProbe Granted = new(true, 0, "Permanently licensed");
    private static readonly RcAstroCli.LicenseProbe Refused = new(false, 77, RefusedLine);

    private sealed class NamedEnhancer(string name) : IImageEnhancer
    {
        public string Name => name;
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default) => Task.FromResult(input);
    }

    [Fact]
    public void ARefusalIsAskedAgainAndALaterGrantIsUsed()
    {
        var probe = new ScriptedProbe(Refused, Granted);
        var cli = new RcAstroCli("/fake/rc-astro", probe.Ask);

        cli.IsLicensed("sxt").ShouldBeFalse();
        cli.LicenseRefusal("sxt").ShouldBe($"its licence check exited 77: {RefusedLine}");

        cli.IsLicensed("sxt").ShouldBeTrue();
        cli.LicenseRefusal("sxt").ShouldBeNull();
        probe.Calls.ShouldBe(2);
    }

    [Fact]
    public void AGrantIsProbedOnce()
    {
        var probe = new ScriptedProbe(Granted, Refused);
        var cli = new RcAstroCli("/fake/rc-astro", probe.Ask);

        cli.IsLicensed("bxt").ShouldBeTrue();
        cli.IsLicensed("bxt").ShouldBeTrue();
        probe.Calls.ShouldBe(1);
    }

    [Fact]
    public void WithNoExecutableNothingIsProbedAndTheRefusalSaysSo()
    {
        var probe = new ScriptedProbe(Granted);
        var cli = new RcAstroCli(null, probe.Ask);

        cli.IsLicensed("nxt").ShouldBeFalse();
        cli.LicenseRefusal("nxt").ShouldBe("RC-Astro is not installed");
        probe.Calls.ShouldBe(0);
    }

    [Fact]
    public void ARefusedStarRemoverNamesItselfUnderAutoOnly()
    {
        var cli = new RcAstroCli("/fake/rc-astro", new ScriptedProbe(Refused).Ask);
        var remover = new DeferredStarRemover(cli, () => new RcAstroStarRemover(cli));

        remover.CanServe(3, EnhanceOptions.Default).ShouldBeFalse();
        remover.Refusal(EnhanceOptions.Default).ShouldBe($"RC-Astro StarXTerminator (sxt): its licence check exited 77: {RefusedLine}");
        remover.Refusal(new EnhanceOptions(EnhanceBackend.TianWen)).ShouldBeNull();
    }

    [Fact]
    public void ThePipelineNamesEachRefusedProductOnce()
    {
        var cli = new RcAstroCli("/fake/rc-astro", new ScriptedProbe(Refused).Ask);
        var pipeline = new SharpenPipeline(
            starRemover: new DeferredStarRemover(cli, () => new NamedEnhancer("RC-Astro StarXTerminator (sxt)")),
            nonStellarDeconvolver: new DeferredNonStellarDeconvolver(cli, () => new NamedEnhancer("RC-Astro BlurXTerminator (bxt)")),
            deblurrer: new DeferredDeblurrer(cli, () => new NamedEnhancer("RC-Astro BlurXTerminator (bxt)"), () => new NamedEnhancer("in-house")));

        // BlurXTerminator serves two roles and is named once.
        pipeline.RefusalsFor(EnhanceOptions.Default).ShouldBe(
            [
                $"RC-Astro BlurXTerminator (bxt): its licence check exited 77: {RefusedLine}",
                $"RC-Astro StarXTerminator (sxt): its licence check exited 77: {RefusedLine}",
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void ThePipelineReportsNothingWhenEveryProductIsGranted()
    {
        var cli = new RcAstroCli("/fake/rc-astro", new ScriptedProbe(Granted).Ask);
        var pipeline = new SharpenPipeline(starRemover: new DeferredStarRemover(cli, () => new NamedEnhancer("sxt")));

        pipeline.RefusalsFor(EnhanceOptions.Default).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("banner\nStarXTerminator: Permanently licensed\n", "StarXTerminator: Permanently licensed")]
    [InlineData("one line", "one line")]
    [InlineData("first\r\n  last  \r\n\r\n", "last")]
    [InlineData("  \n ", null)]
    public void TheStatusLineIsTheLastNonBlankLine(string text, string? expected)
        => RcAstroCli.LastLine(text).ShouldBe(expected);
}
