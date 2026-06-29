using System.Threading.Tasks;
using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.AI.Imaging.Onnx;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the RC-Astro NDJSON parser, the sxt-&gt;<see cref="IStarRemover"/>
/// present+licensed selector, and an end-to-end star-removal smoke test (gated
/// on the CLI being installed and sxt licensed; skips silently otherwise).
/// </summary>
[Collection("Imaging")]
public class RcAstroStarRemoverTests(ITestOutputHelper output)
{
    [Fact]
    public void NdjsonParser_ReadsDeviceProgressAndError()
    {
        RcAstroEvent.TryParse("""{"event":"status","phase":"initializing","message":"Initializing"}""")
            .ShouldNotBeNull().Kind.ShouldBe("status");

        var device = RcAstroEvent.TryParse(
            """{"event":"device","device":"gpu","name":"Adreno X1-85","provider":"DirectML","runtime":"onnxruntime 1.23.2"}""")
            .ShouldNotBeNull();
        device.Kind.ShouldBe("device");
        device.Device.ShouldBe("gpu");
        device.Provider.ShouldBe("DirectML");
        device.DeviceName.ShouldBe("Adreno X1-85");

        var progress = RcAstroEvent.TryParse(
            """{"event":"progress","done":42.5,"mpPerSec":3.4,"eta":7.8}""")
            .ShouldNotBeNull();
        progress.Done.ShouldBe(42.5);
        progress.MpPerSec.ShouldBe(3.4);
        progress.Eta.ShouldBe(7.8);

        RcAstroEvent.TryParse("""{"event":"error","message":"output file already exists"}""")
            .ShouldNotBeNull().Message.ShouldBe("output file already exists");
    }

    [Fact]
    public void NdjsonParser_ToleratesNonEventAndNonJsonLines()
    {
        RcAstroEvent.TryParse("Error: unknown product 'zzz'").ShouldBeNull();
        RcAstroEvent.TryParse("""{"foo":1}""").ShouldBeNull();
        RcAstroEvent.TryParse("").ShouldBeNull();
        // Unknown event kind still parses (forward-compatibility): kind preserved.
        RcAstroEvent.TryParse("""{"event":"future","x":1}""").ShouldNotBeNull().Kind.ShouldBe("future");
    }

    [Fact]
    public void AddRcAstroAi_ResolvesDeferredProxy_ThenSelectsByLicenseOnFirstUse()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddRcAstroAi();

        using var provider = services.BuildServiceProvider();
        // Resolution returns the deferred proxy and spawns NO rc-astro process;
        // if it had probed/selected eagerly the type would be a concrete backend.
        var deferred = provider.GetRequiredService<IStarRemover>().ShouldBeOfType<DeferredStarRemover>();

        // Forcing the backend makes the (cached) selection; assert which won.
        if (RcAstroTestSupport.ProductAvailable("sxt", out _))
        {
            deferred.Backend.ShouldBeOfType<RcAstroStarRemover>();
        }
        else
        {
            deferred.Backend.ShouldBeOfType<OnnxStarRemover>();
        }
    }

    [Fact]
    public async Task EnhanceAsync_RemovesStars_RoundTripsThroughFits()
    {
        if (!RcAstroTestSupport.ProductAvailable("sxt", out var skip)) { Assert.Skip(skip); return; }

        const int w = 256, h = 256;
        var src = RcAstroTestSupport.BuildRgbWithStars(w, h);
        var brightInput = RcAstroTestSupport.CountBrightPixels(src, 0.5f);

        using var factory = LoggerFactory.Create(b => b.AddProvider(new XUnitLoggerProvider(output, appendScope: false)));
        var enhancer = new RcAstroStarRemover(new RcAstroCli(factory.CreateLogger<RcAstroCli>()), factory.CreateLogger<RcAstroStarRemover>());

        var result = await enhancer.EnhanceAsync(src, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        var (channels, outW, outH) = result.Shape;
        channels.ShouldBe(3);
        outW.ShouldBe(w);
        outH.ShouldBe(h);

        var brightOutput = RcAstroTestSupport.CountBrightPixels(result, 0.5f);
        output.WriteLine($"bright pixels (>0.5): input={brightInput} output={brightOutput}");
        brightOutput.ShouldBeLessThan(brightInput / 2);
        RcAstroTestSupport.AllFinite(result).ShouldBeTrue();

        result.Release();
    }
}
