using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The live view's Derive (#1159) through the host <c>tianwen-fits</c> runs: a SER's stacked view (K), the Derive button pressed, and the
/// derived sharpening's gains worked out in the background and seeded into the wavelet dials, or the reason none were said in the panel.
/// The gains themselves are pinned in Lib (<c>PlanetarySharpeningTests</c>, the seeded sliders against the batch's derived sharpening);
/// this is the wiring the GUI's planetary capture shares (<c>WaveletDerivation</c>).
/// </summary>
[Collection("Viewer")]
public class ViewerWaveletDeriveTests
{
    [Theory(Timeout = 180_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task DeriveWithNoApertureSaysItNeedsOneAndLeavesTheDialsAlone(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await OpenStackedAsync(e2e, ct);
        e2e.State.PlanetaryApertureMm = null;
        var before = e2e.State.WaveletGains;

        PressDerive(e2e);
        await e2e.PumpUntilAsync(() => e2e.State.WaveletDeriveNote is not null && !e2e.State.WaveletDeriving, "the derivation's answer", ct);

        e2e.State.WaveletDeriveNote.ShouldNotBeNull().ShouldContain("aperture");
        e2e.State.WaveletDerived.ShouldBeFalse();
        e2e.State.WaveletGains.ShouldBe(before);
    }

    [Theory(Timeout = 180_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task DeriveWithThePlanetAndTheTelescopeSeedsTheDialsWithTheDerivedGains(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await OpenStackedAsync(e2e, ct);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);

        PressDerive(e2e);
        e2e.State.WaveletDeriving.ShouldBeTrue("the derivation runs in the background, and the button says so");
        await e2e.PumpUntilAsync(() => e2e.State.WaveletDeriveNote is not null && !e2e.State.WaveletDeriving, "the derivation's answer", ct);

        var note = e2e.State.WaveletDeriveNote.ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine($"{note}: {string.Join(", ", e2e.State.WaveletGains)}");
        note.ShouldStartWith("Gains derived for Jupiter");
        e2e.State.WaveletDerived.ShouldBeTrue();
        e2e.State.WaveletSharpenEnabled.ShouldBeTrue("seeding the dials turns the sharpening on");
        e2e.State.WaveletGains.ShouldNotBe(WaveletSharpenOptions.PlanetaryDefault.Gains);
        e2e.State.BuildWaveletOptions().ShouldNotBeNull().HoldAtDarkest.ShouldBeTrue("derived gains sharpen as the derived sharpening does");
        var limb = e2e.State.WaveletLimb.ShouldNotBeNull("Derive keeps the limb it fitted (#1201)");

        // Every master the dials sharpen from here is drawn outside that limb as the batch draws it.
        var source = e2e.Controller.Source.ShouldBeOfType<LiveStackPreviewSource>();
        await e2e.PumpUntilAsync(() => source.MastersDrawnOutsideTheLimb > 0, "a master drawn outside the kept limb", ct);

        // Reset puts the preset back and forgets the derivation, but keeps the limb, which is the capture's: hand-set dials are drawn
        // outside it too.
        var drawnBefore = source.MastersDrawnOutsideTheLimb;
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "WaveletReset" }, "the Reset button"));
        e2e.State.WaveletDerived.ShouldBeFalse();
        e2e.State.WaveletDeriveNote.ShouldBeNull();
        e2e.State.WaveletGains.ShouldBe(WaveletSharpenOptions.PlanetaryDefault.Gains);
        e2e.State.WaveletLimb.ShouldBeSameAs(limb);
        await e2e.PumpUntilAsync(() => source.MastersDrawnOutsideTheLimb > drawnBefore, "the preset's master drawn outside the kept limb", ct);
    }

    [Fact]
    public void ADerivationDisposedTwiceStaysQuietAndTicksNoMore()
    {
        // A host disposes its capture controller itself and through its service scope, and the second dispose's cancel threw on the
        // disposed source (the functional suite's GUI harness, on #1183's CI).
        var derivation = new WaveletDerivation();
        var state = new ViewerState { WaveletDeriveRequested = true };
        derivation.Dispose();
        Should.NotThrow(derivation.Dispose);

        derivation.Tick(state, source: null, capturePath: null, DateTimeOffset.UnixEpoch, NullLogger.Instance);
        state.WaveletDeriveRequested.ShouldBeTrue("a disposed derivation takes no request");
        state.WaveletDeriveNote.ShouldBeNull();
    }

    // The synthetic Jupiter capture opened and its stacked view on screen with a master in it.
    private static async Task OpenStackedAsync(ViewerE2E e2e, System.Threading.CancellationToken ct)
    {
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);
        e2e.Key(InputKey.K);
        await e2e.PumpUntilAsync(() => e2e.State.ShowStacked && e2e.Controller.Source is LiveStackPreviewSource { HasMaster: true },
            "the stacked view's first master", ct);
    }

    // The panel's own Derive button, pressed where it is drawn.
    private static void PressDerive(ViewerE2E e2e)
    {
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "WaveletDerive" }, "the Derive button"));
        e2e.Frame();
    }
}
