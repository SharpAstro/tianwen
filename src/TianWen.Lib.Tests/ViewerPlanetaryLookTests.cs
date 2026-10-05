using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planet's colour look as a viewer control (#1277), through the host <c>tianwen-fits</c> runs: the tone popover's choice on a balanced
/// master on show, made by the routine <c>planetary-look</c> runs and given back on "True colour" with the file untouched; no choice on a
/// deep-sky frame; and the live stacked view drawing the look on its masters once a Derive has balanced them.
/// </summary>
[Collection("Viewer")]
public class ViewerPlanetaryLookTests
{
    private static readonly DateTimeOffset At = new DateTimeOffset(2024, 12, 15, 12, 56, 42, TimeSpan.Zero);

    [Theory(Timeout = 180_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheBoostedLookShowsTheMastersLookedCopyAndTrueColourGivesTheMasterBack(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var path = WriteBalancedJupiter(e2e.Folder, "2024-12-15-1256_7-Jupiter_sharpened.fits");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        await e2e.OpenAsync(path, ct);
        var master = e2e.Controller.Document.ShouldNotBeNull();

        e2e.Click(ToolbarAction.Tone);
        e2e.State.TonePopover.IsOpen.ShouldBeTrue();
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "LookBoosted" }, "the boosted look"));
        e2e.State.PlanetaryLook.ShouldBe(ColourLook.Boosted);
        await e2e.PumpUntilAsync(() => !ReferenceEquals(e2e.Controller.Document, master) && !e2e.Controller.IsPlanetaryLookPending,
            "the master's look", ct, untilTimeout: true);

        // What is shown is the routine planetary-look runs, on the master as it was read, adopted as any image is.
        var looked = e2e.Controller.Document.ShouldNotBeNull();
        e2e.State.PlanetaryLookNote.ShouldBeNull();
        var instant = PlanetaryBestStack.InstantOf(master.UnstretchedImage, epoch: null).ShouldNotBeNull();
        var (expected, refusal) = PlanetaryColourLook.OnMaster(master.UnstretchedImage, CatalogIndex.Jupiter, instant, ColourLook.Boosted);
        var expectedDocument = await AstroImageDocument.AdoptImageAsync(expected.ShouldNotBeNull(refusal), DebayerAlgorithm.None,
            cancellationToken: ct);
        Differing(looked.UnstretchedImage, expectedDocument.UnstretchedImage).ShouldBe(0, "the viewer's look is planetary-look's");
        Differing(looked.UnstretchedImage, master.UnstretchedImage).ShouldBeGreaterThan(0, "and it moves the colour");
        looked.FilePath.ShouldBe(path, "a Save of what is shown is named for the master");

        // True colour gives the master back at once, and boosted again is the copy already made.
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "LookTrueColour" }, "true colour"));
        e2e.Frame();
        ReferenceEquals(e2e.Controller.Document, master).ShouldBeTrue("true colour is the master itself");
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "LookBoosted" }, "the boosted look"));
        e2e.Frame();
        ReferenceEquals(e2e.Controller.Document, looked).ShouldBeTrue("the look is kept, not made again");
        e2e.Controller.IsPlanetaryLookPending.ShouldBeFalse();

        (await File.ReadAllBytesAsync(path, ct)).SequenceEqual(bytes).ShouldBeTrue("the file stays linear");
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ADeepSkyFrameOffersNoColourLook(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await e2e.OpenAsync(e2e.WriteColourFits("m42.fits"), ct);

        e2e.Click(ToolbarAction.Tone);
        e2e.State.TonePopover.IsOpen.ShouldBeTrue();
        e2e.Viewer.PaintedRegions().Any(r => r.Result is HitResult.ButtonHit { Action: "LookBoosted" or "LookTrueColour" })
            .ShouldBeFalse("a look is the planet's");
    }

    [Theory(Timeout = 240_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task TheLiveStackedViewDrawsTheLookOnTheMastersADeriveBalanced(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await ViewerWaveletDeriveTests.OpenStackedAsync(e2e, ct, dispersed: true);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);

        // The choice can be made before a Derive; nothing is balanced yet for it to go on.
        e2e.Click(ToolbarAction.Tone);
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "LookBoosted" }, "the boosted look"));
        var source = e2e.Controller.Source.ShouldBeOfType<LiveStackPreviewSource>();
        source.MastersLooked.ShouldBe(0, "nothing balanced yet to look");
        // Closed again before the panel's Derive is pressed: an open popover's backdrop takes a press outside it to close itself.
        e2e.Key(InputKey.Escape);
        e2e.State.TonePopover.IsOpen.ShouldBeFalse();

        ViewerWaveletDeriveTests.PressDerive(e2e);
        await e2e.PumpUntilAsync(() => e2e.State.WaveletDeriveNote is not null && !e2e.State.WaveletDeriving, "the derivation's answer", ct,
            untilTimeout: true);
        var note = e2e.State.WaveletDeriveNote.ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine(note);
        e2e.State.WaveletLimb.ShouldNotBeNull(note).Balance.ShouldNotBeNull("a colour Jupiter is balanced by its Derive");
        await e2e.PumpUntilAsync(() => source.MastersLooked > 0, "a master drawn with the look", ct, untilTimeout: true);

        // True colour: the masters drawn from then on are as the balance made them.
        e2e.Click(ToolbarAction.Tone);
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "LookTrueColour" }, "true colour"));
        e2e.Key(InputKey.Escape);
        var (looked, drawn) = (source.MastersLooked, source.MastersDrawnOutsideTheLimb);
        await e2e.PumpUntilAsync(() => source.MastersDrawnOutsideTheLimb > drawn + 2, "masters drawn in true colour", ct, untilTimeout: true);
        source.MastersLooked.ShouldBeLessThanOrEqualTo(looked + 1, "at most the master already under way when the choice changed");
    }

    // The rendered colour Jupiter written as the Best stack writes its sharpened master: float, its OBJECT and exposure, and CBALSAT.
    private static string WriteBalancedJupiter(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var cards = new Dictionary<string, (object Value, string Comment)>
        {
            ["CBALSAT"] = (PlanetaryColourBalance.DefaultSaturation, "colour balance: saturation (#1212)"),
        };
        PlanetaryColourReadingTests.RenderedJupiter(At, balanced: true).WriteToFitsFile(path, null, cards);
        return path;
    }

    private static int Differing(Image a, Image b)
    {
        a.ChannelCount.ShouldBe(b.ChannelCount);
        var differing = 0;
        for (var c = 0; c < a.ChannelCount; c++)
        {
            var x = a.GetChannelSpan(c);
            var y = b.GetChannelSpan(c);
            for (var i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i])
                {
                    differing++;
                }
            }
        }
        return differing;
    }
}
